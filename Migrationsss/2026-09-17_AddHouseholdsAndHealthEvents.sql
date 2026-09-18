/*
    2026-09-17 — Households, Health events, and care attribution (schema only)

    Adds the household/family-sharing data model and the structured Health
    (symptoms/incidents) event table, plus additive attribution/adherence
    columns on existing care tables. No stored procedures are created here —
    see 2026-09-17_AddHouseholdHealthStoredProcedures.sql for those.

    Safe to run repeatedly (every step is guarded) and safe to run against a
    database that already has some of this schema in place by hand — every
    CREATE/ALTER is conditional on the object not already existing in the
    expected shape. Nothing here deletes a column, drops a table, or removes
    existing rows. Pets.userID (legacy single-owner column) is left in place
    intentionally; see Part 4 of the household MVP for why.

    Run this before 2026-09-17_AddHouseholdHealthStoredProcedures.sql.
*/
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================
-- 1. Households
-- ============================================================
IF OBJECT_ID(N'dbo.Households', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Households
    (
        HouseholdID     INT IDENTITY(1,1) NOT NULL,
        PublicID        UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Households_PublicID DEFAULT (NEWSEQUENTIALID()),
        Name            NVARCHAR(300) NOT NULL,
        CreatedByUserID INT NOT NULL,
        CreatedAtUtc    DATETIME2(6) NOT NULL CONSTRAINT DF_Households_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc    DATETIME2(6) NOT NULL CONSTRAINT DF_Households_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Households PRIMARY KEY CLUSTERED (HouseholdID),
        CONSTRAINT CK_Households_Name CHECK (LEN(LTRIM(RTRIM(Name))) BETWEEN 1 AND 150),
        CONSTRAINT FK_Households_CreatedBy FOREIGN KEY (CreatedByUserID) REFERENCES dbo.Users(userID)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Households') AND name = N'UX_Households_PublicID')
    CREATE UNIQUE INDEX UX_Households_PublicID ON dbo.Households(PublicID);
GO

-- ============================================================
-- 2. HouseholdMembers
-- ============================================================
IF OBJECT_ID(N'dbo.HouseholdMembers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HouseholdMembers
    (
        HouseholdID     INT NOT NULL,
        UserID          INT NOT NULL,
        Role            NVARCHAR(40) NOT NULL,
        Status          NVARCHAR(40) NOT NULL CONSTRAINT DF_HouseholdMembers_Status DEFAULT (N'Active'),
        JoinedAtUtc     DATETIME2(6) NOT NULL CONSTRAINT DF_HouseholdMembers_JoinedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc    DATETIME2(6) NOT NULL CONSTRAINT DF_HouseholdMembers_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        RemovedAtUtc    DATETIME2(6) NULL,
        RemovedByUserID INT NULL,
        CONSTRAINT PK_HouseholdMembers PRIMARY KEY CLUSTERED (HouseholdID, UserID),
        CONSTRAINT CK_HouseholdMembers_Role CHECK (Role IN (N'Owner', N'Member', N'Caregiver')),
        CONSTRAINT CK_HouseholdMembers_Status CHECK (Status IN (N'Active', N'Removed')),
        CONSTRAINT CK_HouseholdMembers_Removal CHECK ((Status = N'Active' AND RemovedAtUtc IS NULL) OR Status = N'Removed'),
        CONSTRAINT FK_HouseholdMembers_Households FOREIGN KEY (HouseholdID) REFERENCES dbo.Households(HouseholdID),
        CONSTRAINT FK_HouseholdMembers_Users FOREIGN KEY (UserID) REFERENCES dbo.Users(userID),
        CONSTRAINT FK_HouseholdMembers_RemovedBy FOREIGN KEY (RemovedByUserID) REFERENCES dbo.Users(userID)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.HouseholdMembers') AND name = N'IX_HouseholdMembers_User_Status')
    CREATE INDEX IX_HouseholdMembers_User_Status ON dbo.HouseholdMembers(UserID, Status) INCLUDE (Role, JoinedAtUtc);
GO

-- ============================================================
-- 3. HouseholdInvitations
-- ============================================================
IF OBJECT_ID(N'dbo.HouseholdInvitations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HouseholdInvitations
    (
        HouseholdInvitationID INT IDENTITY(1,1) NOT NULL,
        PublicID              UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_HouseholdInvitations_PublicID DEFAULT (NEWSEQUENTIALID()),
        HouseholdID           INT NOT NULL,
        Email                 NVARCHAR(640) NOT NULL,
        NormalizedEmail       NVARCHAR(640) NOT NULL,
        Role                  NVARCHAR(40) NOT NULL,
        TokenHash             VARBINARY(32) NOT NULL,
        InvitedByUserID       INT NOT NULL,
        CreatedAtUtc          DATETIME2(6) NOT NULL CONSTRAINT DF_HouseholdInvitations_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ExpiresAtUtc          DATETIME2(6) NOT NULL,
        AcceptedAtUtc         DATETIME2(6) NULL,
        AcceptedByUserID      INT NULL,
        RevokedAtUtc          DATETIME2(6) NULL,
        RevokedByUserID       INT NULL,
        LastSentAtUtc         DATETIME2(6) NULL,
        CONSTRAINT PK_HouseholdInvitations PRIMARY KEY CLUSTERED (HouseholdInvitationID),
        CONSTRAINT CK_HouseholdInvitations_Role CHECK (Role IN (N'Member', N'Caregiver')),
        CONSTRAINT CK_HouseholdInvitations_Expiry CHECK (ExpiresAtUtc > CreatedAtUtc),
        CONSTRAINT CK_HouseholdInvitations_AcceptedBy CHECK
            ((AcceptedAtUtc IS NULL AND AcceptedByUserID IS NULL) OR (AcceptedAtUtc IS NOT NULL AND AcceptedByUserID IS NOT NULL)),
        CONSTRAINT CK_HouseholdInvitations_RevokedBy CHECK (RevokedAtUtc IS NULL OR RevokedByUserID IS NOT NULL),
        CONSTRAINT CK_HouseholdInvitations_FinalState CHECK (NOT (AcceptedAtUtc IS NOT NULL AND RevokedAtUtc IS NOT NULL)),
        CONSTRAINT FK_HouseholdInvitations_Households FOREIGN KEY (HouseholdID) REFERENCES dbo.Households(HouseholdID),
        CONSTRAINT FK_HouseholdInvitations_InvitedBy FOREIGN KEY (InvitedByUserID) REFERENCES dbo.Users(userID),
        CONSTRAINT FK_HouseholdInvitations_AcceptedBy FOREIGN KEY (AcceptedByUserID) REFERENCES dbo.Users(userID),
        CONSTRAINT FK_HouseholdInvitations_RevokedBy FOREIGN KEY (RevokedByUserID) REFERENCES dbo.Users(userID)
    );
END;
GO

-- Additive column for installs created before resend throttling existed.
IF COL_LENGTH(N'dbo.HouseholdInvitations', N'LastSentAtUtc') IS NULL
    ALTER TABLE dbo.HouseholdInvitations ADD LastSentAtUtc DATETIME2(6) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.HouseholdInvitations') AND name = N'UX_HouseholdInvitations_PublicID')
    CREATE UNIQUE INDEX UX_HouseholdInvitations_PublicID ON dbo.HouseholdInvitations(PublicID);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.HouseholdInvitations') AND name = N'UX_HouseholdInvitations_TokenHash')
    CREATE UNIQUE INDEX UX_HouseholdInvitations_TokenHash ON dbo.HouseholdInvitations(TokenHash);
GO

-- Only one *pending* (not yet accepted or revoked) invitation per household+email;
-- an expired-but-unrevoked invite still occupies this slot on purpose — CreateInvitation
-- renews that row in place rather than inserting a duplicate.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.HouseholdInvitations') AND name = N'UX_HouseholdInvitations_PendingEmail')
    CREATE UNIQUE INDEX UX_HouseholdInvitations_PendingEmail ON dbo.HouseholdInvitations(HouseholdID, NormalizedEmail)
        WHERE AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL;
GO

-- ============================================================
-- 4. Users.PublicID — opaque identifier, formalized for parity with the
--    Households/HouseholdInvitations opaque-ID pattern. Not yet required by
--    any URL in this release (session-based selection is used instead), but
--    safe, cheap, and available for a future safer routing/share-link need.
-- ============================================================
IF COL_LENGTH(N'dbo.Users', N'PublicID') IS NULL
    ALTER TABLE dbo.Users ADD PublicID UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Users_PublicID DEFAULT (NEWSEQUENTIALID());
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Users') AND name = N'UX_Users_PublicID')
    CREATE UNIQUE INDEX UX_Users_PublicID ON dbo.Users(PublicID);
GO

-- ============================================================
-- 5. Pets.HouseholdID — staged additive migration (nullable -> backfill -> NOT NULL)
-- ============================================================
IF COL_LENGTH(N'dbo.Pets', N'HouseholdID') IS NULL
    ALTER TABLE dbo.Pets ADD HouseholdID INT NULL;
GO

-- ============================================================
-- 6. Backfill: one household per existing user, that user as Owner,
--    then attach each user's pets to their own household. Deterministic
--    and rerunnable — every step only acts on rows that still need it.
-- ============================================================

-- 6a. One household per user who does not already have an active membership anywhere.
INSERT INTO dbo.Households (Name, CreatedByUserID)
SELECT LTRIM(RTRIM(u.name)) + N' Household', u.userID
FROM dbo.Users u
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.HouseholdMembers hm
    WHERE hm.UserID = u.userID AND hm.Status = N'Active'
);
GO

-- 6b. That user becomes Owner of the household(s) they created that they're not already a member of.
INSERT INTO dbo.HouseholdMembers (HouseholdID, UserID, Role, Status)
SELECT h.HouseholdID, h.CreatedByUserID, N'Owner', N'Active'
FROM dbo.Households h
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.HouseholdMembers hm
    WHERE hm.HouseholdID = h.HouseholdID AND hm.UserID = h.CreatedByUserID
);
GO

-- 6c. Attach each pet to the household where its current owner (legacy Pets.userID) is Owner.
-- Pets.userID has an existing NOT NULL FK to Users, so every pet's owner is a valid user and
-- step 6a/6b guarantees that owner has exactly one Owner-household — this join is unambiguous.
UPDATE p
SET p.HouseholdID = hm.HouseholdID
FROM dbo.Pets p
INNER JOIN dbo.HouseholdMembers hm
    ON hm.UserID = p.userID AND hm.Role = N'Owner' AND hm.Status = N'Active'
WHERE p.HouseholdID IS NULL;
GO

-- 6d. Only tighten to NOT NULL / add the FK+index once every row has been backfilled.
-- If any Pets.HouseholdID remain NULL (should not happen given Pets.userID's existing FK,
-- but checked defensively per the "do not silently assign ambiguous records" requirement),
-- this step is skipped and 2026-09-17_VerifyHouseholdHealthMvp.sql reports it as a failure
-- instead of the migration guessing.
IF NOT EXISTS (SELECT 1 FROM dbo.Pets WHERE HouseholdID IS NULL)
BEGIN
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Pets') AND name = N'HouseholdID' AND is_nullable = 1)
        ALTER TABLE dbo.Pets ALTER COLUMN HouseholdID INT NOT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Pets_Households')
    ALTER TABLE dbo.Pets ADD CONSTRAINT FK_Pets_Households FOREIGN KEY (HouseholdID) REFERENCES dbo.Households(HouseholdID);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Pets') AND name = N'IX_Pets_HouseholdID')
    CREATE INDEX IX_Pets_HouseholdID ON dbo.Pets(HouseholdID) INCLUDE (name, type, createdAt);
GO

-- ============================================================
-- 7. HealthEvents — shared table for Symptom and Incident records.
-- ============================================================
IF OBJECT_ID(N'dbo.HealthEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HealthEvents
    (
        HealthEventID         INT IDENTITY(1,1) NOT NULL,
        PetID                 INT NOT NULL,
        EventKind             NVARCHAR(40) NOT NULL,
        EventType             NVARCHAR(200) NOT NULL,
        OccurredAtUtc         DATETIME2(6) NOT NULL,
        EndedAtUtc            DATETIME2(6) NULL,
        Severity              TINYINT NULL,
        Description           NVARCHAR(4000) NOT NULL CONSTRAINT DF_HealthEvents_Description DEFAULT (N''),
        PossibleTrigger       NVARCHAR(1000) NOT NULL CONSTRAINT DF_HealthEvents_PossibleTrigger DEFAULT (N''),
        AppetiteStatus        NVARCHAR(60) NOT NULL CONSTRAINT DF_HealthEvents_AppetiteStatus DEFAULT (N''),
        DrinkingStatus        NVARCHAR(60) NOT NULL CONSTRAINT DF_HealthEvents_DrinkingStatus DEFAULT (N''),
        RelatedMedicationID   INT NULL,
        RecoveryStatus        NVARCHAR(60) NOT NULL CONSTRAINT DF_HealthEvents_RecoveryStatus DEFAULT (N''),
        RecoveredAtUtc        DATETIME2(6) NULL,
        VeterinarianContacted BIT NOT NULL CONSTRAINT DF_HealthEvents_VetContacted DEFAULT (0),
        CreatedByUserID       INT NOT NULL,
        CreatedAtUtc          DATETIME2(6) NOT NULL CONSTRAINT DF_HealthEvents_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc          DATETIME2(6) NOT NULL CONSTRAINT DF_HealthEvents_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        IsDeleted             BIT NOT NULL CONSTRAINT DF_HealthEvents_IsDeleted DEFAULT (0),
        DeletedAtUtc          DATETIME2(6) NULL,
        DeletedByUserID       INT NULL,
        CONSTRAINT PK_HealthEvents PRIMARY KEY CLUSTERED (HealthEventID),
        CONSTRAINT CK_HealthEvents_EventKind CHECK (EventKind IN (N'Symptom', N'Incident')),
        CONSTRAINT CK_HealthEvents_Severity CHECK (Severity IS NULL OR Severity BETWEEN 1 AND 5),
        CONSTRAINT CK_HealthEvents_End CHECK (EndedAtUtc IS NULL OR EndedAtUtc >= OccurredAtUtc),
        CONSTRAINT CK_HealthEvents_Recovery CHECK (RecoveredAtUtc IS NULL OR RecoveredAtUtc >= OccurredAtUtc),
        CONSTRAINT FK_HealthEvents_Pets FOREIGN KEY (PetID) REFERENCES dbo.Pets(petID) ON DELETE CASCADE,
        CONSTRAINT FK_HealthEvents_RelatedMedication FOREIGN KEY (RelatedMedicationID) REFERENCES dbo.Medications(medID) ON DELETE SET NULL,
        CONSTRAINT FK_HealthEvents_CreatedBy FOREIGN KEY (CreatedByUserID) REFERENCES dbo.Users(userID),
        CONSTRAINT FK_HealthEvents_DeletedBy FOREIGN KEY (DeletedByUserID) REFERENCES dbo.Users(userID)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.HealthEvents') AND name = N'IX_HealthEvents_Pet_OccurredAtUtc')
    CREATE INDEX IX_HealthEvents_Pet_OccurredAtUtc ON dbo.HealthEvents(PetID, OccurredAtUtc)
        INCLUDE (EventKind, EventType, Severity, RecoveryStatus, CreatedByUserID);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.HealthEvents') AND name = N'IX_HealthEvents_CreatedBy_CreatedAtUtc')
    CREATE INDEX IX_HealthEvents_CreatedBy_CreatedAtUtc ON dbo.HealthEvents(CreatedByUserID, CreatedAtUtc);
GO

-- ============================================================
-- 8. Care attribution columns (additive, nullable — historical rows stay NULL)
-- ============================================================
IF COL_LENGTH(N'dbo.Tasks', N'RecordedByUserID') IS NULL
    ALTER TABLE dbo.Tasks ADD RecordedByUserID INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Tasks_RecordedBy')
    ALTER TABLE dbo.Tasks ADD CONSTRAINT FK_Tasks_RecordedBy FOREIGN KEY (RecordedByUserID) REFERENCES dbo.Users(userID);
GO

IF COL_LENGTH(N'dbo.VetVisits', N'CreatedByUserID') IS NULL
    ALTER TABLE dbo.VetVisits ADD CreatedByUserID INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_VetVisits_CreatedBy')
    ALTER TABLE dbo.VetVisits ADD CONSTRAINT FK_VetVisits_CreatedBy FOREIGN KEY (CreatedByUserID) REFERENCES dbo.Users(userID);
GO

-- ============================================================
-- 9. Medication administration/adherence columns (additive)
-- ============================================================
IF COL_LENGTH(N'dbo.MedicationSchedule', N'DoseStatus') IS NULL
    ALTER TABLE dbo.MedicationSchedule ADD DoseStatus NVARCHAR(40) NOT NULL CONSTRAINT DF_MedicationSchedule_DoseStatus DEFAULT (N'Due');
GO
IF COL_LENGTH(N'dbo.MedicationSchedule', N'AdministeredAtUtc') IS NULL
    ALTER TABLE dbo.MedicationSchedule ADD AdministeredAtUtc DATETIME2(6) NULL;
GO
IF COL_LENGTH(N'dbo.MedicationSchedule', N'RecordedAtUtc') IS NULL
    ALTER TABLE dbo.MedicationSchedule ADD RecordedAtUtc DATETIME2(6) NULL;
GO
IF COL_LENGTH(N'dbo.MedicationSchedule', N'RecordedByUserID') IS NULL
    ALTER TABLE dbo.MedicationSchedule ADD RecordedByUserID INT NULL;
GO
IF COL_LENGTH(N'dbo.MedicationSchedule', N'StatusReason') IS NULL
    ALTER TABLE dbo.MedicationSchedule ADD StatusReason NVARCHAR(1000) NULL;
GO
IF COL_LENGTH(N'dbo.MedicationSchedule', N'AdministrationNotes') IS NULL
    ALTER TABLE dbo.MedicationSchedule ADD AdministrationNotes NVARCHAR(2000) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_MedicationSchedule_DoseStatus')
    ALTER TABLE dbo.MedicationSchedule ADD CONSTRAINT CK_MedicationSchedule_DoseStatus
        CHECK (DoseStatus IN (N'Due', N'Taken', N'Taken late', N'Skipped', N'Missed'));
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_MedicationSchedule_RecordedBy')
    ALTER TABLE dbo.MedicationSchedule ADD CONSTRAINT FK_MedicationSchedule_RecordedBy FOREIGN KEY (RecordedByUserID) REFERENCES dbo.Users(userID);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.MedicationSchedule') AND name = N'IX_MedicationSchedule_Med_Status_Date')
    CREATE INDEX IX_MedicationSchedule_Med_Status_Date ON dbo.MedicationSchedule(medID, DoseStatus, scheduleDate)
        INCLUDE (isConfirmed, confirmedAt, AdministeredAtUtc, RecordedByUserID);
GO

-- Backfill: a legacy confirmed dose becomes "Taken" (never invent a historical UTC time).
UPDATE dbo.MedicationSchedule
SET DoseStatus = N'Taken'
WHERE isConfirmed = 1 AND DoseStatus = N'Due';
GO
