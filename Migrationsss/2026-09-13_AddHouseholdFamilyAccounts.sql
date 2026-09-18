/*
  Household / family accounts.

  This migration is additive and safe to run more than once. It preserves the
  legacy Pets.userID column as the original owner for rolling-deployment safety,
  but all new application authorization uses Pets.HouseholdID plus an active
  HouseholdMembers row.

  Rollback consideration: do not drop HouseholdID after shared households have
  been used. A rollback must first split or reassign shared pets to one user.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

/* Used to make one-time legacy cleanup safe when this idempotent script is rerun. */
DECLARE @IsInitialHouseholdMigration bit =
    CASE WHEN COL_LENGTH(N'dbo.Pets', N'HouseholdID') IS NULL THEN 1 ELSE 0 END;

IF COL_LENGTH(N'dbo.Users', N'PublicID') IS NULL
    ALTER TABLE dbo.Users ADD PublicID uniqueidentifier NULL;

UPDATE dbo.Users SET PublicID = NEWID() WHERE PublicID IS NULL;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Users') AND name = N'PublicID' AND is_nullable = 1
)
    ALTER TABLE dbo.Users ALTER COLUMN PublicID uniqueidentifier NOT NULL;

IF OBJECT_ID(N'DF_Users_PublicID', N'D') IS NULL
    ALTER TABLE dbo.Users ADD CONSTRAINT DF_Users_PublicID DEFAULT (NEWSEQUENTIALID()) FOR PublicID;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Users') AND name = N'UX_Users_PublicID'
)
    CREATE UNIQUE INDEX UX_Users_PublicID ON dbo.Users(PublicID);

IF OBJECT_ID(N'dbo.Households', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Households
    (
        HouseholdID       int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Households PRIMARY KEY,
        PublicID          uniqueidentifier NOT NULL CONSTRAINT DF_Households_PublicID DEFAULT (NEWSEQUENTIALID()),
        Name              nvarchar(150) NOT NULL,
        CreatedByUserID   int NOT NULL,
        CreatedAtUtc      datetime2(0) NOT NULL CONSTRAINT DF_Households_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc      datetime2(0) NOT NULL CONSTRAINT DF_Households_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_Households_CreatedBy FOREIGN KEY (CreatedByUserID) REFERENCES dbo.Users(userID),
        CONSTRAINT CK_Households_Name CHECK (LEN(LTRIM(RTRIM(Name))) BETWEEN 1 AND 150)
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Households') AND name = N'UX_Households_PublicID'
)
    CREATE UNIQUE INDEX UX_Households_PublicID ON dbo.Households(PublicID);

IF OBJECT_ID(N'dbo.HouseholdMembers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HouseholdMembers
    (
        HouseholdID      int NOT NULL,
        UserID           int NOT NULL,
        Role             nvarchar(20) NOT NULL,
        Status           nvarchar(20) NOT NULL CONSTRAINT DF_HouseholdMembers_Status DEFAULT (N'Active'),
        JoinedAtUtc      datetime2(0) NOT NULL CONSTRAINT DF_HouseholdMembers_JoinedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc     datetime2(0) NOT NULL CONSTRAINT DF_HouseholdMembers_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        RemovedAtUtc     datetime2(0) NULL,
        RemovedByUserID  int NULL,
        CONSTRAINT PK_HouseholdMembers PRIMARY KEY (HouseholdID, UserID),
        CONSTRAINT FK_HouseholdMembers_Households FOREIGN KEY (HouseholdID) REFERENCES dbo.Households(HouseholdID),
        CONSTRAINT FK_HouseholdMembers_Users FOREIGN KEY (UserID) REFERENCES dbo.Users(userID),
        CONSTRAINT FK_HouseholdMembers_RemovedBy FOREIGN KEY (RemovedByUserID) REFERENCES dbo.Users(userID),
        CONSTRAINT CK_HouseholdMembers_Role CHECK (Role IN (N'Owner', N'Member', N'Caregiver')),
        CONSTRAINT CK_HouseholdMembers_Status CHECK (Status IN (N'Active', N'Removed')),
        CONSTRAINT CK_HouseholdMembers_Removal CHECK
            ((Status = N'Active' AND RemovedAtUtc IS NULL) OR Status = N'Removed')
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.HouseholdMembers') AND name = N'IX_HouseholdMembers_User_Status'
)
    CREATE INDEX IX_HouseholdMembers_User_Status
        ON dbo.HouseholdMembers(UserID, Status) INCLUDE (HouseholdID, Role, JoinedAtUtc);

/* Repair membership first if a prior partial run created household rows. */
INSERT dbo.HouseholdMembers (HouseholdID, UserID, Role, Status, JoinedAtUtc, UpdatedAtUtc)
SELECT h.HouseholdID, h.CreatedByUserID, N'Owner', N'Active', h.CreatedAtUtc, SYSUTCDATETIME()
FROM dbo.Households h
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.HouseholdMembers hm
    WHERE hm.HouseholdID = h.HouseholdID AND hm.UserID = h.CreatedByUserID
);

/* Give every pre-existing account one personal household. */
INSERT dbo.Households (PublicID, Name, CreatedByUserID, CreatedAtUtc, UpdatedAtUtc)
SELECT NEWID(),
       LEFT(CONCAT(COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100), u.name))), N''),
                            NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100), u.userName))), N''),
                            N'My'), N' Household'), 150),
       u.userID, SYSUTCDATETIME(), SYSUTCDATETIME()
FROM dbo.Users u
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.HouseholdMembers hm
    WHERE hm.UserID = u.userID AND hm.Status = N'Active'
);

INSERT dbo.HouseholdMembers (HouseholdID, UserID, Role, Status, JoinedAtUtc, UpdatedAtUtc)
SELECT h.HouseholdID, h.CreatedByUserID, N'Owner', N'Active', h.CreatedAtUtc, SYSUTCDATETIME()
FROM dbo.Households h
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.HouseholdMembers hm
    WHERE hm.HouseholdID = h.HouseholdID AND hm.UserID = h.CreatedByUserID
);

IF OBJECT_ID(N'dbo.HouseholdInvitations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HouseholdInvitations
    (
        HouseholdInvitationID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HouseholdInvitations PRIMARY KEY,
        PublicID               uniqueidentifier NOT NULL CONSTRAINT DF_HouseholdInvitations_PublicID DEFAULT (NEWSEQUENTIALID()),
        HouseholdID            int NOT NULL,
        Email                  nvarchar(320) NOT NULL,
        NormalizedEmail        nvarchar(320) NOT NULL,
        Role                   nvarchar(20) NOT NULL,
        TokenHash              varbinary(32) NOT NULL,
        InvitedByUserID        int NOT NULL,
        CreatedAtUtc           datetime2(0) NOT NULL CONSTRAINT DF_HouseholdInvitations_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ExpiresAtUtc           datetime2(0) NOT NULL,
        AcceptedAtUtc          datetime2(0) NULL,
        AcceptedByUserID       int NULL,
        RevokedAtUtc           datetime2(0) NULL,
        RevokedByUserID        int NULL,
        CONSTRAINT FK_HouseholdInvitations_Households FOREIGN KEY (HouseholdID) REFERENCES dbo.Households(HouseholdID),
        CONSTRAINT FK_HouseholdInvitations_InvitedBy FOREIGN KEY (InvitedByUserID) REFERENCES dbo.Users(userID),
        CONSTRAINT FK_HouseholdInvitations_AcceptedBy FOREIGN KEY (AcceptedByUserID) REFERENCES dbo.Users(userID),
        CONSTRAINT FK_HouseholdInvitations_RevokedBy FOREIGN KEY (RevokedByUserID) REFERENCES dbo.Users(userID),
        CONSTRAINT CK_HouseholdInvitations_Role CHECK (Role IN (N'Member', N'Caregiver')),
        CONSTRAINT CK_HouseholdInvitations_Expiry CHECK (ExpiresAtUtc > CreatedAtUtc),
        CONSTRAINT CK_HouseholdInvitations_FinalState CHECK
            (NOT (AcceptedAtUtc IS NOT NULL AND RevokedAtUtc IS NOT NULL)),
        CONSTRAINT CK_HouseholdInvitations_AcceptedBy CHECK
            ((AcceptedAtUtc IS NULL AND AcceptedByUserID IS NULL) OR
             (AcceptedAtUtc IS NOT NULL AND AcceptedByUserID IS NOT NULL)),
        CONSTRAINT CK_HouseholdInvitations_RevokedBy CHECK
            (RevokedAtUtc IS NULL OR RevokedByUserID IS NOT NULL)
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.HouseholdInvitations') AND name = N'UX_HouseholdInvitations_PublicID'
)
    CREATE UNIQUE INDEX UX_HouseholdInvitations_PublicID ON dbo.HouseholdInvitations(PublicID);

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.HouseholdInvitations') AND name = N'UX_HouseholdInvitations_TokenHash'
)
    CREATE UNIQUE INDEX UX_HouseholdInvitations_TokenHash ON dbo.HouseholdInvitations(TokenHash);

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.HouseholdInvitations') AND name = N'UX_HouseholdInvitations_PendingEmail'
)
    CREATE UNIQUE INDEX UX_HouseholdInvitations_PendingEmail
        ON dbo.HouseholdInvitations(HouseholdID, NormalizedEmail)
        WHERE AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL;

IF COL_LENGTH(N'dbo.Pets', N'HouseholdID') IS NULL
    ALTER TABLE dbo.Pets ADD HouseholdID int NULL;

/* Attach every legacy pet to the personal household created for its owner. */
UPDATE p
SET HouseholdID = ownerHousehold.HouseholdID
FROM dbo.Pets p
CROSS APPLY
(
    SELECT TOP (1) h.HouseholdID
    FROM dbo.Households h
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = h.HouseholdID
     AND hm.UserID = p.userID AND hm.Role = N'Owner' AND hm.Status = N'Active'
    WHERE h.CreatedByUserID = p.userID
    ORDER BY h.CreatedAtUtc, h.HouseholdID
) ownerHousehold
WHERE p.HouseholdID IS NULL;

IF EXISTS (SELECT 1 FROM dbo.Pets WHERE HouseholdID IS NULL)
    THROW 51000, 'Household migration stopped because one or more pets could not be assigned.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Pets') AND name = N'HouseholdID' AND is_nullable = 1
)
    ALTER TABLE dbo.Pets ALTER COLUMN HouseholdID int NOT NULL;

IF OBJECT_ID(N'FK_Pets_Households', N'F') IS NULL
    ALTER TABLE dbo.Pets WITH CHECK ADD CONSTRAINT FK_Pets_Households
        FOREIGN KEY (HouseholdID) REFERENCES dbo.Households(HouseholdID);

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Pets') AND name = N'IX_Pets_HouseholdID'
)
    CREATE INDEX IX_Pets_HouseholdID ON dbo.Pets(HouseholdID, createdAt) INCLUDE (name, type);

IF COL_LENGTH(N'dbo.VetVisitHistory', N'ChangedByUserID') IS NULL
    ALTER TABLE dbo.VetVisitHistory ADD ChangedByUserID int NULL;

IF OBJECT_ID(N'FK_VetVisitHistory_ChangedBy', N'F') IS NULL
    ALTER TABLE dbo.VetVisitHistory WITH CHECK ADD CONSTRAINT FK_VetVisitHistory_ChangedBy
        FOREIGN KEY (ChangedByUserID) REFERENCES dbo.Users(userID);

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.VetVisitHistory') AND name = N'IX_VetVisitHistory_ChangedBy'
)
    CREATE INDEX IX_VetVisitHistory_ChangedBy ON dbo.VetVisitHistory(ChangedByUserID, ChangedAt DESC);

/* An earlier migration guessed the pet owner for pre-existing task/vet rows.
   Records older than that attribution feature could not have captured an actor,
   so remove only that identifiable synthetic backfill. Newer real attribution
   remains intact, and the UI deliberately handles NULL as unknown history. */
IF @IsInitialHouseholdMigration = 1
BEGIN
    UPDATE t
    SET RecordedByUserID = NULL
    FROM dbo.Tasks t
    INNER JOIN dbo.Pets p ON p.petID = t.petID
    WHERE t.createdAt < CONVERT(datetime2(0), N'2026-09-10T00:00:00')
      AND t.RecordedByUserID = p.userID;

    UPDATE v
    SET CreatedByUserID = NULL
    FROM dbo.VetVisits v
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    WHERE v.CreatedAt < CONVERT(datetime2(0), N'2026-09-10T00:00:00')
      AND v.CreatedByUserID = p.userID;
END;

COMMIT TRANSACTION;
GO

/* Vet visit procedures retain their established behavior but authorize through
   the active household and record the authenticated actor. */
CREATE OR ALTER PROCEDURE dbo.AddVetVisit
    @UserID                  int,
    @HouseholdID             int,
    @PetID                   int,
    @VisitDate               date,
    @VisitTime               time(0) = NULL,
    @IsAllDay                bit = 0,
    @ClinicName              nvarchar(200) = N'',
    @VeterinarianName        nvarchar(150) = N'',
    @VisitReason             nvarchar(500),
    @VisitType               nvarchar(50),
    @Location                nvarchar(400) = N'',
    @PhoneNumber             nvarchar(50) = N'',
    @Status                  nvarchar(25) = N'Scheduled',
    @Notes                   nvarchar(max) = N'',
    @FollowUpDate            date = NULL,
    @Cost                    decimal(10,2) = NULL,
    @IsEmergency             bit = 0,
    @PreparationInstructions nvarchar(2000) = N'',
    @ReminderAt              datetime2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS
       (
           SELECT 1 FROM dbo.Pets p
           INNER JOIN dbo.HouseholdMembers hm
             ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
            AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
           WHERE p.petID = @PetID AND p.HouseholdID = @HouseholdID
       )
       OR @Status NOT IN (N'Scheduled', N'Confirmed', N'Cancelled', N'Missed', N'Rescheduled')
       OR LTRIM(RTRIM(COALESCE(@ClinicName, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VisitReason, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VisitType, N''))) = N''
       OR (@FollowUpDate IS NOT NULL AND @FollowUpDate < @VisitDate)
       OR (@Cost IS NOT NULL AND @Cost < 0)
    BEGIN
        SELECT CAST(0 AS int) AS VetVisitID;
        RETURN;
    END;

    DECLARE @CombinedNotes nvarchar(max) = CASE
        WHEN LTRIM(RTRIM(COALESCE(@PreparationInstructions, N''))) = N''
            THEN LTRIM(RTRIM(COALESCE(@Notes, N'')))
        WHEN LTRIM(RTRIM(COALESCE(@Notes, N''))) = N''
            THEN LTRIM(RTRIM(@PreparationInstructions))
        ELSE CONCAT(LTRIM(RTRIM(@PreparationInstructions)), CHAR(13), CHAR(10),
                    CHAR(13), CHAR(10), LTRIM(RTRIM(@Notes))) END;

    BEGIN TRANSACTION;
    INSERT dbo.VetVisits
        (PetID, VisitDate, VisitTime, IsAllDay, ClinicName, VeterinarianName,
         VisitReason, VisitType, Location, PhoneNumber, Status, Notes,
         FollowUpDate, Cost, IsEmergency, PreparationInstructions,
         CreatedAt, UpdatedAt, IsDeleted, CreatedByUserID)
    VALUES
        (@PetID, @VisitDate, CASE WHEN @IsAllDay = 1 THEN NULL ELSE @VisitTime END,
         @IsAllDay, LTRIM(RTRIM(COALESCE(@ClinicName, N''))),
         LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))),
         LTRIM(RTRIM(@VisitReason)), LTRIM(RTRIM(@VisitType)),
         LTRIM(RTRIM(COALESCE(@Location, N''))), LTRIM(RTRIM(COALESCE(@PhoneNumber, N''))),
         @Status, @CombinedNotes, @FollowUpDate, @Cost, @IsEmergency, N'',
         SYSDATETIME(), SYSDATETIME(), 0, @UserID);

    DECLARE @VetVisitID int = CONVERT(int, SCOPE_IDENTITY());
    IF @ReminderAt IS NOT NULL AND @Status IN (N'Scheduled', N'Confirmed', N'Rescheduled')
        INSERT dbo.VetVisitReminders (VetVisitID, ReminderAt, Status, CreatedAt, UpdatedAt)
        VALUES (@VetVisitID, @ReminderAt, N'Pending', SYSDATETIME(), SYSDATETIME());

    INSERT dbo.VetVisitHistory
        (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt, ChangedByUserID)
    VALUES (@VetVisitID, N'Created', NULL, @Status, N'Visit record created.', SYSDATETIME(), @UserID);
    COMMIT TRANSACTION;
    SELECT @VetVisitID AS VetVisitID;
END;
GO

CREATE OR ALTER PROCEDURE dbo.UpdateVetVisit
    @UserID                  int,
    @HouseholdID             int,
    @VetVisitID              int,
    @PetID                   int,
    @VisitDate               date,
    @VisitTime               time(0) = NULL,
    @IsAllDay                bit = 0,
    @ClinicName              nvarchar(200) = N'',
    @VeterinarianName        nvarchar(150) = N'',
    @VisitReason             nvarchar(500),
    @VisitType               nvarchar(50),
    @Location                nvarchar(400) = N'',
    @PhoneNumber             nvarchar(50) = N'',
    @Status                  nvarchar(25),
    @Notes                   nvarchar(max) = N'',
    @FollowUpDate            date = NULL,
    @Cost                    decimal(10,2) = NULL,
    @IsEmergency             bit = 0,
    @PreparationInstructions nvarchar(2000) = N'',
    @ReminderAt              datetime2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    DECLARE @OldStatus nvarchar(25);
    SELECT @OldStatus = v.Status
    FROM dbo.VetVisits v WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
     AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
    WHERE v.VetVisitID = @VetVisitID AND p.HouseholdID = @HouseholdID AND v.IsDeleted = 0;

    IF @OldStatus IS NULL
       OR NOT EXISTS (SELECT 1 FROM dbo.Pets WHERE petID = @PetID AND HouseholdID = @HouseholdID)
       OR @Status NOT IN (N'Scheduled', N'Confirmed', N'Completed', N'Cancelled', N'Missed', N'Rescheduled')
       OR (@OldStatus = N'Completed' AND @Status <> N'Completed')
       OR (@OldStatus <> N'Completed' AND @Status = N'Completed')
       OR LTRIM(RTRIM(COALESCE(@ClinicName, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VisitReason, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VisitType, N''))) = N''
       OR (@FollowUpDate IS NOT NULL AND @FollowUpDate < @VisitDate)
       OR (@Cost IS NOT NULL AND @Cost < 0)
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS bit) AS Succeeded;
        RETURN;
    END;

    DECLARE @CombinedNotes nvarchar(max) = CASE
        WHEN LTRIM(RTRIM(COALESCE(@PreparationInstructions, N''))) = N''
            THEN LTRIM(RTRIM(COALESCE(@Notes, N'')))
        WHEN LTRIM(RTRIM(COALESCE(@Notes, N''))) = N''
            THEN LTRIM(RTRIM(@PreparationInstructions))
        ELSE CONCAT(LTRIM(RTRIM(@PreparationInstructions)), CHAR(13), CHAR(10),
                    CHAR(13), CHAR(10), LTRIM(RTRIM(@Notes))) END;

    UPDATE dbo.VetVisits
    SET PetID = @PetID, VisitDate = @VisitDate,
        VisitTime = CASE WHEN @IsAllDay = 1 THEN NULL ELSE @VisitTime END,
        IsAllDay = @IsAllDay, ClinicName = LTRIM(RTRIM(COALESCE(@ClinicName, N''))),
        VeterinarianName = LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))),
        VisitReason = LTRIM(RTRIM(@VisitReason)), VisitType = LTRIM(RTRIM(@VisitType)),
        Location = LTRIM(RTRIM(COALESCE(@Location, N''))),
        PhoneNumber = LTRIM(RTRIM(COALESCE(@PhoneNumber, N''))), Status = @Status,
        Notes = @CombinedNotes, FollowUpDate = @FollowUpDate, Cost = @Cost,
        IsEmergency = @IsEmergency, PreparationInstructions = N'', UpdatedAt = SYSDATETIME()
    WHERE VetVisitID = @VetVisitID;

    IF @ReminderAt IS NOT NULL AND @Status IN (N'Scheduled', N'Confirmed', N'Rescheduled')
    BEGIN
        UPDATE dbo.VetVisitReminders
        SET ReminderAt = @ReminderAt, Status = N'Pending', DisplayedAt = NULL,
            DismissedAt = NULL, UpdatedAt = SYSDATETIME()
        WHERE VetVisitID = @VetVisitID;
        IF @@ROWCOUNT = 0
            INSERT dbo.VetVisitReminders (VetVisitID, ReminderAt, Status, CreatedAt, UpdatedAt)
            VALUES (@VetVisitID, @ReminderAt, N'Pending', SYSDATETIME(), SYSDATETIME());
    END
    ELSE
        UPDATE dbo.VetVisitReminders SET Status = N'Cancelled', UpdatedAt = SYSDATETIME()
        WHERE VetVisitID = @VetVisitID;

    INSERT dbo.VetVisitHistory
        (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt, ChangedByUserID)
    VALUES
        (@VetVisitID, CASE WHEN @OldStatus <> @Status THEN N'Status changed' ELSE N'Updated' END,
         CASE WHEN @OldStatus <> @Status THEN @OldStatus ELSE NULL END,
         CASE WHEN @OldStatus <> @Status THEN @Status ELSE NULL END,
         CASE WHEN @OldStatus <> @Status THEN CONCAT(N'Status changed from ', @OldStatus, N' to ', @Status, N'.')
              ELSE N'Visit details updated.' END, SYSDATETIME(), @UserID);
    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

CREATE OR ALTER PROCEDURE dbo.ChangeVetVisitStatus
    @UserID int, @HouseholdID int, @VetVisitID int,
    @Status nvarchar(25), @Details nvarchar(1000) = N''
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @OldStatus nvarchar(25);
    SELECT @OldStatus = v.Status
    FROM dbo.VetVisits v WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
     AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
    WHERE v.VetVisitID = @VetVisitID AND p.HouseholdID = @HouseholdID AND v.IsDeleted = 0;
    IF @OldStatus IS NULL OR @OldStatus = N'Completed'
       OR @Status NOT IN (N'Scheduled', N'Confirmed', N'Cancelled', N'Missed', N'Rescheduled')
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS bit) AS Succeeded;
        RETURN;
    END;
    UPDATE dbo.VetVisits SET Status = @Status, UpdatedAt = SYSDATETIME() WHERE VetVisitID = @VetVisitID;
    IF @Status NOT IN (N'Scheduled', N'Confirmed', N'Rescheduled')
        UPDATE dbo.VetVisitReminders SET Status = N'Cancelled', UpdatedAt = SYSDATETIME()
        WHERE VetVisitID = @VetVisitID;
    INSERT dbo.VetVisitHistory
        (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt, ChangedByUserID)
    VALUES (@VetVisitID, N'Status changed', @OldStatus, @Status,
        CASE WHEN LTRIM(RTRIM(COALESCE(@Details, N''))) = N''
             THEN CONCAT(N'Status changed to ', @Status, N'.') ELSE LTRIM(RTRIM(@Details)) END,
        SYSDATETIME(), @UserID);
    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

CREATE OR ALTER PROCEDURE dbo.CompleteVetVisit
    @UserID int, @HouseholdID int, @VetVisitID int,
    @VisitSummary nvarchar(4000), @Diagnosis nvarchar(2000) = N'',
    @TreatmentProvided nvarchar(4000) = N'', @VaccinationsReceived nvarchar(2000) = N'',
    @Prescriptions nvarchar(2000) = N'', @FollowUpInstructions nvarchar(4000) = N'',
    @FollowUpDate date = NULL, @FinalCost decimal(10,2) = NULL,
    @AdditionalNotes nvarchar(4000) = N''
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @OldStatus nvarchar(25), @ExistingVisitDate date;
    SELECT @OldStatus = v.Status, @ExistingVisitDate = v.VisitDate
    FROM dbo.VetVisits v WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
     AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
    WHERE v.VetVisitID = @VetVisitID AND p.HouseholdID = @HouseholdID AND v.IsDeleted = 0;
    IF @OldStatus IS NULL OR @OldStatus NOT IN (N'Scheduled', N'Confirmed', N'Rescheduled')
       OR LTRIM(RTRIM(COALESCE(@VisitSummary, N''))) = N''
       OR (@FollowUpDate IS NOT NULL AND @FollowUpDate < @ExistingVisitDate)
       OR (@FinalCost IS NOT NULL AND @FinalCost < 0)
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS bit) AS Succeeded;
        RETURN;
    END;
    UPDATE dbo.VetVisits
    SET Status = N'Completed', VisitSummary = LTRIM(RTRIM(@VisitSummary)),
        Diagnosis = LTRIM(RTRIM(COALESCE(@Diagnosis, N''))),
        TreatmentProvided = LTRIM(RTRIM(COALESCE(@TreatmentProvided, N''))),
        VaccinationsReceived = LTRIM(RTRIM(COALESCE(@VaccinationsReceived, N''))),
        Prescriptions = LTRIM(RTRIM(COALESCE(@Prescriptions, N''))),
        FollowUpInstructions = LTRIM(RTRIM(COALESCE(@FollowUpInstructions, N''))),
        FollowUpDate = @FollowUpDate, Cost = COALESCE(@FinalCost, Cost),
        Notes = CASE WHEN LTRIM(RTRIM(COALESCE(@AdditionalNotes, N''))) = N'' THEN Notes
                     WHEN Notes = N'' THEN LTRIM(RTRIM(@AdditionalNotes))
                     ELSE CONCAT(Notes, CHAR(13), CHAR(10), LTRIM(RTRIM(@AdditionalNotes))) END,
        UpdatedAt = SYSDATETIME()
    WHERE VetVisitID = @VetVisitID;
    UPDATE dbo.VetVisitReminders SET Status = N'Cancelled', UpdatedAt = SYSDATETIME()
    WHERE VetVisitID = @VetVisitID;
    INSERT dbo.VetVisitHistory
        (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt, ChangedByUserID)
    VALUES (@VetVisitID, N'Completed', @OldStatus, N'Completed',
            N'Visit marked completed and medical outcome recorded.', SYSDATETIME(), @UserID);
    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

CREATE OR ALTER PROCEDURE dbo.DeleteVetVisit
    @UserID int, @HouseholdID int, @VetVisitID int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.VetVisits v WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.Pets p ON p.petID = v.PetID
        INNER JOIN dbo.HouseholdMembers hm
          ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
         AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
        WHERE v.VetVisitID = @VetVisitID AND p.HouseholdID = @HouseholdID AND v.IsDeleted = 0
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS bit) AS Succeeded;
        RETURN;
    END;
    DELETE dbo.VetVisitReminders WHERE VetVisitID = @VetVisitID;
    DELETE dbo.VetVisitDocuments WHERE VetVisitID = @VetVisitID;
    DELETE dbo.VetVisitHistory WHERE VetVisitID = @VetVisitID;
    DELETE dbo.VetVisits WHERE VetVisitID = @VetVisitID;
    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

/* Verification: every count should be zero; final result preserves row totals for review. */
SELECT N'Pets without a household' AS CheckName, COUNT_BIG(*) AS FailureCount
FROM dbo.Pets WHERE HouseholdID IS NULL
UNION ALL
SELECT N'Users without an active household', COUNT_BIG(*)
FROM dbo.Users u WHERE NOT EXISTS
    (SELECT 1 FROM dbo.HouseholdMembers hm WHERE hm.UserID = u.userID AND hm.Status = N'Active')
UNION ALL
SELECT N'Households without an active owner', COUNT_BIG(*)
FROM dbo.Households h WHERE NOT EXISTS
    (SELECT 1 FROM dbo.HouseholdMembers hm
     WHERE hm.HouseholdID = h.HouseholdID AND hm.Status = N'Active' AND hm.Role = N'Owner');

SELECT (SELECT COUNT_BIG(*) FROM dbo.Users) AS UserCount,
       (SELECT COUNT_BIG(*) FROM dbo.Pets) AS PetCount,
       (SELECT COUNT_BIG(*) FROM dbo.Tasks) AS TaskCount,
       (SELECT COUNT_BIG(*) FROM dbo.Medications) AS MedicationCount,
       (SELECT COUNT_BIG(*) FROM dbo.MedicationSchedule) AS MedicationScheduleCount,
       (SELECT COUNT_BIG(*) FROM dbo.HealthEvents) AS HealthEventCount,
       (SELECT COUNT_BIG(*) FROM dbo.VetVisits) AS VetVisitCount;
GO
