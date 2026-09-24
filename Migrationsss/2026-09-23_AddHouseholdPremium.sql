/*
   Household-scoped Premium subscriptions.

   Households own the entitlement. Users inherit it through an active
   HouseholdMembers row, so adding or removing a family member does not
   require copying subscription state between user records.

   This migration is additive and safe to run more than once.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.Households', N'PremiumStatus') IS NULL
    EXEC(N'ALTER TABLE dbo.Households ADD PremiumStatus nvarchar(20) NOT NULL
        CONSTRAINT DF_Households_PremiumStatus DEFAULT (N''free'')');

IF COL_LENGTH(N'dbo.Households', N'StripeCustomerID') IS NULL
    EXEC(N'ALTER TABLE dbo.Households ADD StripeCustomerID nvarchar(255) NULL');

IF COL_LENGTH(N'dbo.Households', N'StripeSubscriptionID') IS NULL
    EXEC(N'ALTER TABLE dbo.Households ADD StripeSubscriptionID nvarchar(255) NULL');

IF COL_LENGTH(N'dbo.Households', N'PremiumCurrentPeriodEndUtc') IS NULL
    EXEC(N'ALTER TABLE dbo.Households ADD PremiumCurrentPeriodEndUtc datetime2(0) NULL');

IF COL_LENGTH(N'dbo.Households', N'PremiumCancelAtPeriodEnd') IS NULL
    EXEC(N'ALTER TABLE dbo.Households ADD PremiumCancelAtPeriodEnd bit NOT NULL
        CONSTRAINT DF_Households_PremiumCancelAtPeriodEnd DEFAULT (0)');

IF COL_LENGTH(N'dbo.Households', N'PremiumUpdatedAtUtc') IS NULL
    EXEC(N'ALTER TABLE dbo.Households ADD PremiumUpdatedAtUtc datetime2(0) NULL');

IF COL_LENGTH(N'dbo.Households', N'PremiumLastStripeEventCreatedUtc') IS NULL
    EXEC(N'ALTER TABLE dbo.Households ADD PremiumLastStripeEventCreatedUtc datetime2(0) NULL');

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Households') AND name = N'UX_Households_StripeCustomerID'
)
    EXEC(N'CREATE UNIQUE INDEX UX_Households_StripeCustomerID
        ON dbo.Households(StripeCustomerID)
        WHERE StripeCustomerID IS NOT NULL');

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Households') AND name = N'UX_Households_StripeSubscriptionID'
)
    EXEC(N'CREATE UNIQUE INDEX UX_Households_StripeSubscriptionID
        ON dbo.Households(StripeSubscriptionID)
        WHERE StripeSubscriptionID IS NOT NULL');

IF OBJECT_ID(N'dbo.HouseholdPremiumTransactions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HouseholdPremiumTransactions
    (
        HouseholdPremiumTransactionID bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_HouseholdPremiumTransactions PRIMARY KEY,
        HouseholdID                 int NOT NULL,
        UserID                      int NULL,
        StripeEventID               nvarchar(255) NOT NULL,
        StripeEventType             nvarchar(100) NOT NULL,
        StripeCheckoutSessionID     nvarchar(255) NULL,
        StripeCustomerID            nvarchar(255) NULL,
        StripeSubscriptionID        nvarchar(255) NULL,
        StripeInvoiceID             nvarchar(255) NULL,
        StripePaymentIntentID       nvarchar(255) NULL,
        AmountMinor                 bigint NULL,
        Currency                    nvarchar(3) NULL,
        TransactionStatus           nvarchar(40) NOT NULL,
        EventCreatedAtUtc           datetime2(0) NOT NULL,
        RecordedAtUtc               datetime2(0) NOT NULL
            CONSTRAINT DF_HouseholdPremiumTransactions_RecordedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_HouseholdPremiumTransactions_Households
            FOREIGN KEY (HouseholdID) REFERENCES dbo.Households(HouseholdID),
        CONSTRAINT FK_HouseholdPremiumTransactions_Users
            FOREIGN KEY (UserID) REFERENCES dbo.Users(userID)
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.HouseholdPremiumTransactions') AND name = N'UX_HouseholdPremiumTransactions_StripeEventID'
)
    EXEC(N'CREATE UNIQUE INDEX UX_HouseholdPremiumTransactions_StripeEventID
        ON dbo.HouseholdPremiumTransactions(StripeEventID)');

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.HouseholdPremiumTransactions') AND name = N'IX_HouseholdPremiumTransactions_Household_Date'
)
    EXEC(N'CREATE INDEX IX_HouseholdPremiumTransactions_Household_Date
        ON dbo.HouseholdPremiumTransactions(HouseholdID, EventCreatedAtUtc DESC)');

COMMIT TRANSACTION;
GO

/*
   Atomically records one signed Stripe event and updates the household's
   current entitlement. Stripe retries are ignored by StripeEventID. An older
   event cannot overwrite a newer entitlement snapshot.
*/
CREATE OR ALTER PROCEDURE dbo.ApplyHouseholdPremiumStripeEvent
    @HouseholdID                 int,
    @UserID                      int = NULL,
    @StripeEventID               nvarchar(255),
    @StripeEventType             nvarchar(100),
    @StripeCheckoutSessionID     nvarchar(255) = NULL,
    @StripeCustomerID            nvarchar(255) = NULL,
    @StripeSubscriptionID        nvarchar(255) = NULL,
    @StripeInvoiceID             nvarchar(255) = NULL,
    @StripePaymentIntentID       nvarchar(255) = NULL,
    @AmountMinor                 bigint = NULL,
    @Currency                    nvarchar(3) = NULL,
    @TransactionStatus           nvarchar(40),
    @EventCreatedAtUtc           datetime2(0),
    @EntitlementStatus           nvarchar(20) = NULL,
    @CurrentPeriodEndUtc         datetime2(0) = NULL,
    @CancelAtPeriodEnd           bit = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

    BEGIN TRANSACTION;

    IF EXISTS
    (
        SELECT 1
        FROM dbo.HouseholdPremiumTransactions WITH (UPDLOCK, HOLDLOCK)
        WHERE StripeEventID = @StripeEventID
    )
    BEGIN
        COMMIT TRANSACTION;
        SELECT CAST(0 AS bit) AS Applied;
        RETURN;
    END;

    INSERT dbo.HouseholdPremiumTransactions
    (
        HouseholdID, UserID, StripeEventID, StripeEventType,
        StripeCheckoutSessionID, StripeCustomerID, StripeSubscriptionID,
        StripeInvoiceID, StripePaymentIntentID, AmountMinor, Currency,
        TransactionStatus, EventCreatedAtUtc
    )
    VALUES
    (
        @HouseholdID, @UserID, @StripeEventID, @StripeEventType,
        @StripeCheckoutSessionID, @StripeCustomerID, @StripeSubscriptionID,
        @StripeInvoiceID, @StripePaymentIntentID, @AmountMinor, @Currency,
        @TransactionStatus, @EventCreatedAtUtc
    );

    UPDATE h
    SET StripeCustomerID = COALESCE(@StripeCustomerID, h.StripeCustomerID),
        StripeSubscriptionID = COALESCE(@StripeSubscriptionID, h.StripeSubscriptionID),
        PremiumStatus = COALESCE(@EntitlementStatus, h.PremiumStatus),
        PremiumCurrentPeriodEndUtc = CASE
            WHEN @EntitlementStatus IS NULL THEN h.PremiumCurrentPeriodEndUtc
            ELSE @CurrentPeriodEndUtc
        END,
        PremiumCancelAtPeriodEnd = COALESCE(@CancelAtPeriodEnd, h.PremiumCancelAtPeriodEnd),
        PremiumUpdatedAtUtc = SYSUTCDATETIME(),
        PremiumLastStripeEventCreatedUtc = CASE
            WHEN @EntitlementStatus IS NULL OR @CurrentPeriodEndUtc IS NULL
                THEN h.PremiumLastStripeEventCreatedUtc
            ELSE @EventCreatedAtUtc
        END
    FROM dbo.Households h
    WHERE h.HouseholdID = @HouseholdID
      AND (@EntitlementStatus IS NULL
           OR (@CurrentPeriodEndUtc IS NULL AND h.PremiumStatus = N'free')
           OR h.PremiumLastStripeEventCreatedUtc IS NULL
           OR h.PremiumLastStripeEventCreatedUtc <= @EventCreatedAtUtc);

    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Applied;
END;
GO
