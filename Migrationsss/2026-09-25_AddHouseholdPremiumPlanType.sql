/*
   Household-scoped Premium subscriptions.

   Households own the entitlement. Users inherit it through an active
   HouseholdMembers row, so adding or removing a family member does not
   require copying subscription state between user records.

   PremiumPlanType values:
     free
     monthly
     yearly
     lifetime

   Stripe remains the billing source of truth.
   This table stores the application's current entitlement snapshot.

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
    EXEC(N'
        ALTER TABLE dbo.Households
        ADD PremiumStatus nvarchar(20) NOT NULL
        CONSTRAINT DF_Households_PremiumStatus DEFAULT (N''free'')
    ');

IF COL_LENGTH(N'dbo.Households', N'PremiumPlanType') IS NULL
    EXEC(N'
        ALTER TABLE dbo.Households
        ADD PremiumPlanType nvarchar(20) NOT NULL
        CONSTRAINT DF_Households_PremiumPlanType DEFAULT (N''free'')
    ');

IF COL_LENGTH(N'dbo.Households', N'StripeCustomerID') IS NULL
    EXEC(N'
        ALTER TABLE dbo.Households
        ADD StripeCustomerID nvarchar(255) NULL
    ');

IF COL_LENGTH(N'dbo.Households', N'StripeSubscriptionID') IS NULL
    EXEC(N'
        ALTER TABLE dbo.Households
        ADD StripeSubscriptionID nvarchar(255) NULL
    ');

IF COL_LENGTH(N'dbo.Households', N'PremiumCurrentPeriodEndUtc') IS NULL
    EXEC(N'
        ALTER TABLE dbo.Households
        ADD PremiumCurrentPeriodEndUtc datetime2(0) NULL
    ');

IF COL_LENGTH(N'dbo.Households', N'PremiumCancelAtPeriodEnd') IS NULL
    EXEC(N'
        ALTER TABLE dbo.Households
        ADD PremiumCancelAtPeriodEnd bit NOT NULL
        CONSTRAINT DF_Households_PremiumCancelAtPeriodEnd DEFAULT (0)
    ');

IF COL_LENGTH(N'dbo.Households', N'PremiumUpdatedAtUtc') IS NULL
    EXEC(N'
        ALTER TABLE dbo.Households
        ADD PremiumUpdatedAtUtc datetime2(0) NULL
    ');

IF COL_LENGTH(N'dbo.Households', N'PremiumLastStripeEventCreatedUtc') IS NULL
    EXEC(N'
        ALTER TABLE dbo.Households
        ADD PremiumLastStripeEventCreatedUtc datetime2(0) NULL
    ');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Households')
      AND name = N'UX_Households_StripeCustomerID'
)
    EXEC(N'
        CREATE UNIQUE INDEX UX_Households_StripeCustomerID
        ON dbo.Households(StripeCustomerID)
        WHERE StripeCustomerID IS NOT NULL
    ');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Households')
      AND name = N'UX_Households_StripeSubscriptionID'
)
    EXEC(N'
        CREATE UNIQUE INDEX UX_Households_StripeSubscriptionID
        ON dbo.Households(StripeSubscriptionID)
        WHERE StripeSubscriptionID IS NOT NULL
    ');

IF OBJECT_ID(N'dbo.HouseholdPremiumTransactions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HouseholdPremiumTransactions
    (
        HouseholdPremiumTransactionID bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_HouseholdPremiumTransactions PRIMARY KEY,

        HouseholdID int NOT NULL,
        UserID int NULL,

        PremiumPlanType nvarchar(20) NULL,

        StripeEventID nvarchar(255) NOT NULL,
        StripeEventType nvarchar(100) NOT NULL,

        StripeCheckoutSessionID nvarchar(255) NULL,
        StripeCustomerID nvarchar(255) NULL,
        StripeSubscriptionID nvarchar(255) NULL,
        StripeInvoiceID nvarchar(255) NULL,
        StripePaymentIntentID nvarchar(255) NULL,

        AmountMinor bigint NULL,
        Currency nvarchar(3) NULL,

        TransactionStatus nvarchar(40) NOT NULL,

        EventCreatedAtUtc datetime2(0) NOT NULL,

        RecordedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_HouseholdPremiumTransactions_RecordedAtUtc
            DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT FK_HouseholdPremiumTransactions_Households
            FOREIGN KEY (HouseholdID)
            REFERENCES dbo.Households(HouseholdID),

        CONSTRAINT FK_HouseholdPremiumTransactions_Users
            FOREIGN KEY (UserID)
            REFERENCES dbo.Users(userID)
    );
END;

-- Handles databases where the transaction table already exists.
IF COL_LENGTH(N'dbo.HouseholdPremiumTransactions', N'PremiumPlanType') IS NULL
    EXEC(N'
        ALTER TABLE dbo.HouseholdPremiumTransactions
        ADD PremiumPlanType nvarchar(20) NULL
    ');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.HouseholdPremiumTransactions')
      AND name = N'UX_HouseholdPremiumTransactions_StripeEventID'
)
    EXEC(N'
        CREATE UNIQUE INDEX UX_HouseholdPremiumTransactions_StripeEventID
        ON dbo.HouseholdPremiumTransactions(StripeEventID)
    ');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.HouseholdPremiumTransactions')
      AND name = N'IX_HouseholdPremiumTransactions_Household_Date'
)
    EXEC(N'
        CREATE INDEX IX_HouseholdPremiumTransactions_Household_Date
        ON dbo.HouseholdPremiumTransactions
           (HouseholdID, EventCreatedAtUtc DESC)
    ');

COMMIT TRANSACTION;
GO


/*
   Atomically records one signed Stripe event and updates the household's
   current premium entitlement.

   Stripe retries are ignored by StripeEventID.

   Older entitlement events cannot overwrite a newer entitlement snapshot.

   Expected PremiumPlanType values:
     free
     monthly
     yearly
     lifetime
*/

CREATE OR ALTER PROCEDURE dbo.ApplyHouseholdPremiumStripeEvent
    @HouseholdID                 int,
    @UserID                      int = NULL,

    @PremiumPlanType             nvarchar(20) = NULL,

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

    /*
       Normalize/validate plan values.

       NULL means:
       "this Stripe event does not change the household plan type."
    */
    IF @PremiumPlanType IS NOT NULL
       AND @PremiumPlanType NOT IN
       (
           N'free',
           N'monthly',
           N'yearly',
           N'lifetime'
       )
    BEGIN
        THROW 51001, 'Invalid PremiumPlanType.', 1;
    END;

    BEGIN TRANSACTION;

    /*
       Stripe may retry webhooks.

       StripeEventID acts as our idempotency key so the same event
       is not processed twice.
    */
    IF EXISTS
    (
        SELECT 1
        FROM dbo.HouseholdPremiumTransactions
             WITH (UPDLOCK, HOLDLOCK)
        WHERE StripeEventID = @StripeEventID
    )
    BEGIN
        COMMIT TRANSACTION;

        SELECT CAST(0 AS bit) AS Applied;
        RETURN;
    END;

    INSERT dbo.HouseholdPremiumTransactions
    (
        HouseholdID,
        UserID,
        PremiumPlanType,

        StripeEventID,
        StripeEventType,

        StripeCheckoutSessionID,
        StripeCustomerID,
        StripeSubscriptionID,
        StripeInvoiceID,
        StripePaymentIntentID,

        AmountMinor,
        Currency,

        TransactionStatus,
        EventCreatedAtUtc
    )
    VALUES
    (
        @HouseholdID,
        @UserID,
        @PremiumPlanType,

        @StripeEventID,
        @StripeEventType,

        @StripeCheckoutSessionID,
        @StripeCustomerID,
        @StripeSubscriptionID,
        @StripeInvoiceID,
        @StripePaymentIntentID,

        @AmountMinor,
        @Currency,

        @TransactionStatus,
        @EventCreatedAtUtc
    );

    UPDATE h
    SET
        StripeCustomerID =
            COALESCE(
                @StripeCustomerID,
                h.StripeCustomerID
            ),

        StripeSubscriptionID =
            CASE
                /*
                   Lifetime plans do not use a recurring subscription.
                */
                WHEN @PremiumPlanType = N'lifetime'
                    THEN NULL

                ELSE COALESCE(
                    @StripeSubscriptionID,
                    h.StripeSubscriptionID
                )
            END,

        PremiumStatus =
            COALESCE(
                @EntitlementStatus,
                h.PremiumStatus
            ),

        PremiumPlanType =
            COALESCE(
                @PremiumPlanType,
                h.PremiumPlanType
            ),

        PremiumCurrentPeriodEndUtc =
            CASE
                /*
                   Lifetime premium never expires through a subscription
                   period, so there is no CurrentPeriodEnd.
                */
                WHEN @PremiumPlanType = N'lifetime'
                    THEN NULL

                WHEN @EntitlementStatus IS NULL
                    THEN h.PremiumCurrentPeriodEndUtc

                ELSE @CurrentPeriodEndUtc
            END,

        PremiumCancelAtPeriodEnd =
            CASE
                WHEN @PremiumPlanType = N'lifetime'
                    THEN 0

                ELSE COALESCE(
                    @CancelAtPeriodEnd,
                    h.PremiumCancelAtPeriodEnd
                )
            END,

        PremiumUpdatedAtUtc =
            SYSUTCDATETIME(),

        PremiumLastStripeEventCreatedUtc =
            CASE
                WHEN @EntitlementStatus IS NULL
                    THEN h.PremiumLastStripeEventCreatedUtc

                ELSE @EventCreatedAtUtc
            END

    FROM dbo.Households h
    WHERE h.HouseholdID = @HouseholdID
      AND
      (
          @EntitlementStatus IS NULL

          OR h.PremiumLastStripeEventCreatedUtc IS NULL

          OR h.PremiumLastStripeEventCreatedUtc <= @EventCreatedAtUtc
      );

    COMMIT TRANSACTION;

    SELECT CAST(1 AS bit) AS Applied;
END;
GO