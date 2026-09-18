/*
  PetPotty health-history MVP.

  Additive and safe to run more than once. Existing care, medication, and vet
  rows remain in their source tables. HealthEvents adds only first-class
  symptoms/incidents. MedicationSchedule gains adherence metadata while the
  legacy isConfirmed/confirmedAt columns remain available to the established
  scheduling procedures.

  New clinical timestamps use UTC. Historical local timestamps are not
  rewritten because the user's historical timezone is unknown.
*/
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.HealthEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HealthEvents
    (
        HealthEventID          int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_HealthEvents PRIMARY KEY,
        PetID                  int NOT NULL,
        EventKind              nvarchar(20) NOT NULL,
        EventType              nvarchar(100) NOT NULL,
        OccurredAtUtc          datetime2(0) NOT NULL,
        EndedAtUtc             datetime2(0) NULL,
        Severity               tinyint NULL,
        Description            nvarchar(2000) NOT NULL
            CONSTRAINT DF_HealthEvents_Description DEFAULT (N''),
        PossibleTrigger        nvarchar(500) NOT NULL
            CONSTRAINT DF_HealthEvents_PossibleTrigger DEFAULT (N''),
        AppetiteStatus         nvarchar(30) NOT NULL
            CONSTRAINT DF_HealthEvents_AppetiteStatus DEFAULT (N''),
        DrinkingStatus         nvarchar(30) NOT NULL
            CONSTRAINT DF_HealthEvents_DrinkingStatus DEFAULT (N''),
        RelatedMedicationID    int NULL,
        RecoveryStatus         nvarchar(30) NOT NULL
            CONSTRAINT DF_HealthEvents_RecoveryStatus DEFAULT (N''),
        RecoveredAtUtc         datetime2(0) NULL,
        VeterinarianContacted  bit NOT NULL
            CONSTRAINT DF_HealthEvents_VetContacted DEFAULT (0),
        CreatedByUserID        int NOT NULL,
        CreatedAtUtc           datetime2(0) NOT NULL
            CONSTRAINT DF_HealthEvents_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc           datetime2(0) NOT NULL
            CONSTRAINT DF_HealthEvents_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        IsDeleted              bit NOT NULL
            CONSTRAINT DF_HealthEvents_IsDeleted DEFAULT (0),
        DeletedAtUtc           datetime2(0) NULL,
        DeletedByUserID        int NULL,

        CONSTRAINT FK_HealthEvents_Pets FOREIGN KEY (PetID)
            REFERENCES dbo.Pets(petID) ON DELETE CASCADE,
        CONSTRAINT FK_HealthEvents_RelatedMedication FOREIGN KEY (RelatedMedicationID)
            REFERENCES dbo.Medications(medID) ON DELETE SET NULL,
        CONSTRAINT FK_HealthEvents_CreatedBy FOREIGN KEY (CreatedByUserID)
            REFERENCES dbo.Users(userID),
        CONSTRAINT FK_HealthEvents_DeletedBy FOREIGN KEY (DeletedByUserID)
            REFERENCES dbo.Users(userID),
        CONSTRAINT CK_HealthEvents_EventKind
            CHECK (EventKind IN (N'Symptom', N'Incident')),
        CONSTRAINT CK_HealthEvents_Severity
            CHECK (Severity IS NULL OR Severity BETWEEN 1 AND 5),
        CONSTRAINT CK_HealthEvents_End
            CHECK (EndedAtUtc IS NULL OR EndedAtUtc >= OccurredAtUtc),
        CONSTRAINT CK_HealthEvents_Recovery
            CHECK (RecoveredAtUtc IS NULL OR RecoveredAtUtc >= OccurredAtUtc)
    );
END;
GO

IF COL_LENGTH(N'dbo.MedicationSchedule', N'DoseStatus') IS NULL
    ALTER TABLE dbo.MedicationSchedule ADD DoseStatus nvarchar(20) NULL;

IF COL_LENGTH(N'dbo.MedicationSchedule', N'AdministeredAtUtc') IS NULL
    ALTER TABLE dbo.MedicationSchedule ADD AdministeredAtUtc datetime2(0) NULL;

IF COL_LENGTH(N'dbo.MedicationSchedule', N'RecordedAtUtc') IS NULL
    ALTER TABLE dbo.MedicationSchedule ADD RecordedAtUtc datetime2(0) NULL;

IF COL_LENGTH(N'dbo.MedicationSchedule', N'RecordedByUserID') IS NULL
    ALTER TABLE dbo.MedicationSchedule ADD RecordedByUserID int NULL;

IF COL_LENGTH(N'dbo.MedicationSchedule', N'StatusReason') IS NULL
    ALTER TABLE dbo.MedicationSchedule ADD StatusReason nvarchar(500) NULL;

IF COL_LENGTH(N'dbo.MedicationSchedule', N'AdministrationNotes') IS NULL
    ALTER TABLE dbo.MedicationSchedule ADD AdministrationNotes nvarchar(1000) NULL;

IF COL_LENGTH(N'dbo.Tasks', N'RecordedByUserID') IS NULL
    ALTER TABLE dbo.Tasks ADD RecordedByUserID int NULL;

IF COL_LENGTH(N'dbo.VetVisits', N'CreatedByUserID') IS NULL
    ALTER TABLE dbo.VetVisits ADD CreatedByUserID int NULL;
GO

UPDATE dbo.MedicationSchedule
SET DoseStatus = CASE WHEN isConfirmed = 1 THEN N'Taken' ELSE N'Due' END
WHERE DoseStatus IS NULL
   OR DoseStatus NOT IN (N'Due', N'Taken', N'Taken late', N'Skipped', N'Missed');

/* Historical authorship cannot be reconstructed safely. Leave these nullable
   rather than guessing that the pet's owner created every older record. */

ALTER TABLE dbo.MedicationSchedule ALTER COLUMN DoseStatus nvarchar(20) NOT NULL;

IF OBJECT_ID(N'DF_MedicationSchedule_DoseStatus', N'D') IS NULL
    ALTER TABLE dbo.MedicationSchedule
        ADD CONSTRAINT DF_MedicationSchedule_DoseStatus DEFAULT (N'Due') FOR DoseStatus;

IF OBJECT_ID(N'CK_MedicationSchedule_DoseStatus', N'C') IS NULL
    ALTER TABLE dbo.MedicationSchedule WITH CHECK
        ADD CONSTRAINT CK_MedicationSchedule_DoseStatus
            CHECK (DoseStatus IN (N'Due', N'Taken', N'Taken late', N'Skipped', N'Missed'));

IF OBJECT_ID(N'FK_MedicationSchedule_RecordedBy', N'F') IS NULL
    ALTER TABLE dbo.MedicationSchedule WITH CHECK
        ADD CONSTRAINT FK_MedicationSchedule_RecordedBy
            FOREIGN KEY (RecordedByUserID) REFERENCES dbo.Users(userID);

IF OBJECT_ID(N'FK_Tasks_RecordedBy', N'F') IS NULL
    ALTER TABLE dbo.Tasks WITH CHECK
        ADD CONSTRAINT FK_Tasks_RecordedBy
            FOREIGN KEY (RecordedByUserID) REFERENCES dbo.Users(userID);

IF OBJECT_ID(N'FK_VetVisits_CreatedBy', N'F') IS NULL
    ALTER TABLE dbo.VetVisits WITH CHECK
        ADD CONSTRAINT FK_VetVisits_CreatedBy
            FOREIGN KEY (CreatedByUserID) REFERENCES dbo.Users(userID);

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.HealthEvents')
      AND name = N'IX_HealthEvents_Pet_OccurredAtUtc'
)
    CREATE INDEX IX_HealthEvents_Pet_OccurredAtUtc
        ON dbo.HealthEvents (PetID, OccurredAtUtc DESC)
        INCLUDE (EventKind, EventType, Severity, RecoveryStatus, CreatedByUserID)
        WHERE IsDeleted = 0;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.HealthEvents')
      AND name = N'IX_HealthEvents_CreatedBy_CreatedAtUtc'
)
    CREATE INDEX IX_HealthEvents_CreatedBy_CreatedAtUtc
        ON dbo.HealthEvents (CreatedByUserID, CreatedAtUtc DESC);

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.MedicationSchedule')
      AND name = N'IX_MedicationSchedule_Med_Status_Date'
)
    CREATE INDEX IX_MedicationSchedule_Med_Status_Date
        ON dbo.MedicationSchedule (medID, DoseStatus, scheduleDate)
        INCLUDE (isConfirmed, confirmedAt, AdministeredAtUtc, RecordedByUserID);

COMMIT TRANSACTION;
GO

CREATE OR ALTER PROCEDURE dbo.GetScheduledMedsByPetID_AllTime
    @petID int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        ms.scheduleID,
        m.medID,
        m.medicationName,
        m.dosage,
        m.frequencyType,
        m.TimingDoesNotMatter,
        ms.scheduleDate,
        ms.isConfirmed,
        ms.confirmedAt,
        ms.DoseStatus,
        ms.AdministeredAtUtc,
        ms.RecordedAtUtc,
        ms.RecordedByUserID,
        u.name AS RecordedByName,
        ms.StatusReason,
        ms.AdministrationNotes
    FROM dbo.MedicationSchedule ms
    INNER JOIN dbo.Medications m ON m.medID = ms.medID
    LEFT JOIN dbo.Users u ON u.userID = ms.RecordedByUserID
    WHERE m.petID = @petID
    ORDER BY ms.scheduleDate;
END;
GO

CREATE OR ALTER PROCEDURE dbo.GetScheduledMedsByPetID_Next2Months
    @petID int,
    @Today date = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SET @Today = COALESCE(@Today, CAST(SYSUTCDATETIME() AS date));

    SELECT
        ms.scheduleID,
        m.medID,
        m.medicationName,
        m.dosage,
        m.frequencyType,
        m.TimingDoesNotMatter,
        ms.scheduleDate,
        ms.isConfirmed,
        ms.confirmedAt,
        ms.DoseStatus,
        ms.AdministeredAtUtc,
        ms.RecordedAtUtc,
        ms.RecordedByUserID,
        u.name AS RecordedByName,
        ms.StatusReason,
        ms.AdministrationNotes
    FROM dbo.MedicationSchedule ms
    INNER JOIN dbo.Medications m ON m.medID = ms.medID
    LEFT JOIN dbo.Users u ON u.userID = ms.RecordedByUserID
    WHERE m.petID = @petID
      AND ms.scheduleDate < DATEADD(MONTH, 2, @Today)
      AND (ms.DoseStatus = N'Due' OR ms.scheduleDate >= @Today)
    ORDER BY ms.scheduleDate;
END;
GO
