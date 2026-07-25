/*
  PackTracker / PetPotty Vet Visits feature
  Additive and safe to run more than once against SQL Server.

  The ALTER statements also upgrade the earlier VetVisits draft schema if it
  was already applied. Application times are currently stored as local,
  timezone-unspecified datetime2 values to match the rest of the application.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.VetVisits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.VetVisits
    (
        VetVisitID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_VetVisits PRIMARY KEY,
        PetID                   int NOT NULL,
        VisitDate               date NOT NULL,
        VisitTime               time(0) NULL,
        IsAllDay                bit NOT NULL CONSTRAINT DF_VetVisits_IsAllDay DEFAULT (0),
        ClinicName              nvarchar(200) NOT NULL CONSTRAINT DF_VetVisits_ClinicName DEFAULT (N''),
        VeterinarianName        nvarchar(150) NOT NULL CONSTRAINT DF_VetVisits_VeterinarianName DEFAULT (N''),
        VisitReason             nvarchar(500) NOT NULL CONSTRAINT DF_VetVisits_VisitReason DEFAULT (N''),
        VisitType               nvarchar(50) NOT NULL,
        Location                nvarchar(400) NOT NULL CONSTRAINT DF_VetVisits_Location DEFAULT (N''),
        PhoneNumber             nvarchar(50) NOT NULL CONSTRAINT DF_VetVisits_PhoneNumber DEFAULT (N''),
        Status                  nvarchar(25) NOT NULL CONSTRAINT DF_VetVisits_Status DEFAULT (N'Scheduled'),
        Notes                   nvarchar(4000) NOT NULL CONSTRAINT DF_VetVisits_Notes DEFAULT (N''),
        FollowUpDate            date NULL,
        Cost                    decimal(10,2) NULL,
        IsEmergency             bit NOT NULL CONSTRAINT DF_VetVisits_IsEmergency DEFAULT (0),
        PreparationInstructions nvarchar(2000) NOT NULL CONSTRAINT DF_VetVisits_Preparation DEFAULT (N''),
        VisitSummary            nvarchar(4000) NOT NULL CONSTRAINT DF_VetVisits_Summary DEFAULT (N''),
        Diagnosis               nvarchar(2000) NOT NULL CONSTRAINT DF_VetVisits_Diagnosis DEFAULT (N''),
        TreatmentProvided       nvarchar(4000) NOT NULL CONSTRAINT DF_VetVisits_Treatment DEFAULT (N''),
        VaccinationsReceived    nvarchar(2000) NOT NULL CONSTRAINT DF_VetVisits_Vaccinations DEFAULT (N''),
        Prescriptions           nvarchar(2000) NOT NULL CONSTRAINT DF_VetVisits_Prescriptions DEFAULT (N''),
        FollowUpInstructions    nvarchar(4000) NOT NULL CONSTRAINT DF_VetVisits_FollowUpInstructions DEFAULT (N''),
        CreatedAt               datetime2(0) NOT NULL CONSTRAINT DF_VetVisits_CreatedAt DEFAULT (SYSDATETIME()),
        UpdatedAt               datetime2(0) NOT NULL CONSTRAINT DF_VetVisits_UpdatedAt DEFAULT (SYSDATETIME()),
        IsDeleted               bit NOT NULL CONSTRAINT DF_VetVisits_IsDeleted DEFAULT (0),
        DeletedAt               datetime2(0) NULL,
        CONSTRAINT FK_VetVisits_Pets FOREIGN KEY (PetID) REFERENCES dbo.Pets(petID) ON DELETE CASCADE,
        CONSTRAINT CK_VetVisits_Cost CHECK (Cost IS NULL OR Cost >= 0)
    );
END;

/* Upgrade an earlier VetVisits draft in place. */
IF COL_LENGTH(N'dbo.VetVisits', N'VisitTime') IS NULL
    ALTER TABLE dbo.VetVisits ADD VisitTime time(0) NULL;
IF COL_LENGTH(N'dbo.VetVisits', N'IsAllDay') IS NULL
    ALTER TABLE dbo.VetVisits ADD IsAllDay bit NOT NULL CONSTRAINT DF_VetVisits_IsAllDay_Upgrade DEFAULT (0);
IF COL_LENGTH(N'dbo.VetVisits', N'VeterinarianName') IS NULL
    ALTER TABLE dbo.VetVisits ADD VeterinarianName nvarchar(150) NOT NULL CONSTRAINT DF_VetVisits_VeterinarianName_Upgrade DEFAULT (N'');
IF COL_LENGTH(N'dbo.VetVisits', N'VisitReason') IS NULL
    ALTER TABLE dbo.VetVisits ADD VisitReason nvarchar(500) NOT NULL CONSTRAINT DF_VetVisits_VisitReason_Upgrade DEFAULT (N'');
IF COL_LENGTH(N'dbo.VetVisits', N'Location') IS NULL
    ALTER TABLE dbo.VetVisits ADD Location nvarchar(400) NOT NULL CONSTRAINT DF_VetVisits_Location_Upgrade DEFAULT (N'');
IF COL_LENGTH(N'dbo.VetVisits', N'PhoneNumber') IS NULL
    ALTER TABLE dbo.VetVisits ADD PhoneNumber nvarchar(50) NOT NULL CONSTRAINT DF_VetVisits_PhoneNumber_Upgrade DEFAULT (N'');
IF COL_LENGTH(N'dbo.VetVisits', N'Status') IS NULL
    ALTER TABLE dbo.VetVisits ADD Status nvarchar(25) NOT NULL CONSTRAINT DF_VetVisits_Status_Upgrade DEFAULT (N'Completed');
IF COL_LENGTH(N'dbo.VetVisits', N'IsEmergency') IS NULL
    ALTER TABLE dbo.VetVisits ADD IsEmergency bit NOT NULL CONSTRAINT DF_VetVisits_IsEmergency_Upgrade DEFAULT (0);
IF COL_LENGTH(N'dbo.VetVisits', N'PreparationInstructions') IS NULL
    ALTER TABLE dbo.VetVisits ADD PreparationInstructions nvarchar(2000) NOT NULL CONSTRAINT DF_VetVisits_Preparation_Upgrade DEFAULT (N'');
IF COL_LENGTH(N'dbo.VetVisits', N'VisitSummary') IS NULL
    ALTER TABLE dbo.VetVisits ADD VisitSummary nvarchar(4000) NOT NULL CONSTRAINT DF_VetVisits_Summary_Upgrade DEFAULT (N'');
IF COL_LENGTH(N'dbo.VetVisits', N'TreatmentProvided') IS NULL
    ALTER TABLE dbo.VetVisits ADD TreatmentProvided nvarchar(4000) NOT NULL CONSTRAINT DF_VetVisits_Treatment_Upgrade DEFAULT (N'');
IF COL_LENGTH(N'dbo.VetVisits', N'VaccinationsReceived') IS NULL
    ALTER TABLE dbo.VetVisits ADD VaccinationsReceived nvarchar(2000) NOT NULL CONSTRAINT DF_VetVisits_Vaccinations_Upgrade DEFAULT (N'');
IF COL_LENGTH(N'dbo.VetVisits', N'Prescriptions') IS NULL
    ALTER TABLE dbo.VetVisits ADD Prescriptions nvarchar(2000) NOT NULL CONSTRAINT DF_VetVisits_Prescriptions_Upgrade DEFAULT (N'');
IF COL_LENGTH(N'dbo.VetVisits', N'FollowUpInstructions') IS NULL
    ALTER TABLE dbo.VetVisits ADD FollowUpInstructions nvarchar(4000) NOT NULL CONSTRAINT DF_VetVisits_FollowUpInstructions_Upgrade DEFAULT (N'');
IF COL_LENGTH(N'dbo.VetVisits', N'UpdatedAt') IS NULL
    ALTER TABLE dbo.VetVisits ADD UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_VetVisits_UpdatedAt_Upgrade DEFAULT (SYSDATETIME());
IF COL_LENGTH(N'dbo.VetVisits', N'IsDeleted') IS NULL
    ALTER TABLE dbo.VetVisits ADD IsDeleted bit NOT NULL CONSTRAINT DF_VetVisits_IsDeleted_Upgrade DEFAULT (0);
IF COL_LENGTH(N'dbo.VetVisits', N'DeletedAt') IS NULL
    ALTER TABLE dbo.VetVisits ADD DeletedAt datetime2(0) NULL;

/* Copy compatible values from the earlier draft without making it a dependency. */
IF COL_LENGTH(N'dbo.VetVisits', N'Veterinarian') IS NOT NULL
    EXEC(N'UPDATE dbo.VetVisits SET VeterinarianName = Veterinarian WHERE VeterinarianName = N'''' AND Veterinarian <> N'''';');
IF COL_LENGTH(N'dbo.VetVisits', N'ReasonForVisit') IS NOT NULL
    EXEC(N'UPDATE dbo.VetVisits SET VisitReason = ReasonForVisit WHERE VisitReason = N'''' AND ReasonForVisit <> N'''';');
IF COL_LENGTH(N'dbo.VetVisits', N'ClinicAddress') IS NOT NULL
    EXEC(N'UPDATE dbo.VetVisits SET Location = ClinicAddress WHERE Location = N'''' AND ClinicAddress <> N'''';');
IF COL_LENGTH(N'dbo.VetVisits', N'ClinicPhone') IS NOT NULL
    EXEC(N'UPDATE dbo.VetVisits SET PhoneNumber = ClinicPhone WHERE PhoneNumber = N'''' AND ClinicPhone <> N'''';');
IF COL_LENGTH(N'dbo.VetVisits', N'Treatment') IS NOT NULL
    EXEC(N'UPDATE dbo.VetVisits SET TreatmentProvided = Treatment WHERE TreatmentProvided = N'''' AND Treatment <> N'''';');
EXEC(N'UPDATE dbo.VetVisits SET UpdatedAt = COALESCE(UpdatedAt, CreatedAt, SYSDATETIME());');

/* The earlier draft used NO ACTION, which prevents the existing DeletePet flow. */
DECLARE @VetVisitPetForeignKey sysname;
SELECT TOP (1) @VetVisitPetForeignKey = fk.name
FROM sys.foreign_keys fk
WHERE fk.parent_object_id = OBJECT_ID(N'dbo.VetVisits')
  AND fk.referenced_object_id = OBJECT_ID(N'dbo.Pets')
  AND fk.delete_referential_action = 0;
IF @VetVisitPetForeignKey IS NOT NULL
BEGIN
    EXEC(N'ALTER TABLE dbo.VetVisits DROP CONSTRAINT ' + QUOTENAME(@VetVisitPetForeignKey) + N';');
    ALTER TABLE dbo.VetVisits WITH CHECK
        ADD CONSTRAINT FK_VetVisits_Pets FOREIGN KEY (PetID)
        REFERENCES dbo.Pets(petID) ON DELETE CASCADE;
END;

IF OBJECT_ID(N'dbo.VetVisitReminders', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.VetVisitReminders
    (
        VetVisitReminderID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_VetVisitReminders PRIMARY KEY,
        VetVisitID         int NOT NULL,
        ReminderAt         datetime2(0) NOT NULL,
        Status             nvarchar(20) NOT NULL CONSTRAINT DF_VetVisitReminders_Status DEFAULT (N'Pending'),
        DisplayedAt        datetime2(0) NULL,
        DismissedAt        datetime2(0) NULL,
        CreatedAt          datetime2(0) NOT NULL CONSTRAINT DF_VetVisitReminders_CreatedAt DEFAULT (SYSDATETIME()),
        UpdatedAt          datetime2(0) NOT NULL CONSTRAINT DF_VetVisitReminders_UpdatedAt DEFAULT (SYSDATETIME()),
        CONSTRAINT FK_VetVisitReminders_Visits FOREIGN KEY (VetVisitID)
            REFERENCES dbo.VetVisits(VetVisitID) ON DELETE CASCADE,
        CONSTRAINT UQ_VetVisitReminders_Visit UNIQUE (VetVisitID),
        CONSTRAINT CK_VetVisitReminders_Status CHECK
            (Status IN (N'Pending', N'Displayed', N'Dismissed', N'Expired', N'Cancelled'))
    );
END;

IF OBJECT_ID(N'dbo.VetVisitDocuments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.VetVisitDocuments
    (
        VetVisitDocumentID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_VetVisitDocuments PRIMARY KEY,
        VetVisitID         int NOT NULL,
        DocumentType       nvarchar(50) NOT NULL,
        DisplayName        nvarchar(260) NOT NULL,
        OriginalFileName   nvarchar(260) NOT NULL,
        StoredPath         nvarchar(500) NOT NULL,
        ContentType        nvarchar(150) NOT NULL,
        FileSizeBytes      bigint NOT NULL,
        Description        nvarchar(1000) NOT NULL CONSTRAINT DF_VetVisitDocuments_Description DEFAULT (N''),
        CreatedAt          datetime2(0) NOT NULL CONSTRAINT DF_VetVisitDocuments_CreatedAt DEFAULT (SYSDATETIME()),
        CONSTRAINT FK_VetVisitDocuments_Visits FOREIGN KEY (VetVisitID)
            REFERENCES dbo.VetVisits(VetVisitID) ON DELETE CASCADE,
        CONSTRAINT UQ_VetVisitDocuments_StoredPath UNIQUE (StoredPath),
        CONSTRAINT CK_VetVisitDocuments_FileSize CHECK (FileSizeBytes > 0)
    );
END;

IF OBJECT_ID(N'dbo.VetVisitHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.VetVisitHistory
    (
        VetVisitHistoryID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_VetVisitHistory PRIMARY KEY,
        VetVisitID        int NOT NULL,
        ChangeType        nvarchar(60) NOT NULL,
        OldStatus         nvarchar(25) NULL,
        NewStatus         nvarchar(25) NULL,
        Details           nvarchar(1000) NOT NULL CONSTRAINT DF_VetVisitHistory_Details DEFAULT (N''),
        ChangedAt         datetime2(0) NOT NULL CONSTRAINT DF_VetVisitHistory_ChangedAt DEFAULT (SYSDATETIME()),
        CONSTRAINT FK_VetVisitHistory_Visits FOREIGN KEY (VetVisitID)
            REFERENCES dbo.VetVisits(VetVisitID) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.VetVisits') AND name = N'IX_VetVisits_Pet_Date_Status')
    EXEC(N'CREATE INDEX IX_VetVisits_Pet_Date_Status ON dbo.VetVisits (PetID, VisitDate, Status) INCLUDE (VisitTime, IsAllDay, VisitType, IsDeleted);');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.VetVisitReminders') AND name = N'IX_VetVisitReminders_Status_ReminderAt')
    CREATE INDEX IX_VetVisitReminders_Status_ReminderAt ON dbo.VetVisitReminders (Status, ReminderAt) INCLUDE (VetVisitID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.VetVisitDocuments') AND name = N'IX_VetVisitDocuments_Visit')
    CREATE INDEX IX_VetVisitDocuments_Visit ON dbo.VetVisitDocuments (VetVisitID, CreatedAt DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.VetVisitHistory') AND name = N'IX_VetVisitHistory_Visit')
    CREATE INDEX IX_VetVisitHistory_Visit ON dbo.VetVisitHistory (VetVisitID, ChangedAt DESC);

COMMIT TRANSACTION;
