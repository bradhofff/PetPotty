/* Run after AddHouseholdFamilyAccounts. No existing household is granted paid access.
   BillingJson v1 holds checkout attempts, subscriptions and successful payment periods.
   Customer IDs are unique; webhook receipt and state changes commit atomically. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.Households', N'U') IS NULL
    THROW 51000, 'Apply the household migration first.', 1;

IF OBJECT_ID(N'dbo.HouseholdBilling', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HouseholdBilling
    (
        HouseholdID int NOT NULL CONSTRAINT PK_HouseholdBilling PRIMARY KEY,
        StripeCustomerID varchar(255) NULL,
        BillingJson nvarchar(max) NOT NULL CONSTRAINT DF_HouseholdBilling_Json DEFAULT N'{}',
        PlanName varchar(20) NOT NULL CONSTRAINT DF_HouseholdBilling_Plan DEFAULT 'Free',
        BillingStatus nvarchar(50) NOT NULL CONSTRAINT DF_HouseholdBilling_Status DEFAULT N'Free',
        AccessEndsAtUtc datetimeoffset(0) NULL,
        RenewsAtUtc datetimeoffset(0) NULL,
        UpdatedAtUtc datetimeoffset(0) NOT NULL CONSTRAINT DF_HouseholdBilling_Updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_HouseholdBilling_Household FOREIGN KEY (HouseholdID) REFERENCES dbo.Households(HouseholdID),
        CONSTRAINT CK_HouseholdBilling_Json CHECK (ISJSON(BillingJson) = 1),
        CONSTRAINT CK_HouseholdBilling_Plan CHECK (PlanName IN ('Free', 'Monthly', 'Yearly', 'Lifetime'))
    );
    CREATE UNIQUE INDEX UX_HouseholdBilling_Customer ON dbo.HouseholdBilling(StripeCustomerID)
        WHERE StripeCustomerID IS NOT NULL;
END;
IF OBJECT_ID(N'dbo.StripeWebhookEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StripeWebhookEvents
    (
        EventID varchar(255) NOT NULL CONSTRAINT PK_StripeWebhookEvents PRIMARY KEY,
        HouseholdID int NOT NULL,
        EventType varchar(100) NOT NULL,
        StripeObjectID varchar(255) NOT NULL,
        EventCreatedAtUtc datetimeoffset(0) NOT NULL,
        ProcessedAtUtc datetimeoffset(0) NOT NULL CONSTRAINT DF_StripeWebhookEvents_Processed DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_StripeWebhookEvents_Household FOREIGN KEY (HouseholdID) REFERENCES dbo.Households(HouseholdID)
    );
    CREATE INDEX IX_StripeWebhookEvents_Household ON dbo.StripeWebhookEvents(HouseholdID, ProcessedAtUtc);
END;
COMMIT;
GO
CREATE OR ALTER VIEW dbo.HouseholdBillingCurrent AS
SELECT h.HouseholdID,
    CASE paid.PlanValue WHEN 1 THEN 'Monthly' WHEN 2 THEN 'Yearly' WHEN 3 THEN 'Lifetime' ELSE 'Free' END AS PlanName,
    CASE WHEN paid.PlanValue = 3 THEN N'Active'
         WHEN paid.PlanValue IS NULL AND b.PlanName IN ('Monthly', 'Yearly') THEN N'Expired'
         WHEN paid.PlanValue IN (1,2) AND b.PlanName = 'Free' THEN N'Active'
         ELSE COALESCE(b.BillingStatus, N'Free') END AS BillingStatus,
    COALESCE(paid.EndsAt, b.AccessEndsAtUtc) AS AccessEndsAtUtc,
    CASE WHEN paid.PlanValue IN (1,2) THEN b.RenewsAtUtc END AS RenewsAtUtc,
    b.StripeCustomerID, b.UpdatedAtUtc
FROM dbo.Households h
LEFT JOIN dbo.HouseholdBilling b ON b.HouseholdID = h.HouseholdID
OUTER APPLY
(
    /* Resolve paid coverage at read time, including future coverage that becomes current
       without another event. Enum values in BillingJson v1: Monthly=1, Yearly=2, Lifetime=3. */
    SELECT TOP (1) p.PlanValue, p.EndsAt
    FROM OPENJSON(COALESCE(b.BillingJson,N'{}'), '$.Payments')
    WITH (PlanValue int '$.Plan', StartsAt datetimeoffset(7) '$.StartsAt',
          EndsAt datetimeoffset(7) '$.EndsAt', PaidAt datetimeoffset(7) '$.PaidAt') p
    WHERE p.PlanValue = 3 OR (p.PlanValue IN (1,2) AND p.StartsAt <= SYSUTCDATETIME() AND p.EndsAt > SYSUTCDATETIME())
    ORDER BY CASE WHEN p.PlanValue=3 THEN 1 ELSE 0 END DESC, p.StartsAt DESC, p.PaidAt DESC
) paid;
GO
