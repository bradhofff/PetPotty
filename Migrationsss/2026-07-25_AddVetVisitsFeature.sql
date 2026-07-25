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
    DECLARE @DropVetVisitPetForeignKeySql nvarchar(max);
    SET @DropVetVisitPetForeignKeySql =
        N'ALTER TABLE dbo.VetVisits DROP CONSTRAINT '
        + QUOTENAME(@VetVisitPetForeignKey)
        + N';';
    EXEC sys.sp_executesql @DropVetVisitPetForeignKeySql;

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
GO

/*
  Create a visit and its optional reminder atomically.
  Returns one row containing VetVisitID. A value of 0 means ownership,
  appointment, or required-field validation failed.
*/
CREATE OR ALTER PROCEDURE dbo.AddVetVisit
    @UserID                 int,
    @PetID                  int,
    @VisitDate              date,
    @VisitTime              time(0) = NULL,
    @IsAllDay               bit = 0,
    @ClinicName             nvarchar(200) = N'',
    @VeterinarianName       nvarchar(150) = N'',
    @VisitReason            nvarchar(500),
    @VisitType              nvarchar(50),
    @Location               nvarchar(400) = N'',
    @PhoneNumber            nvarchar(50) = N'',
    @Status                 nvarchar(25) = N'Scheduled',
    @Notes                  nvarchar(4000) = N'',
    @FollowUpDate           date = NULL,
    @Cost                   decimal(10,2) = NULL,
    @IsEmergency            bit = 0,
    @PreparationInstructions nvarchar(2000) = N'',
    @ReminderAt             datetime2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS
        (SELECT 1 FROM dbo.Pets WHERE petID = @PetID AND userID = @UserID)
       OR @Status NOT IN (N'Scheduled', N'Confirmed', N'Cancelled', N'Missed', N'Rescheduled')
       OR LTRIM(RTRIM(COALESCE(@ClinicName, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@Location, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@PhoneNumber, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VisitReason, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VisitType, N''))) = N''
       OR (@FollowUpDate IS NOT NULL AND @FollowUpDate < @VisitDate)
       OR @Cost IS NULL
       OR @Cost < 0
    BEGIN
        SELECT CAST(0 AS int) AS VetVisitID;
        RETURN;
    END;

    BEGIN TRANSACTION;

    INSERT dbo.VetVisits
        (PetID, VisitDate, VisitTime, IsAllDay, ClinicName, VeterinarianName,
         VisitReason, VisitType, Location, PhoneNumber, Status, Notes,
         FollowUpDate, Cost, IsEmergency, PreparationInstructions,
         CreatedAt, UpdatedAt, IsDeleted)
    VALUES
        (@PetID, @VisitDate, CASE WHEN @IsAllDay = 1 THEN NULL ELSE @VisitTime END,
         @IsAllDay, LTRIM(RTRIM(COALESCE(@ClinicName, N''))),
         LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))),
         LTRIM(RTRIM(@VisitReason)), LTRIM(RTRIM(@VisitType)),
         LTRIM(RTRIM(COALESCE(@Location, N''))),
         LTRIM(RTRIM(COALESCE(@PhoneNumber, N''))), @Status,
         LTRIM(RTRIM(COALESCE(@Notes, N''))), @FollowUpDate, @Cost,
         @IsEmergency, LTRIM(RTRIM(COALESCE(@PreparationInstructions, N''))),
         SYSDATETIME(), SYSDATETIME(), 0);

    DECLARE @VetVisitID int = CONVERT(int, SCOPE_IDENTITY());

    IF @ReminderAt IS NOT NULL
       AND @Status IN (N'Scheduled', N'Confirmed', N'Rescheduled')
    BEGIN
        INSERT dbo.VetVisitReminders
            (VetVisitID, ReminderAt, Status, CreatedAt, UpdatedAt)
        VALUES
            (@VetVisitID, @ReminderAt, N'Pending', SYSDATETIME(), SYSDATETIME());
    END;

    INSERT dbo.VetVisitHistory
        (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt)
    VALUES
        (@VetVisitID, N'Created', NULL, @Status, N'Visit record created.', SYSDATETIME());

    COMMIT TRANSACTION;
    SELECT @VetVisitID AS VetVisitID;
END;
GO

/*
  Edit appointment fields, reschedule, and replace/cancel the single reminder.
  Completed visits may be edited but cannot be moved back to an active status;
  active visits must use CompleteVetVisit to become completed.
*/
CREATE OR ALTER PROCEDURE dbo.UpdateVetVisit
    @UserID                 int,
    @VetVisitID             int,
    @PetID                  int,
    @VisitDate              date,
    @VisitTime              time(0) = NULL,
    @IsAllDay               bit = 0,
    @ClinicName             nvarchar(200) = N'',
    @VeterinarianName       nvarchar(150) = N'',
    @VisitReason            nvarchar(500),
    @VisitType              nvarchar(50),
    @Location               nvarchar(400) = N'',
    @PhoneNumber            nvarchar(50) = N'',
    @Status                 nvarchar(25),
    @Notes                  nvarchar(4000) = N'',
    @FollowUpDate           date = NULL,
    @Cost                   decimal(10,2) = NULL,
    @IsEmergency            bit = 0,
    @PreparationInstructions nvarchar(2000) = N'',
    @ReminderAt             datetime2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @OldStatus nvarchar(25);
    SELECT @OldStatus = v.Status
    FROM dbo.VetVisits v WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    WHERE v.VetVisitID = @VetVisitID
      AND p.userID = @UserID
      AND v.IsDeleted = 0;

    IF @OldStatus IS NULL
       OR NOT EXISTS
          (SELECT 1 FROM dbo.Pets WHERE petID = @PetID AND userID = @UserID)
       OR @Status NOT IN (N'Scheduled', N'Confirmed', N'Completed', N'Cancelled', N'Missed', N'Rescheduled')
       OR (@OldStatus = N'Completed' AND @Status <> N'Completed')
       OR (@OldStatus <> N'Completed' AND @Status = N'Completed')
       OR LTRIM(RTRIM(COALESCE(@ClinicName, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@Location, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@PhoneNumber, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VisitReason, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VisitType, N''))) = N''
       OR (@FollowUpDate IS NOT NULL AND @FollowUpDate < @VisitDate)
       OR @Cost IS NULL
       OR @Cost < 0
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS bit) AS Succeeded;
        RETURN;
    END;

    UPDATE dbo.VetVisits
    SET PetID = @PetID,
        VisitDate = @VisitDate,
        VisitTime = CASE WHEN @IsAllDay = 1 THEN NULL ELSE @VisitTime END,
        IsAllDay = @IsAllDay,
        ClinicName = LTRIM(RTRIM(COALESCE(@ClinicName, N''))),
        VeterinarianName = LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))),
        VisitReason = LTRIM(RTRIM(@VisitReason)),
        VisitType = LTRIM(RTRIM(@VisitType)),
        Location = LTRIM(RTRIM(COALESCE(@Location, N''))),
        PhoneNumber = LTRIM(RTRIM(COALESCE(@PhoneNumber, N''))),
        Status = @Status,
        Notes = LTRIM(RTRIM(COALESCE(@Notes, N''))),
        FollowUpDate = @FollowUpDate,
        Cost = @Cost,
        IsEmergency = @IsEmergency,
        PreparationInstructions = LTRIM(RTRIM(COALESCE(@PreparationInstructions, N''))),
        UpdatedAt = SYSDATETIME()
    WHERE VetVisitID = @VetVisitID;

    IF @ReminderAt IS NOT NULL
       AND @Status IN (N'Scheduled', N'Confirmed', N'Rescheduled')
    BEGIN
        MERGE dbo.VetVisitReminders WITH (HOLDLOCK) AS target
        USING (SELECT @VetVisitID AS VetVisitID) AS source
           ON target.VetVisitID = source.VetVisitID
        WHEN MATCHED THEN
            UPDATE SET ReminderAt = @ReminderAt, Status = N'Pending',
                       DisplayedAt = NULL, DismissedAt = NULL,
                       UpdatedAt = SYSDATETIME()
        WHEN NOT MATCHED THEN
            INSERT (VetVisitID, ReminderAt, Status, CreatedAt, UpdatedAt)
            VALUES (@VetVisitID, @ReminderAt, N'Pending', SYSDATETIME(), SYSDATETIME());
    END
    ELSE
    BEGIN
        UPDATE dbo.VetVisitReminders
        SET Status = N'Cancelled', UpdatedAt = SYSDATETIME()
        WHERE VetVisitID = @VetVisitID;
    END;

    DECLARE @StatusChanged bit =
        CASE WHEN @OldStatus <> @Status THEN 1 ELSE 0 END;
    INSERT dbo.VetVisitHistory
        (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt)
    VALUES
        (@VetVisitID,
         CASE WHEN @StatusChanged = 1 THEN N'Status changed' ELSE N'Updated' END,
         CASE WHEN @StatusChanged = 1 THEN @OldStatus ELSE NULL END,
         CASE WHEN @StatusChanged = 1 THEN @Status ELSE NULL END,
         CASE WHEN @StatusChanged = 1
              THEN CONCAT(N'Status changed from ', @OldStatus, N' to ', @Status, N'.')
              ELSE N'Visit details updated.' END,
         SYSDATETIME());

    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

/* Cancel, miss, confirm, schedule, or reschedule while preserving history. */
CREATE OR ALTER PROCEDURE dbo.ChangeVetVisitStatus
    @UserID     int,
    @VetVisitID int,
    @Status     nvarchar(25),
    @Details    nvarchar(1000) = N''
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @OldStatus nvarchar(25);
    SELECT @OldStatus = v.Status
    FROM dbo.VetVisits v WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    WHERE v.VetVisitID = @VetVisitID
      AND p.userID = @UserID
      AND v.IsDeleted = 0;

    IF @OldStatus IS NULL
       OR @OldStatus = N'Completed'
       OR @Status NOT IN (N'Scheduled', N'Confirmed', N'Cancelled', N'Missed', N'Rescheduled')
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS bit) AS Succeeded;
        RETURN;
    END;

    UPDATE dbo.VetVisits
    SET Status = @Status, UpdatedAt = SYSDATETIME()
    WHERE VetVisitID = @VetVisitID;

    IF @Status NOT IN (N'Scheduled', N'Confirmed', N'Rescheduled')
    BEGIN
        UPDATE dbo.VetVisitReminders
        SET Status = N'Cancelled', UpdatedAt = SYSDATETIME()
        WHERE VetVisitID = @VetVisitID;
    END;

    INSERT dbo.VetVisitHistory
        (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt)
    VALUES
        (@VetVisitID, N'Status changed', @OldStatus, @Status,
         CASE WHEN LTRIM(RTRIM(COALESCE(@Details, N''))) = N''
              THEN CONCAT(N'Status changed to ', @Status, N'.')
              ELSE LTRIM(RTRIM(@Details)) END,
         SYSDATETIME());

    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

/* Complete an active appointment and save its medical outcome atomically. */
CREATE OR ALTER PROCEDURE dbo.CompleteVetVisit
    @UserID                 int,
    @VetVisitID             int,
    @VisitSummary           nvarchar(4000),
    @Diagnosis              nvarchar(2000) = N'',
    @TreatmentProvided      nvarchar(4000) = N'',
    @VaccinationsReceived   nvarchar(2000) = N'',
    @Prescriptions          nvarchar(2000) = N'',
    @FollowUpInstructions   nvarchar(4000) = N'',
    @FollowUpDate           date = NULL,
    @FinalCost              decimal(10,2) = NULL,
    @AdditionalNotes        nvarchar(4000) = N''
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @OldStatus nvarchar(25);
    DECLARE @ExistingVisitDate date;
    SELECT @OldStatus = v.Status, @ExistingVisitDate = v.VisitDate
    FROM dbo.VetVisits v WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    WHERE v.VetVisitID = @VetVisitID
      AND p.userID = @UserID
      AND v.IsDeleted = 0;

    IF @OldStatus IS NULL
       OR @OldStatus NOT IN (N'Scheduled', N'Confirmed', N'Rescheduled')
       OR LTRIM(RTRIM(COALESCE(@VisitSummary, N''))) = N''
       OR (@FollowUpDate IS NOT NULL AND @FollowUpDate < @ExistingVisitDate)
       OR (@FinalCost IS NOT NULL AND @FinalCost < 0)
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS bit) AS Succeeded;
        RETURN;
    END;

    UPDATE dbo.VetVisits
    SET Status = N'Completed',
        VisitSummary = LTRIM(RTRIM(@VisitSummary)),
        Diagnosis = LTRIM(RTRIM(COALESCE(@Diagnosis, N''))),
        TreatmentProvided = LTRIM(RTRIM(COALESCE(@TreatmentProvided, N''))),
        VaccinationsReceived = LTRIM(RTRIM(COALESCE(@VaccinationsReceived, N''))),
        Prescriptions = LTRIM(RTRIM(COALESCE(@Prescriptions, N''))),
        FollowUpInstructions = LTRIM(RTRIM(COALESCE(@FollowUpInstructions, N''))),
        FollowUpDate = @FollowUpDate,
        Cost = COALESCE(@FinalCost, Cost),
        Notes = CASE
                    WHEN LTRIM(RTRIM(COALESCE(@AdditionalNotes, N''))) = N'' THEN Notes
                    WHEN Notes = N'' THEN LTRIM(RTRIM(@AdditionalNotes))
                    ELSE CONCAT(Notes, CHAR(13), CHAR(10), LTRIM(RTRIM(@AdditionalNotes)))
                END,
        UpdatedAt = SYSDATETIME()
    WHERE VetVisitID = @VetVisitID;

    UPDATE dbo.VetVisitReminders
    SET Status = N'Cancelled', UpdatedAt = SYSDATETIME()
    WHERE VetVisitID = @VetVisitID;

    INSERT dbo.VetVisitHistory
        (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt)
    VALUES
        (@VetVisitID, N'Completed', @OldStatus, N'Completed',
         N'Visit marked completed and medical outcome recorded.', SYSDATETIME());

    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

/*
  Permanently delete an owned visit and every dependent database record.
  Attached files are removed by the application after this transaction commits.
  Returns Succeeded = 0 when ownership fails or the visit is already deleted.
*/
CREATE OR ALTER PROCEDURE dbo.DeleteVetVisit
    @UserID     int,
    @VetVisitID int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @OldStatus nvarchar(25);
    SELECT @OldStatus = v.Status
    FROM dbo.VetVisits v WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    WHERE v.VetVisitID = @VetVisitID
      AND p.userID = @UserID
      AND v.IsDeleted = 0;

    IF @OldStatus IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS bit) AS Succeeded;
        RETURN;
    END;

    DELETE FROM dbo.VetVisitReminders
    WHERE VetVisitID = @VetVisitID;

    DELETE FROM dbo.VetVisitDocuments
    WHERE VetVisitID = @VetVisitID;

    DELETE FROM dbo.VetVisitHistory
    WHERE VetVisitID = @VetVisitID;

    DELETE FROM dbo.VetVisits
    WHERE VetVisitID = @VetVisitID;

    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO
