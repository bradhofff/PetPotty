/* Read-only post-deployment verification for the health-history MVP. */
SET NOCOUNT ON;

DECLARE @Failures table (CheckName nvarchar(200) NOT NULL);

IF OBJECT_ID(N'dbo.HealthEvents', N'U') IS NULL
    INSERT @Failures VALUES (N'HealthEvents table is missing');

IF COL_LENGTH(N'dbo.MedicationSchedule', N'DoseStatus') IS NULL
    INSERT @Failures VALUES (N'MedicationSchedule.DoseStatus is missing');

IF COL_LENGTH(N'dbo.MedicationSchedule', N'AdministeredAtUtc') IS NULL
    INSERT @Failures VALUES (N'MedicationSchedule.AdministeredAtUtc is missing');

IF COL_LENGTH(N'dbo.MedicationSchedule', N'RecordedByUserID') IS NULL
    INSERT @Failures VALUES (N'MedicationSchedule.RecordedByUserID is missing');

IF COL_LENGTH(N'dbo.Tasks', N'RecordedByUserID') IS NULL
    INSERT @Failures VALUES (N'Tasks.RecordedByUserID is missing');

IF COL_LENGTH(N'dbo.VetVisits', N'CreatedByUserID') IS NULL
    INSERT @Failures VALUES (N'VetVisits.CreatedByUserID is missing');

IF OBJECT_ID(N'dbo.HealthEvents', N'U') IS NOT NULL
   AND NOT EXISTS
       (SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.HealthEvents')
          AND name = N'IX_HealthEvents_Pet_OccurredAtUtc')
    INSERT @Failures VALUES (N'Health event timeline index is missing');

IF OBJECT_ID(N'dbo.GetScheduledMedsByPetID_AllTime', N'P') IS NULL
   OR OBJECT_DEFINITION(OBJECT_ID(N'dbo.GetScheduledMedsByPetID_AllTime'))
        NOT LIKE N'%DoseStatus%'
    INSERT @Failures VALUES (N'All-time medication schedule procedure lacks adherence fields');

IF OBJECT_ID(N'dbo.GetScheduledMedsByPetID_Next2Months', N'P') IS NULL
   OR OBJECT_DEFINITION(OBJECT_ID(N'dbo.GetScheduledMedsByPetID_Next2Months'))
        NOT LIKE N'%DoseStatus%'
    INSERT @Failures VALUES (N'Next-two-month medication schedule procedure lacks adherence fields');

IF COL_LENGTH(N'dbo.MedicationSchedule', N'DoseStatus') IS NOT NULL
   AND EXISTS (SELECT 1 FROM dbo.MedicationSchedule WHERE DoseStatus IS NULL)
    INSERT @Failures VALUES (N'One or more existing medication rows were not backfilled');

IF EXISTS (SELECT 1 FROM @Failures)
BEGIN
    SELECT CheckName FROM @Failures ORDER BY CheckName;
    THROW 51010, 'Health-history MVP verification failed.', 1;
END;

SELECT N'Health-history MVP schema verification passed.' AS Result;
