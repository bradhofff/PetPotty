/* Read-only checks after AddHouseholdBilling. Every FailureCount should be zero. */
SELECT N'Invalid billing JSON' AS CheckName, COUNT_BIG(*) AS FailureCount
FROM dbo.HouseholdBilling WHERE ISJSON(BillingJson) <> 1
UNION ALL
SELECT N'Households without billing incorrectly paid', COUNT_BIG(*)
FROM dbo.HouseholdBillingCurrent v
WHERE NOT EXISTS (SELECT 1 FROM dbo.HouseholdBilling b WHERE b.HouseholdID=v.HouseholdID)
  AND v.PlanName <> 'Free'
UNION ALL
SELECT N'Expired recurring access incorrectly paid', COUNT_BIG(*)
FROM dbo.HouseholdBillingCurrent
WHERE PlanName IN ('Monthly','Yearly') AND (AccessEndsAtUtc IS NULL OR AccessEndsAtUtc <= SYSUTCDATETIME())
UNION ALL
SELECT N'Customer projection differs from reconciliation state', COUNT_BIG(*)
FROM dbo.HouseholdBilling
WHERE COALESCE(StripeCustomerID,'') <> COALESCE(JSON_VALUE(BillingJson,'$.CustomerId'),'');

SELECT HouseholdID, PlanName, BillingStatus, AccessEndsAtUtc, RenewsAtUtc, UpdatedAtUtc
FROM dbo.HouseholdBillingCurrent ORDER BY HouseholdID;
GO
