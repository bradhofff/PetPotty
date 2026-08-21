/* Read-only verification for the 2026-08-20 and 2026-08-21 SQL changes. */
SET NOCOUNT ON;

DECLARE @Failures table (CheckName nvarchar(200) NOT NULL);

IF OBJECT_ID(N'dbo.GetScheduledMedsByPetID_AllTime', N'P') IS NULL
    INSERT @Failures VALUES (N'GetScheduledMedsByPetID_AllTime is missing');

IF OBJECT_ID(N'dbo.GetScheduledMedsByPetID_Next2Months', N'P') IS NULL
    INSERT @Failures VALUES (N'GetScheduledMedsByPetID_Next2Months is missing');
ELSE IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.GetScheduledMedsByPetID_Next2Months'))
        NOT LIKE N'%DATEADD(MONTH, 2, @Today)%'
    INSERT @Failures VALUES (N'Next2Months does not use the two-month boundary');

IF OBJECT_ID(N'dbo.AddVetVisit', N'P') IS NULL
    INSERT @Failures VALUES (N'AddVetVisit is missing');

IF OBJECT_ID(N'dbo.UpdateVetVisit', N'P') IS NULL
    INSERT @Failures VALUES (N'UpdateVetVisit is missing');

IF OBJECT_ID(N'dbo.VetVisitDocuments', N'U') IS NULL
    INSERT @Failures VALUES (N'VetVisitDocuments is missing');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.VetVisits')
      AND name = N'Cost'
      AND is_nullable = 1
)
    INSERT @Failures VALUES (N'VetVisits.Cost is not nullable');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.VetVisits')
      AND name = N'Notes'
      AND max_length = -1
      AND is_nullable = 0
)
    INSERT @Failures VALUES (N'VetVisits.Notes is not nvarchar(max) NOT NULL');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.parameters
    WHERE object_id = OBJECT_ID(N'dbo.AddVetVisit')
      AND name = N'@Notes'
      AND max_length = -1
)
    INSERT @Failures VALUES (N'AddVetVisit @Notes is not nvarchar(max)');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.parameters
    WHERE object_id = OBJECT_ID(N'dbo.UpdateVetVisit')
      AND name = N'@Notes'
      AND max_length = -1
)
    INSERT @Failures VALUES (N'UpdateVetVisit @Notes is not nvarchar(max)');

IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.AddVetVisit'))
       LIKE N'%OR LTRIM(RTRIM(COALESCE(@Location, N''''))) = N''''%'
    INSERT @Failures VALUES (N'AddVetVisit still requires Location');

IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.AddVetVisit'))
       LIKE N'%OR LTRIM(RTRIM(COALESCE(@PhoneNumber, N''''))) = N''''%'
    INSERT @Failures VALUES (N'AddVetVisit still requires PhoneNumber');

IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.UpdateVetVisit'))
       LIKE N'%OR LTRIM(RTRIM(COALESCE(@Location, N''''))) = N''''%'
    INSERT @Failures VALUES (N'UpdateVetVisit still requires Location');

IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.UpdateVetVisit'))
       LIKE N'%OR LTRIM(RTRIM(COALESCE(@PhoneNumber, N''''))) = N''''%'
    INSERT @Failures VALUES (N'UpdateVetVisit still requires PhoneNumber');

IF EXISTS (SELECT 1 FROM @Failures)
BEGIN
    SELECT N'FAIL' AS Result, CheckName FROM @Failures ORDER BY CheckName;
    THROW 51000, 'One or more release database checks failed.', 1;
END;

SELECT N'PASS' AS Result,
       N'Medication range, optional cost, simplified vet visits, and attachment schema are ready.' AS Details;
