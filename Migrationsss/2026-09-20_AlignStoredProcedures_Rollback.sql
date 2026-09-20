/* =============================================================================
   2026-09-20_AlignStoredProcedures_Rollback.sql

   PURPOSE
     Undoes 2026-09-20_AlignStoredProcedures.sql:
       * drops the 26 procedures it created, and
       * restores the 26 procedures it changed to the exact definitions that were
         deployed in PetPottyDb_Test on 2026-09-20 (before that script ran). Those
         definitions were not in source control until now.

   KNOWN CONSEQUENCES OF ROLLING BACK
     * AddUser goes back to SET NOCOUNT ON, so Signup reports every successful signup
       as "Failed to create account" again (ExecuteNonQuery() returns -1).
     * Vet-visit history rows are written without ChangedByUserID again.
     * UpdatePet / AddPet allow the Member role again (the app's rule is Owner only).
     * UpdateUser truncates name / userName / email to 50 characters again.

   HOW TO RUN
     SSMS : open, choose the database, Execute.
     sqlcmd: sqlcmd -S <server> -d <database> -E -b -i 2026-09-20_AlignStoredProcedures_Rollback.sql
   ============================================================================= */

SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------- 1. restore the previous definitions ---------- */

-- [RESTORE] GetPetsByHouseholdID
-- ============================================================
-- PET PROCEDURES (household-aware)
-- ============================================================

CREATE OR ALTER PROCEDURE dbo.GetPetsByHouseholdID
    @UserID INT, @HouseholdID INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.HouseholdMembers WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active')
        RETURN;

    SELECT p.petID, p.userID, p.HouseholdID, p.[name], p.[type], p.breed, p.age,
           p.birthdate, p.gender, p.createdAt, p.ProfileImagePath
    FROM dbo.Pets p
    WHERE p.HouseholdID = @HouseholdID
    ORDER BY p.createdAt;
END;
GO

-- [RESTORE] GetTasksByPetID
CREATE OR ALTER PROCEDURE dbo.GetTasksByPetID
    @UserID INT, @HouseholdID INT, @petID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Pets p INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
        WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID
    )
        RETURN;

    SELECT t.taskID, t.petID, p.name AS petName, t.taskType, t.notes, t.createdAt,
           t.RecordedByUserID, u.name AS RecordedByName
    FROM Tasks t
    INNER JOIN Pets p ON t.petID = p.petID
    LEFT JOIN dbo.Users u ON u.userID = t.RecordedByUserID
    WHERE t.petID = @petID
    ORDER BY t.createdAt DESC;
END;
GO

-- [RESTORE] GetTasksByPetID_Recent
CREATE OR ALTER PROCEDURE dbo.GetTasksByPetID_Recent
    @UserID INT, @HouseholdID INT, @petID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Pets p INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
        WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID
    )
        RETURN;

    SELECT t.taskID, t.petID, p.name AS petName, t.taskType, t.notes, t.createdAt,
           t.RecordedByUserID, u.name AS RecordedByName
    FROM Tasks t
    INNER JOIN Pets p ON t.petID = p.petID
    LEFT JOIN dbo.Users u ON u.userID = t.RecordedByUserID
    WHERE p.petID = @petID
      AND t.createdAt >= DATEADD(HOUR, -36, GETDATE())
    ORDER BY t.createdAt DESC;
END;
GO

-- [RESTORE] AddPet
CREATE OR ALTER PROCEDURE dbo.AddPet
    @UserID INT, @HouseholdID INT,
    @name NVARCHAR(255), @type NVARCHAR(255), @breed NVARCHAR(255),
    @age NVARCHAR(50), @birthdate DATETIME, @gender NVARCHAR(50), @createdAt DATETIME
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers WITH (UPDLOCK, HOLDLOCK)
        WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active' AND Role IN (N'Owner', N'Member')
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS INT) AS petID;
        RETURN;
    END;

    INSERT INTO dbo.Pets (userID, HouseholdID, [name], [type], breed, age, birthdate, gender, createdAt)
    VALUES (@UserID, @HouseholdID, @name, @type, @breed, @age, @birthdate, @gender, @createdAt);

    DECLARE @NewID INT = CONVERT(INT, SCOPE_IDENTITY());
    COMMIT TRANSACTION;
    SELECT @NewID AS petID;
END;
GO

-- [RESTORE] UpdatePet
-- NOTE: @age is INT here (not NVARCHAR) - matches this procedure's existing,
-- pre-existing signature. AddPet accepts a free-text age; UpdatePet has always
-- required a numeric one. Preserved as-is; not introduced by this migration.
CREATE OR ALTER PROCEDURE dbo.UpdatePet
    @UserID INT, @HouseholdID INT,
    @petID INT, @name VARCHAR(100), @type VARCHAR(50), @breed VARCHAR(50),
    @age INT, @birthDate DATE, @gender VARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    UPDATE p
    SET name = @name, type = @type, breed = @breed, age = @age, birthDate = @birthDate, gender = @gender
    FROM dbo.Pets p WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
       AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
    WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [RESTORE] GetHouseholdContext
-- Returns exactly one row when @UserID is currently an active member of
-- @HouseholdID (used to revalidate the session-stored active household on
-- every request); no rows otherwise.
CREATE OR ALTER PROCEDURE dbo.GetHouseholdContext
    @UserID INT,
    @HouseholdID INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT h.HouseholdID, h.Name, hm.Role
    FROM dbo.HouseholdMembers hm
    INNER JOIN dbo.Households h ON h.HouseholdID = hm.HouseholdID
    WHERE hm.UserID = @UserID AND hm.HouseholdID = @HouseholdID AND hm.Status = N'Active';
END;
GO

-- [RESTORE] CreateDefaultHouseholdForUser
-- ============================================================
-- HOUSEHOLD PROCEDURES
-- ============================================================

-- Idempotent: returns the user's existing household if they already have one
-- (Owner-created or joined via invitation), otherwise creates a new one and
-- makes the user its Owner. Used both by the one-time backfill migration and
-- as a safety net right after Signup.
CREATE OR ALTER PROCEDURE dbo.CreateDefaultHouseholdForUser
    @UserID INT,
    @UserName NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @HouseholdID INT;
    BEGIN TRANSACTION;

    SELECT TOP (1) @HouseholdID = hm.HouseholdID
    FROM dbo.HouseholdMembers hm WITH (UPDLOCK, HOLDLOCK)
    WHERE hm.UserID = @UserID AND hm.Status = N'Active'
    ORDER BY hm.HouseholdID;

    IF @HouseholdID IS NULL
    BEGIN
        INSERT dbo.Households (Name, CreatedByUserID)
        VALUES (LTRIM(RTRIM(@UserName)) + N' Household', @UserID);
        SET @HouseholdID = CONVERT(INT, SCOPE_IDENTITY());

        INSERT dbo.HouseholdMembers (HouseholdID, UserID, Role, Status)
        VALUES (@HouseholdID, @UserID, N'Owner', N'Active');
    END;

    COMMIT TRANSACTION;
    SELECT @HouseholdID AS HouseholdID;
END;
GO

-- [RESTORE] GetHouseholdsForUser
CREATE OR ALTER PROCEDURE dbo.GetHouseholdsForUser
    @UserID INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT h.HouseholdID, h.Name, hm.Role, hm.JoinedAtUtc
    FROM dbo.HouseholdMembers hm
    INNER JOIN dbo.Households h ON h.HouseholdID = hm.HouseholdID
    WHERE hm.UserID = @UserID AND hm.Status = N'Active'
    ORDER BY hm.JoinedAtUtc, h.HouseholdID;
END;
GO

-- [RESTORE] GetHouseholdMembers
CREATE OR ALTER PROCEDURE dbo.GetHouseholdMembers
    @UserID INT,
    @HouseholdID INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers
        WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active'
    )
        RETURN;

    SELECT hm.UserID, u.name AS Name, u.email AS Email, hm.Role, hm.JoinedAtUtc
    FROM dbo.HouseholdMembers hm
    INNER JOIN dbo.Users u ON u.userID = hm.UserID
    WHERE hm.HouseholdID = @HouseholdID AND hm.Status = N'Active'
    ORDER BY CASE hm.Role WHEN N'Owner' THEN 0 WHEN N'Member' THEN 1 ELSE 2 END, hm.JoinedAtUtc;
END;
GO

-- [RESTORE] GetPendingHouseholdInvitations
CREATE OR ALTER PROCEDURE dbo.GetPendingHouseholdInvitations
    @UserID INT,
    @HouseholdID INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers
        WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active' AND Role = N'Owner'
    )
        RETURN;

    SELECT i.HouseholdInvitationID, i.Email, i.Role, i.CreatedAtUtc, i.ExpiresAtUtc, i.LastSentAtUtc,
           inviter.name AS InvitedByName,
           CAST(CASE WHEN i.ExpiresAtUtc <= SYSUTCDATETIME() THEN 1 ELSE 0 END AS BIT) AS IsExpired
    FROM dbo.HouseholdInvitations i
    INNER JOIN dbo.Users inviter ON inviter.userID = i.InvitedByUserID
    WHERE i.HouseholdID = @HouseholdID AND i.AcceptedAtUtc IS NULL AND i.RevokedAtUtc IS NULL
    ORDER BY i.CreatedAtUtc DESC;
END;
GO

-- [RESTORE] UpdateHouseholdMemberRole
-- Owner only. Can never set/target the Owner role - promotion/demotion of
-- ownership is out of scope for this MVP.
CREATE OR ALTER PROCEDURE dbo.UpdateHouseholdMemberRole
    @UserID INT,
    @HouseholdID INT,
    @TargetUserID INT,
    @NewRole NVARCHAR(40)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @NewRole NOT IN (N'Member', N'Caregiver')
    BEGIN
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers WITH (UPDLOCK, HOLDLOCK)
        WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active' AND Role = N'Owner'
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    UPDATE dbo.HouseholdMembers
    SET Role = @NewRole, UpdatedAtUtc = SYSUTCDATETIME()
    WHERE HouseholdID = @HouseholdID AND UserID = @TargetUserID
      AND Status = N'Active' AND Role <> N'Owner';

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [RESTORE] RemoveHouseholdMember
-- Owner only. Soft-removes (Status='Removed'); can never remove an Owner,
-- which also means an Owner can never remove themselves through this path.
CREATE OR ALTER PROCEDURE dbo.RemoveHouseholdMember
    @UserID INT,
    @HouseholdID INT,
    @TargetUserID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers WITH (UPDLOCK, HOLDLOCK)
        WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active' AND Role = N'Owner'
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    UPDATE dbo.HouseholdMembers
    SET Status = N'Removed', RemovedAtUtc = SYSUTCDATETIME(), RemovedByUserID = @UserID, UpdatedAtUtc = SYSUTCDATETIME()
    WHERE HouseholdID = @HouseholdID AND UserID = @TargetUserID
      AND Status = N'Active' AND Role <> N'Owner';

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [RESTORE] CreateHouseholdInvitation
-- ============================================================
-- INVITATION PROCEDURES
-- ============================================================

-- ResultCode: Created | AlreadyPending | AlreadyMember | InvalidRole | NotAuthorized
-- The unique index on (HouseholdID, NormalizedEmail) only covers *pending*
-- (unaccepted, unrevoked) rows, but does not exclude an expired one - an
-- expired-but-unrevoked invite still occupies that slot, so this proc renews
-- it in place instead of inserting a duplicate that would violate the index.
CREATE OR ALTER PROCEDURE dbo.CreateHouseholdInvitation
    @UserID INT,
    @HouseholdID INT,
    @Email NVARCHAR(320),
    @NormalizedEmail NVARCHAR(320),
    @Role NVARCHAR(40),
    @TokenHash VARBINARY(32),
    @ExpiresAtUtc DATETIME2(6)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Role NOT IN (N'Member', N'Caregiver')
    BEGIN
        SELECT CAST(0 AS INT) AS HouseholdInvitationID, N'InvalidRole' AS ResultCode;
        RETURN;
    END;

    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers WITH (UPDLOCK, HOLDLOCK)
        WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active' AND Role = N'Owner'
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS INT) AS HouseholdInvitationID, N'NotAuthorized' AS ResultCode;
        RETURN;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM dbo.HouseholdMembers hm
        INNER JOIN dbo.Users u ON u.userID = hm.UserID
        WHERE hm.HouseholdID = @HouseholdID AND hm.Status = N'Active'
          AND LOWER(LTRIM(RTRIM(u.email))) = @NormalizedEmail
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS INT) AS HouseholdInvitationID, N'AlreadyMember' AS ResultCode;
        RETURN;
    END;

    DECLARE @ExistingID INT, @ExistingExpires DATETIME2(6);
    SELECT TOP (1) @ExistingID = HouseholdInvitationID, @ExistingExpires = ExpiresAtUtc
    FROM dbo.HouseholdInvitations WITH (UPDLOCK, HOLDLOCK)
    WHERE HouseholdID = @HouseholdID AND NormalizedEmail = @NormalizedEmail
      AND AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL;

    IF @ExistingID IS NOT NULL AND @ExistingExpires > SYSUTCDATETIME()
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT @ExistingID AS HouseholdInvitationID, N'AlreadyPending' AS ResultCode;
        RETURN;
    END;

    IF @ExistingID IS NOT NULL
    BEGIN
        UPDATE dbo.HouseholdInvitations
        SET Role = @Role, TokenHash = @TokenHash, InvitedByUserID = @UserID,
            Email = @Email, CreatedAtUtc = SYSUTCDATETIME(), ExpiresAtUtc = @ExpiresAtUtc,
            LastSentAtUtc = NULL
        WHERE HouseholdInvitationID = @ExistingID;

        COMMIT TRANSACTION;
        SELECT @ExistingID AS HouseholdInvitationID, N'Created' AS ResultCode;
        RETURN;
    END;

    INSERT dbo.HouseholdInvitations
        (HouseholdID, Email, NormalizedEmail, Role, TokenHash, InvitedByUserID, ExpiresAtUtc)
    VALUES
        (@HouseholdID, @Email, @NormalizedEmail, @Role, @TokenHash, @UserID, @ExpiresAtUtc);

    DECLARE @NewID INT = CONVERT(INT, SCOPE_IDENTITY());
    COMMIT TRANSACTION;
    SELECT @NewID AS HouseholdInvitationID, N'Created' AS ResultCode;
END;
GO

-- [RESTORE] ResendHouseholdInvitation
-- ResultCode: Sent | NotAuthorized | NotPending | TooSoon
CREATE OR ALTER PROCEDURE dbo.ResendHouseholdInvitation
    @UserID INT,
    @HouseholdID INT,
    @HouseholdInvitationID INT,
    @NewTokenHash VARBINARY(32),
    @NewExpiresAtUtc DATETIME2(6),
    @MinResendIntervalSeconds INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers WITH (UPDLOCK, HOLDLOCK)
        WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active' AND Role = N'Owner'
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'NotAuthorized' AS ResultCode;
        RETURN;
    END;

    DECLARE @LastSent DATETIME2(6), @Accepted DATETIME2(6), @Revoked DATETIME2(6);
    SELECT @LastSent = LastSentAtUtc, @Accepted = AcceptedAtUtc, @Revoked = RevokedAtUtc
    FROM dbo.HouseholdInvitations WITH (UPDLOCK, HOLDLOCK)
    WHERE HouseholdInvitationID = @HouseholdInvitationID AND HouseholdID = @HouseholdID;

    IF @Accepted IS NOT NULL OR @Revoked IS NOT NULL
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'NotPending' AS ResultCode;
        RETURN;
    END;

    IF @LastSent IS NOT NULL AND DATEDIFF(SECOND, @LastSent, SYSUTCDATETIME()) < @MinResendIntervalSeconds
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'TooSoon' AS ResultCode;
        RETURN;
    END;

    UPDATE dbo.HouseholdInvitations
    SET TokenHash = @NewTokenHash, ExpiresAtUtc = @NewExpiresAtUtc, LastSentAtUtc = SYSUTCDATETIME()
    WHERE HouseholdInvitationID = @HouseholdInvitationID;

    COMMIT TRANSACTION;
    SELECT CAST(1 AS BIT) AS Succeeded, N'Sent' AS ResultCode;
END;
GO

-- [RESTORE] RevokeHouseholdInvitation
CREATE OR ALTER PROCEDURE dbo.RevokeHouseholdInvitation
    @UserID INT,
    @HouseholdID INT,
    @HouseholdInvitationID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers WITH (UPDLOCK, HOLDLOCK)
        WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active' AND Role = N'Owner'
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    UPDATE dbo.HouseholdInvitations
    SET RevokedAtUtc = SYSUTCDATETIME(), RevokedByUserID = @UserID
    WHERE HouseholdInvitationID = @HouseholdInvitationID AND HouseholdID = @HouseholdID
      AND AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [RESTORE] GetInvitationByTokenHash
-- Anonymous-safe lookup by hashed token (needed before the recipient logs in).
-- Leaks nothing beyond what the invitation link's holder already knows.
CREATE OR ALTER PROCEDURE dbo.GetInvitationByTokenHash
    @TokenHash VARBINARY(32)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT i.HouseholdInvitationID, i.HouseholdID, h.Name AS HouseholdName,
           i.Email, i.NormalizedEmail, i.Role, i.ExpiresAtUtc, i.AcceptedAtUtc, i.RevokedAtUtc,
           inviter.name AS InvitedByName
    FROM dbo.HouseholdInvitations i
    INNER JOIN dbo.Households h ON h.HouseholdID = i.HouseholdID
    INNER JOIN dbo.Users inviter ON inviter.userID = i.InvitedByUserID
    WHERE i.TokenHash = @TokenHash;
END;
GO

-- [RESTORE] AcceptHouseholdInvitation
-- ResultCode: Accepted | AlreadyAccepted | NotFound | Expired | Revoked | EmailMismatch
CREATE OR ALTER PROCEDURE dbo.AcceptHouseholdInvitation
    @TokenHash VARBINARY(32),
    @AcceptingUserID INT,
    @AcceptingNormalizedEmail NVARCHAR(320)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    DECLARE @InvitationID INT, @HouseholdID INT, @InviteEmail NVARCHAR(640), @Role NVARCHAR(40),
            @ExpiresAtUtc DATETIME2(6), @AcceptedAtUtc DATETIME2(6), @RevokedAtUtc DATETIME2(6);

    SELECT @InvitationID = HouseholdInvitationID, @HouseholdID = HouseholdID,
           @InviteEmail = NormalizedEmail, @Role = Role, @ExpiresAtUtc = ExpiresAtUtc,
           @AcceptedAtUtc = AcceptedAtUtc, @RevokedAtUtc = RevokedAtUtc
    FROM dbo.HouseholdInvitations WITH (UPDLOCK, HOLDLOCK)
    WHERE TokenHash = @TokenHash;

    IF @InvitationID IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'NotFound' AS ResultCode, CAST(NULL AS INT) AS HouseholdID;
        RETURN;
    END;

    IF @AcceptedAtUtc IS NOT NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.HouseholdMembers WHERE HouseholdID = @HouseholdID AND UserID = @AcceptingUserID AND Status = N'Active')
        BEGIN
            COMMIT TRANSACTION;
            SELECT CAST(1 AS BIT) AS Succeeded, N'AlreadyAccepted' AS ResultCode, @HouseholdID AS HouseholdID;
            RETURN;
        END;
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'AlreadyAccepted' AS ResultCode, CAST(NULL AS INT) AS HouseholdID;
        RETURN;
    END;

    IF @RevokedAtUtc IS NOT NULL
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'Revoked' AS ResultCode, CAST(NULL AS INT) AS HouseholdID;
        RETURN;
    END;

    IF @ExpiresAtUtc <= SYSUTCDATETIME()
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'Expired' AS ResultCode, CAST(NULL AS INT) AS HouseholdID;
        RETURN;
    END;

    IF @InviteEmail <> @AcceptingNormalizedEmail
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'EmailMismatch' AS ResultCode, CAST(NULL AS INT) AS HouseholdID;
        RETURN;
    END;

    IF EXISTS (SELECT 1 FROM dbo.HouseholdMembers WITH (UPDLOCK, HOLDLOCK) WHERE HouseholdID = @HouseholdID AND UserID = @AcceptingUserID)
    BEGIN
        UPDATE dbo.HouseholdMembers
        SET Role = @Role, Status = N'Active', JoinedAtUtc = SYSUTCDATETIME(),
            UpdatedAtUtc = SYSUTCDATETIME(), RemovedAtUtc = NULL, RemovedByUserID = NULL
        WHERE HouseholdID = @HouseholdID AND UserID = @AcceptingUserID;
    END
    ELSE
    BEGIN
        INSERT dbo.HouseholdMembers (HouseholdID, UserID, Role, Status)
        VALUES (@HouseholdID, @AcceptingUserID, @Role, N'Active');
    END;

    UPDATE dbo.HouseholdInvitations
    SET AcceptedAtUtc = SYSUTCDATETIME(), AcceptedByUserID = @AcceptingUserID
    WHERE HouseholdInvitationID = @InvitationID;

    COMMIT TRANSACTION;
    SELECT CAST(1 AS BIT) AS Succeeded, N'Accepted' AS ResultCode, @HouseholdID AS HouseholdID;
END;
GO

-- [RESTORE] GetMedicationsByPetID
-- ============================================================
-- MEDICATION PROCEDURES (household-aware + adherence/attribution)
-- ============================================================

CREATE OR ALTER PROCEDURE dbo.GetMedicationsByPetID
    @UserID INT, @HouseholdID INT, @petID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Pets p INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
        WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID
    )
        RETURN;

    SELECT medID, medicationName, dosage, frequencyType, frequencyInterval,
           TimingDoesNotMatter, startDate, endDate, notes
    FROM dbo.Medications
    WHERE petID = @petID
    ORDER BY startDate DESC;
END;
GO

-- [RESTORE] UnconfirmMedicationSchedule
CREATE OR ALTER PROCEDURE dbo.UnconfirmMedicationSchedule
    @UserID INT, @HouseholdID INT, @medID INT, @logDate DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Medications m
        INNER JOIN dbo.Pets p ON p.petID = m.petID
        INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
           AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
        WHERE m.medID = @medID AND p.HouseholdID = @HouseholdID
    )
        RETURN;

    DECLARE @timingDoesNotMatter bit = 0;
    DECLARE @scheduleID int;

    SELECT @timingDoesNotMatter = CASE
        WHEN UPPER(LTRIM(RTRIM(ISNULL(frequencyType, N'')))) = N'HOURLY' THEN 0
        ELSE TimingDoesNotMatter
    END
    FROM dbo.Medications
    WHERE medID = @medID;

    SELECT TOP (1) @scheduleID = scheduleID
    FROM dbo.MedicationSchedule
    WHERE medID = @medID
      AND
      (
          (@timingDoesNotMatter = 1 AND CONVERT(date, scheduleDate) = CONVERT(date, @logDate))
          OR
          (@timingDoesNotMatter = 0 AND scheduleDate = @logDate)
      )
    ORDER BY CASE WHEN isConfirmed = 1 THEN 0 ELSE 1 END, scheduleID;

    UPDATE dbo.MedicationSchedule
    SET isConfirmed = 0, confirmedAt = NULL, DoseStatus = N'Due',
        AdministeredAtUtc = NULL, RecordedAtUtc = NULL, RecordedByUserID = NULL,
        AdministrationNotes = NULL, StatusReason = NULL
    WHERE scheduleID = @scheduleID;
END;
GO

-- [RESTORE] GetHealthTimeline
-- Chronological medical timeline: health events + medication doses + vet
-- visits (never routine Tasks). The Medication branch returns its own raw
-- DoseStatus/ScheduleDate/TimingDoesNotMatter rather than a pre-computed
-- "Missed" text, so the adherence threshold is only ever evaluated in one
-- place (Services/MedicationAdherence.cs), not duplicated in T-SQL.
CREATE OR ALTER PROCEDURE dbo.GetHealthTimeline
    @UserID INT, @HouseholdID INT, @PetID INT = NULL,
    @StartUtc DATETIME2(6), @EndUtc DATETIME2(6)
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.HouseholdMembers WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active')
        RETURN;

    SELECT SourceType, SourceID, PetID, PetName, EventAtUtc, Title, Summary, Attribution,
           Status, Severity, RawDoseStatus, ScheduleDateRaw, TimingDoesNotMatter, Url
    FROM
    (
        SELECT h.EventKind AS SourceType, h.HealthEventID AS SourceID, h.PetID,
               CONVERT(nvarchar(100), p.name) AS PetName, h.OccurredAtUtc AS EventAtUtc,
               h.EventType AS Title, h.Description AS Summary,
               CONVERT(nvarchar(100), u.name) AS Attribution, h.RecoveryStatus AS Status,
               CONVERT(int, h.Severity) AS Severity,
               CAST(NULL AS nvarchar(40)) AS RawDoseStatus, CAST(NULL AS datetime2(6)) AS ScheduleDateRaw,
               CAST(NULL AS bit) AS TimingDoesNotMatter,
               CONCAT(N'/Health?petID=', h.PetID, N'#health-event-', h.HealthEventID) AS Url
        FROM dbo.HealthEvents h
        INNER JOIN dbo.Pets p ON p.petID = h.PetID
        INNER JOIN dbo.Users u ON u.userID = h.CreatedByUserID
        WHERE p.HouseholdID = @HouseholdID AND h.IsDeleted = 0
          AND (@PetID IS NULL OR h.PetID = @PetID)

        UNION ALL

        SELECT N'Medication', ms.scheduleID, m.petID, CONVERT(nvarchar(100), p.name),
               COALESCE(ms.AdministeredAtUtc, ms.scheduleDate),
               CONVERT(nvarchar(100), m.medicationName),
               CONVERT(nvarchar(2000), CONCAT(ISNULL(m.dosage, N''),
                   CASE WHEN NULLIF(ms.AdministrationNotes, N'') IS NULL THEN N''
                        ELSE CONCAT(N' ', NCHAR(8212), N' ', ms.AdministrationNotes) END)),
               COALESCE(CONVERT(nvarchar(100), u.name), N'Recorded user unavailable'),
               CAST(NULL AS nvarchar(40)), CAST(NULL AS int),
               ms.DoseStatus, ms.scheduleDate, m.TimingDoesNotMatter,
               CONCAT(N'/Medications?petID=', m.petID, N'&editMedID=', m.medID)
        FROM dbo.MedicationSchedule ms
        INNER JOIN dbo.Medications m ON m.medID = ms.medID
        INNER JOIN dbo.Pets p ON p.petID = m.petID
        LEFT JOIN dbo.Users u ON u.userID = ms.RecordedByUserID
        WHERE p.HouseholdID = @HouseholdID AND (@PetID IS NULL OR m.petID = @PetID)

        UNION ALL

        SELECT N'VetVisit', v.VetVisitID, v.PetID, CONVERT(nvarchar(100), p.name),
               DATEADD(SECOND, DATEDIFF(SECOND, CONVERT(time(0), '00:00'), COALESCE(v.VisitTime, CONVERT(time(0), '09:00'))),
                   CONVERT(datetime2(0), v.VisitDate)),
               CONVERT(nvarchar(100), CONCAT(N'Vet visit: ', v.VisitReason)),
               CONVERT(nvarchar(2000), CONCAT(v.ClinicName,
                   CASE WHEN NULLIF(v.VisitSummary, N'') IS NULL THEN N'' ELSE CONCAT(N' ', NCHAR(8212), N' ', v.VisitSummary) END)),
               COALESCE(CONVERT(nvarchar(100), visitUser.name), N'Recorded user unavailable'),
               CONVERT(nvarchar(30), v.Status), CAST(NULL AS int),
               CAST(NULL AS nvarchar(40)), CAST(NULL AS datetime2(6)), CAST(NULL AS bit),
               CONCAT(N'/VetVisits?petID=', v.PetID, N'&vetVisitID=', v.VetVisitID)
        FROM dbo.VetVisits v
        INNER JOIN dbo.Pets p ON p.petID = v.PetID
        LEFT JOIN dbo.Users visitUser ON visitUser.userID = v.CreatedByUserID
        WHERE p.HouseholdID = @HouseholdID AND v.IsDeleted = 0
          AND (@PetID IS NULL OR v.PetID = @PetID)
    ) timeline
    WHERE EventAtUtc >= @StartUtc AND EventAtUtc < @EndUtc
    ORDER BY EventAtUtc, SourceType, SourceID;
END;
GO

-- [RESTORE] AddVetVisit
-- ============================================================
-- VET VISIT PROCEDURES (household-aware + CreatedByUserID attribution)
-- ============================================================

CREATE OR ALTER PROCEDURE dbo.AddVetVisit
    @UserID int, @HouseholdID int,
    @PetID int, @VisitDate date, @VisitTime time(0) = NULL, @IsAllDay bit = 0,
    @ClinicName nvarchar(200) = N'', @VeterinarianName nvarchar(150) = N'',
    @VisitReason nvarchar(500), @VisitType nvarchar(50), @Location nvarchar(400) = N'',
    @PhoneNumber nvarchar(50) = N'', @Status nvarchar(25) = N'Scheduled',
    @Notes nvarchar(max) = N'', @FollowUpDate date = NULL, @Cost decimal(10,2) = NULL,
    @IsEmergency bit = 0, @PreparationInstructions nvarchar(2000) = N'',
    @ReminderAt datetime2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS
        (SELECT 1 FROM dbo.Pets p
         INNER JOIN dbo.HouseholdMembers hm
             ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
            AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
         WHERE p.petID = @PetID AND p.HouseholdID = @HouseholdID)
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
        ELSE CONCAT(LTRIM(RTRIM(@PreparationInstructions)), CHAR(13), CHAR(10), CHAR(13), CHAR(10), LTRIM(RTRIM(@Notes)))
    END;

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
         LTRIM(RTRIM(COALESCE(@Location, N''))),
         LTRIM(RTRIM(COALESCE(@PhoneNumber, N''))), @Status,
         @CombinedNotes, @FollowUpDate, @Cost, @IsEmergency, N'',
         SYSDATETIME(), SYSDATETIME(), 0, @UserID);

    DECLARE @VetVisitID int = CONVERT(int, SCOPE_IDENTITY());

    IF @ReminderAt IS NOT NULL AND @Status IN (N'Scheduled', N'Confirmed', N'Rescheduled')
    BEGIN
        INSERT dbo.VetVisitReminders (VetVisitID, ReminderAt, Status, CreatedAt, UpdatedAt)
        VALUES (@VetVisitID, @ReminderAt, N'Pending', SYSDATETIME(), SYSDATETIME());
    END;

    INSERT dbo.VetVisitHistory (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt)
    VALUES (@VetVisitID, N'Created', NULL, @Status, N'Visit record created.', SYSDATETIME());

    COMMIT TRANSACTION;
    SELECT @VetVisitID AS VetVisitID;
END;
GO

-- [RESTORE] UpdateVetVisit
CREATE OR ALTER PROCEDURE dbo.UpdateVetVisit
    @UserID int, @HouseholdID int,
    @VetVisitID int, @PetID int, @VisitDate date, @VisitTime time(0) = NULL, @IsAllDay bit = 0,
    @ClinicName nvarchar(200) = N'', @VeterinarianName nvarchar(150) = N'',
    @VisitReason nvarchar(500), @VisitType nvarchar(50), @Location nvarchar(400) = N'',
    @PhoneNumber nvarchar(50) = N'', @Status nvarchar(25),
    @Notes nvarchar(max) = N'', @FollowUpDate date = NULL, @Cost decimal(10,2) = NULL,
    @IsEmergency bit = 0, @PreparationInstructions nvarchar(2000) = N'',
    @ReminderAt datetime2(0) = NULL
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
        ELSE CONCAT(LTRIM(RTRIM(@PreparationInstructions)), CHAR(13), CHAR(10), CHAR(13), CHAR(10), LTRIM(RTRIM(@Notes)))
    END;

    UPDATE dbo.VetVisits
    SET PetID = @PetID, VisitDate = @VisitDate,
        VisitTime = CASE WHEN @IsAllDay = 1 THEN NULL ELSE @VisitTime END,
        IsAllDay = @IsAllDay, ClinicName = LTRIM(RTRIM(COALESCE(@ClinicName, N''))),
        VeterinarianName = LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))),
        VisitReason = LTRIM(RTRIM(@VisitReason)), VisitType = LTRIM(RTRIM(@VisitType)),
        Location = LTRIM(RTRIM(COALESCE(@Location, N''))), PhoneNumber = LTRIM(RTRIM(COALESCE(@PhoneNumber, N''))),
        Status = @Status, Notes = @CombinedNotes, FollowUpDate = @FollowUpDate, Cost = @Cost,
        IsEmergency = @IsEmergency, PreparationInstructions = N'', UpdatedAt = SYSDATETIME()
    WHERE VetVisitID = @VetVisitID;

    IF @ReminderAt IS NOT NULL AND @Status IN (N'Scheduled', N'Confirmed', N'Rescheduled')
    BEGIN
        MERGE dbo.VetVisitReminders WITH (HOLDLOCK) AS target
        USING (SELECT @VetVisitID AS VetVisitID) AS source
           ON target.VetVisitID = source.VetVisitID
        WHEN MATCHED THEN
            UPDATE SET ReminderAt = @ReminderAt, Status = N'Pending', DisplayedAt = NULL, DismissedAt = NULL, UpdatedAt = SYSDATETIME()
        WHEN NOT MATCHED THEN
            INSERT (VetVisitID, ReminderAt, Status, CreatedAt, UpdatedAt)
            VALUES (@VetVisitID, @ReminderAt, N'Pending', SYSDATETIME(), SYSDATETIME());
    END
    ELSE
    BEGIN
        UPDATE dbo.VetVisitReminders SET Status = N'Cancelled', UpdatedAt = SYSDATETIME() WHERE VetVisitID = @VetVisitID;
    END;

    DECLARE @StatusChanged bit = CASE WHEN @OldStatus <> @Status THEN 1 ELSE 0 END;
    INSERT dbo.VetVisitHistory (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt)
    VALUES
        (@VetVisitID, CASE WHEN @StatusChanged = 1 THEN N'Status changed' ELSE N'Updated' END,
         CASE WHEN @StatusChanged = 1 THEN @OldStatus ELSE NULL END,
         CASE WHEN @StatusChanged = 1 THEN @Status ELSE NULL END,
         CASE WHEN @StatusChanged = 1 THEN CONCAT(N'Status changed from ', @OldStatus, N' to ', @Status, N'.')
              ELSE N'Visit details updated.' END,
         SYSDATETIME());

    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

-- [RESTORE] ChangeVetVisitStatus
CREATE OR ALTER PROCEDURE dbo.ChangeVetVisitStatus
    @UserID int, @HouseholdID int, @VetVisitID int, @Status nvarchar(25), @Details nvarchar(1000) = N''
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
        UPDATE dbo.VetVisitReminders SET Status = N'Cancelled', UpdatedAt = SYSDATETIME() WHERE VetVisitID = @VetVisitID;

    INSERT dbo.VetVisitHistory (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt)
    VALUES
        (@VetVisitID, N'Status changed', @OldStatus, @Status,
         CASE WHEN LTRIM(RTRIM(COALESCE(@Details, N''))) = N'' THEN CONCAT(N'Status changed to ', @Status, N'.')
              ELSE LTRIM(RTRIM(@Details)) END,
         SYSDATETIME());

    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

-- [RESTORE] CompleteVetVisit
CREATE OR ALTER PROCEDURE dbo.CompleteVetVisit
    @UserID int, @HouseholdID int, @VetVisitID int,
    @VisitSummary nvarchar(4000), @Diagnosis nvarchar(2000) = N'',
    @TreatmentProvided nvarchar(4000) = N'', @VaccinationsReceived nvarchar(2000) = N'',
    @Prescriptions nvarchar(2000) = N'', @FollowUpInstructions nvarchar(4000) = N'',
    @FollowUpDate date = NULL, @FinalCost decimal(10,2) = NULL, @AdditionalNotes nvarchar(4000) = N''
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
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
       AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
    WHERE v.VetVisitID = @VetVisitID AND p.HouseholdID = @HouseholdID AND v.IsDeleted = 0;

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
    SET Status = N'Completed', VisitSummary = LTRIM(RTRIM(@VisitSummary)),
        Diagnosis = LTRIM(RTRIM(COALESCE(@Diagnosis, N''))),
        TreatmentProvided = LTRIM(RTRIM(COALESCE(@TreatmentProvided, N''))),
        VaccinationsReceived = LTRIM(RTRIM(COALESCE(@VaccinationsReceived, N''))),
        Prescriptions = LTRIM(RTRIM(COALESCE(@Prescriptions, N''))),
        FollowUpInstructions = LTRIM(RTRIM(COALESCE(@FollowUpInstructions, N''))),
        FollowUpDate = @FollowUpDate, Cost = COALESCE(@FinalCost, Cost),
        Notes = CASE
                    WHEN LTRIM(RTRIM(COALESCE(@AdditionalNotes, N''))) = N'' THEN Notes
                    WHEN Notes = N'' THEN LTRIM(RTRIM(@AdditionalNotes))
                    ELSE CONCAT(Notes, CHAR(13), CHAR(10), LTRIM(RTRIM(@AdditionalNotes)))
                END,
        UpdatedAt = SYSDATETIME()
    WHERE VetVisitID = @VetVisitID;

    UPDATE dbo.VetVisitReminders SET Status = N'Cancelled', UpdatedAt = SYSDATETIME() WHERE VetVisitID = @VetVisitID;

    INSERT dbo.VetVisitHistory (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt)
    VALUES (@VetVisitID, N'Completed', @OldStatus, N'Completed', N'Visit marked completed and medical outcome recorded.', SYSDATETIME());

    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

-- [RESTORE] AddUser
-- Returns the new user's ID so callers (Signup) can reliably create their
-- default household without an ambiguous re-lookup by username - Users has
-- no unique constraint on userName/email today (pre-existing; out of scope
-- for this migration), so re-querying by username could match the wrong row.
CREATE OR ALTER PROCEDURE dbo.AddUser
    @name       VARCHAR(100),
    @userName   VARCHAR(100),
    @email      VARCHAR(100),
    @phone      VARCHAR(15),
    @pass       VARCHAR(100),
    @createdAt  DATETIME,
    @isAdmin    BIT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO Users (name, userName, email, phone, pass, createdAt, isAdmin)
    VALUES (@name, @userName, @email, @phone, @pass, @createdAt, @isAdmin);
    SELECT CONVERT(INT, SCOPE_IDENTITY()) AS userID;
END;
GO

-- [RESTORE] UpdateUser

CREATE OR ALTER PROCEDURE dbo.UpdateUser
    @userID     INT,
    @name       VARCHAR(50),
    @userName   VARCHAR(50),
    @email      VARCHAR(50),
    @phone      VARCHAR(20),
    @pass       NVARCHAR(100) = NULL
AS
BEGIN
    IF @pass IS NOT NULL
    BEGIN
        UPDATE Users
        SET name = @name, userName = @userName, email = @email,
            phone = @phone, pass = @pass
        WHERE userID = @userID;
    END
    ELSE
    BEGIN
        UPDATE Users
        SET name = @name, userName = @userName, email = @email, phone = @phone
        WHERE userID = @userID;
    END
END
GO

/* ---------- 2. drop the procedures the script created ---------- */
DROP PROCEDURE IF EXISTS dbo.GetTasksByPetIDSince;
DROP PROCEDURE IF EXISTS dbo.GetLatestActivityTasksByPetID;
DROP PROCEDURE IF EXISTS dbo.VerifyPetOwnership;
DROP PROCEDURE IF EXISTS dbo.VerifyTaskOwnership;
DROP PROCEDURE IF EXISTS dbo.VerifyMedicationOwnership;
DROP PROCEDURE IF EXISTS dbo.GetHouseholdIDByPublicID;
DROP PROCEDURE IF EXISTS dbo.GetHouseholdMemberRole;
DROP PROCEDURE IF EXISTS dbo.CheckMedicationAccess;
DROP PROCEDURE IF EXISTS dbo.FindMedicationScheduleForDose;
DROP PROCEDURE IF EXISTS dbo.UpdateMedicationScheduleDose;
DROP PROCEDURE IF EXISTS dbo.GetVetVisits;
DROP PROCEDURE IF EXISTS dbo.GetVetVisitByID;
DROP PROCEDURE IF EXISTS dbo.RefreshVetVisitReminderStatuses;
DROP PROCEDURE IF EXISTS dbo.DismissVetVisitReminder;
DROP PROCEDURE IF EXISTS dbo.GetVetVisitDocuments;
DROP PROCEDURE IF EXISTS dbo.GetVetVisitDocumentByID;
DROP PROCEDURE IF EXISTS dbo.AddVetVisitDocument;
DROP PROCEDURE IF EXISTS dbo.UpdateVetVisitDocument;
DROP PROCEDURE IF EXISTS dbo.DeleteVetVisitDocument;
DROP PROCEDURE IF EXISTS dbo.GetVetVisitHistory;
DROP PROCEDURE IF EXISTS dbo.GetDashboardVetVisits;
DROP PROCEDURE IF EXISTS dbo.GetVetVisitDocumentPathsByPet;
DROP PROCEDURE IF EXISTS dbo.AuthenticateUser;
DROP PROCEDURE IF EXISTS dbo.GetUserProfile;
DROP PROCEDURE IF EXISTS dbo.VerifyUserPassword;
DROP PROCEDURE IF EXISTS dbo.UpdateUserDarkMode;
GO

/* ---------- post-check ---------- */
DECLARE @Left nvarchar(max) = N'';
SELECT @Left = @Left + N' ' + p.name
FROM sys.procedures p
WHERE p.schema_id = SCHEMA_ID(N'dbo') AND p.name IN (
    N'GetTasksByPetIDSince',
    N'GetLatestActivityTasksByPetID',
    N'VerifyPetOwnership',
    N'VerifyTaskOwnership',
    N'VerifyMedicationOwnership',
    N'GetHouseholdIDByPublicID',
    N'GetHouseholdMemberRole',
    N'CheckMedicationAccess',
    N'FindMedicationScheduleForDose',
    N'UpdateMedicationScheduleDose',
    N'GetVetVisits',
    N'GetVetVisitByID',
    N'RefreshVetVisitReminderStatuses',
    N'DismissVetVisitReminder',
    N'GetVetVisitDocuments',
    N'GetVetVisitDocumentByID',
    N'AddVetVisitDocument',
    N'UpdateVetVisitDocument',
    N'DeleteVetVisitDocument',
    N'GetVetVisitHistory',
    N'GetDashboardVetVisits',
    N'GetVetVisitDocumentPathsByPet',
    N'AuthenticateUser',
    N'GetUserProfile',
    N'VerifyUserPassword',
    N'UpdateUserDarkMode');
IF LEN(@Left) > 0
    RAISERROR(N'Rollback incomplete - these procedures still exist:%s', 16, 1, @Left);
ELSE
    SELECT 26 AS ProceduresRestored, 26 AS ProceduresDropped, N'Rollback complete' AS Result;
GO
