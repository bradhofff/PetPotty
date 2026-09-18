/*
    2026-09-17 — Household, invitation, and health stored procedures, plus
    household-aware rewrites of the existing Pet/Task/Medication/VetVisit
    procedures.

    Run this AFTER 2026-09-17_AddHouseholdsAndHealthEvents.sql (it depends on
    the Households/HouseholdMembers/HouseholdInvitations/HealthEvents tables
    and the RecordedByUserID/CreatedByUserID/adherence columns existing).

    Every procedure uses CREATE OR ALTER so this script is safe to rerun.
    Authorization is checked INSIDE every procedure (never trust a client-
    supplied UserID/HouseholdID/PetID beyond using it to look up rows) as a
    second layer under the C# service-level checks — defense in depth.

    Household-wide role tiers used throughout:
      - ManageHousehold : Owner only            (rename, invite, role/remove, revoke/resend)
      - ManagePets      : Owner, Member         (add/edit pet, medication defs, vet visits)
      - RecordCare      : Owner, Member, Caregiver (tasks, dose confirm, health events)
    Deleting a pet outright requires Owner (see DeletePetByPetID).
*/
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

-- Returns the new user's ID so callers (Signup) can reliably create their
-- default household without an ambiguous re-lookup by username — Users has
-- no unique constraint on userName/email today (pre-existing; out of scope
-- for this migration), so re-querying by username could match the wrong row.
CREATE OR ALTER PROCEDURE [dbo].[AddUser]
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

CREATE OR ALTER PROCEDURE dbo.RenameHousehold
    @UserID INT,
    @HouseholdID INT,
    @NewName NVARCHAR(150)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF LEN(LTRIM(RTRIM(ISNULL(@NewName, N'')))) NOT BETWEEN 1 AND 150
    BEGIN
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    BEGIN TRANSACTION;

    UPDATE h
    SET Name = LTRIM(RTRIM(@NewName)), UpdatedAtUtc = SYSUTCDATETIME()
    FROM dbo.Households h WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = h.HouseholdID AND hm.UserID = @UserID
       AND hm.Status = N'Active' AND hm.Role = N'Owner'
    WHERE h.HouseholdID = @HouseholdID;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

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

-- Owner only. Can never set/target the Owner role — promotion/demotion of
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

-- ============================================================
-- INVITATION PROCEDURES
-- ============================================================

-- ResultCode: Created | AlreadyPending | AlreadyMember | InvalidRole | NotAuthorized
-- The unique index on (HouseholdID, NormalizedEmail) only covers *pending*
-- (unaccepted, unrevoked) rows, but does not exclude an expired one — an
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

-- ============================================================
-- HEALTH EVENT PROCEDURES
-- ============================================================

CREATE OR ALTER PROCEDURE dbo.AddHealthEvent
    @UserID INT, @HouseholdID INT, @PetID INT,
    @EventKind NVARCHAR(40), @EventType NVARCHAR(200),
    @OccurredAtUtc DATETIME2(6), @EndedAtUtc DATETIME2(6) = NULL,
    @Severity TINYINT = NULL, @Description NVARCHAR(4000) = N'',
    @PossibleTrigger NVARCHAR(1000) = N'', @AppetiteStatus NVARCHAR(60) = N'',
    @DrinkingStatus NVARCHAR(60) = N'', @RelatedMedicationID INT = NULL,
    @RecoveryStatus NVARCHAR(60) = N'', @RecoveredAtUtc DATETIME2(6) = NULL,
    @VeterinarianContacted BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @EventKind NOT IN (N'Symptom', N'Incident')
    BEGIN
        SELECT CAST(0 AS INT) AS HealthEventID;
        RETURN;
    END;

    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Pets p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.HouseholdMembers hm WITH (UPDLOCK, HOLDLOCK)
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
           AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
        WHERE p.petID = @PetID AND p.HouseholdID = @HouseholdID
    )
       OR (@RelatedMedicationID IS NOT NULL AND NOT EXISTS
           (SELECT 1 FROM dbo.Medications WHERE medID = @RelatedMedicationID AND petID = @PetID))
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS INT) AS HealthEventID;
        RETURN;
    END;

    INSERT dbo.HealthEvents
        (PetID, EventKind, EventType, OccurredAtUtc, EndedAtUtc, Severity, Description,
         PossibleTrigger, AppetiteStatus, DrinkingStatus, RelatedMedicationID,
         RecoveryStatus, RecoveredAtUtc, VeterinarianContacted, CreatedByUserID)
    VALUES
        (@PetID, @EventKind, @EventType, @OccurredAtUtc, @EndedAtUtc, @Severity, @Description,
         @PossibleTrigger, @AppetiteStatus, @DrinkingStatus, @RelatedMedicationID,
         @RecoveryStatus, @RecoveredAtUtc, @VeterinarianContacted, @UserID);

    DECLARE @NewID INT = CONVERT(INT, SCOPE_IDENTITY());
    COMMIT TRANSACTION;
    SELECT @NewID AS HealthEventID;
END;
GO

CREATE OR ALTER PROCEDURE dbo.UpdateHealthEvent
    @UserID INT, @HouseholdID INT, @HealthEventID INT, @PetID INT,
    @EventKind NVARCHAR(40), @EventType NVARCHAR(200),
    @OccurredAtUtc DATETIME2(6), @EndedAtUtc DATETIME2(6) = NULL,
    @Severity TINYINT = NULL, @Description NVARCHAR(4000) = N'',
    @PossibleTrigger NVARCHAR(1000) = N'', @AppetiteStatus NVARCHAR(60) = N'',
    @DrinkingStatus NVARCHAR(60) = N'', @RelatedMedicationID INT = NULL,
    @RecoveryStatus NVARCHAR(60) = N'', @RecoveredAtUtc DATETIME2(6) = NULL,
    @VeterinarianContacted BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @EventKind NOT IN (N'Symptom', N'Incident')
    BEGIN
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    BEGIN TRANSACTION;

    -- The pet may be changing as part of the edit, so both the row's CURRENT
    -- pet and the NEW target pet are checked against the active household.
    UPDATE h
    SET PetID = @PetID, EventKind = @EventKind, EventType = @EventType,
        OccurredAtUtc = @OccurredAtUtc, EndedAtUtc = @EndedAtUtc, Severity = @Severity,
        Description = @Description, PossibleTrigger = @PossibleTrigger,
        AppetiteStatus = @AppetiteStatus, DrinkingStatus = @DrinkingStatus,
        RelatedMedicationID = @RelatedMedicationID, RecoveryStatus = @RecoveryStatus,
        RecoveredAtUtc = @RecoveredAtUtc, VeterinarianContacted = @VeterinarianContacted,
        UpdatedAtUtc = SYSUTCDATETIME()
    FROM dbo.HealthEvents h WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets currentPet ON currentPet.petID = h.PetID AND currentPet.HouseholdID = @HouseholdID
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = @HouseholdID AND hm.UserID = @UserID
       AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
    WHERE h.HealthEventID = @HealthEventID AND h.IsDeleted = 0
      AND EXISTS (SELECT 1 FROM dbo.Pets np WHERE np.petID = @PetID AND np.HouseholdID = @HouseholdID)
      AND (@RelatedMedicationID IS NULL OR EXISTS
          (SELECT 1 FROM dbo.Medications WHERE medID = @RelatedMedicationID AND petID = @PetID));

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

CREATE OR ALTER PROCEDURE dbo.DeleteHealthEvent
    @UserID INT, @HouseholdID INT, @HealthEventID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    UPDATE h
    SET IsDeleted = 1, DeletedAtUtc = SYSUTCDATETIME(), DeletedByUserID = @UserID, UpdatedAtUtc = SYSUTCDATETIME()
    FROM dbo.HealthEvents h WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = h.PetID
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
       AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
    WHERE h.HealthEventID = @HealthEventID AND p.HouseholdID = @HouseholdID AND h.IsDeleted = 0;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

CREATE OR ALTER PROCEDURE dbo.GetHealthEventByID
    @UserID INT, @HouseholdID INT, @HealthEventID INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT h.HealthEventID, h.PetID, p.name AS PetName, h.EventKind, h.EventType,
           h.OccurredAtUtc, h.EndedAtUtc, h.Severity, h.Description, h.PossibleTrigger,
           h.AppetiteStatus, h.DrinkingStatus, h.RelatedMedicationID,
           ISNULL(m.medicationName, N'') AS RelatedMedicationName,
           h.RecoveryStatus, h.RecoveredAtUtc, h.VeterinarianContacted,
           h.CreatedByUserID, u.name AS CreatedByName, h.CreatedAtUtc
    FROM dbo.HealthEvents h
    INNER JOIN dbo.Pets p ON p.petID = h.PetID
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    INNER JOIN dbo.Users u ON u.userID = h.CreatedByUserID
    LEFT JOIN dbo.Medications m ON m.medID = h.RelatedMedicationID
    WHERE h.HealthEventID = @HealthEventID AND p.HouseholdID = @HouseholdID AND h.IsDeleted = 0;
END;
GO

CREATE OR ALTER PROCEDURE dbo.GetHealthEvents
    @UserID INT, @HouseholdID INT, @PetID INT = NULL,
    @StartUtc DATETIME2(6), @EndUtc DATETIME2(6), @EventKind NVARCHAR(40) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.HouseholdMembers WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active')
        RETURN;

    SELECT h.HealthEventID, h.PetID, p.name AS PetName, h.EventKind, h.EventType,
           h.OccurredAtUtc, h.EndedAtUtc, h.Severity, h.Description, h.PossibleTrigger,
           h.AppetiteStatus, h.DrinkingStatus, h.RelatedMedicationID,
           ISNULL(m.medicationName, N'') AS RelatedMedicationName,
           h.RecoveryStatus, h.RecoveredAtUtc, h.VeterinarianContacted,
           h.CreatedByUserID, u.name AS CreatedByName, h.CreatedAtUtc
    FROM dbo.HealthEvents h
    INNER JOIN dbo.Pets p ON p.petID = h.PetID
    INNER JOIN dbo.Users u ON u.userID = h.CreatedByUserID
    LEFT JOIN dbo.Medications m ON m.medID = h.RelatedMedicationID
    WHERE p.HouseholdID = @HouseholdID AND h.IsDeleted = 0
      AND (@PetID IS NULL OR h.PetID = @PetID)
      AND (@EventKind IS NULL OR @EventKind = N'All' OR h.EventKind = @EventKind)
      AND h.OccurredAtUtc >= @StartUtc AND h.OccurredAtUtc < @EndUtc
    ORDER BY h.OccurredAtUtc DESC, h.HealthEventID DESC;
END;
GO

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
                        ELSE CONCAT(N' — ', ms.AdministrationNotes) END)),
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
                   CASE WHEN NULLIF(v.VisitSummary, N'') IS NULL THEN N'' ELSE CONCAT(N' — ', v.VisitSummary) END)),
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

-- NOTE: @age is INT here (not NVARCHAR) — matches this procedure's existing,
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

-- Deleting a pet cascades its medications/tasks/vet visits, so it requires
-- Owner (stricter than the Owner+Member tier used to add/edit a pet).
CREATE OR ALTER PROCEDURE dbo.DeletePetByPetID
    @UserID INT, @HouseholdID INT, @petID INT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1 FROM dbo.Pets p WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN dbo.HouseholdMembers hm
                ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
               AND hm.Status = N'Active' AND hm.Role = N'Owner'
            WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID
        )
        BEGIN
            ROLLBACK;
            SELECT CAST(0 AS BIT) AS Succeeded;
            RETURN;
        END;

        DELETE FROM MedicationSchedule WHERE medID IN (SELECT medID FROM Medications WHERE petID = @petID);
        DELETE FROM Medications WHERE petID = @petID;
        DELETE FROM Tasks WHERE petID = @petID;
        DELETE FROM VetVisits WHERE petID = @petID;
        DELETE FROM Pets WHERE petID = @petID;
        COMMIT;
        SELECT CAST(1 AS BIT) AS Succeeded;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH
END;
GO

-- ============================================================
-- TASK PROCEDURES (household-aware + RecordedByUserID attribution)
-- ============================================================

CREATE OR ALTER PROCEDURE dbo.AddTaskByPetID
    @UserID INT, @HouseholdID INT, @petID INT,
    @taskType VARCHAR(50), @notes VARCHAR(255), @createdAt DATETIME, @RecordedByUserID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Pets p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
           AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
        WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    INSERT INTO Tasks (petID, taskType, notes, createdAt, RecordedByUserID)
    VALUES (@petID, @taskType, @notes, @createdAt, @RecordedByUserID);

    COMMIT TRANSACTION;
    SELECT CAST(1 AS BIT) AS Succeeded;
END;
GO

CREATE OR ALTER PROCEDURE dbo.UpdateTaskByID
    @UserID INT, @HouseholdID INT, @taskID INT,
    @taskType NVARCHAR(50), @notes NVARCHAR(255), @createdAt DATETIME
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    UPDATE t
    SET taskType = @taskType, notes = @notes, createdAt = @createdAt
    FROM Tasks t WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = t.petID
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
       AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
    WHERE t.taskID = @taskID AND p.HouseholdID = @HouseholdID;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

CREATE OR ALTER PROCEDURE dbo.DeleteTaskByTaskID
    @UserID INT, @HouseholdID INT, @taskID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    DELETE t
    FROM Tasks t WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = t.petID
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
       AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
    WHERE t.taskID = @taskID AND p.HouseholdID = @HouseholdID;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

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

CREATE OR ALTER PROCEDURE dbo.AddMedication
    @UserID INT, @HouseholdID INT,
    @petID INT, @medicationName NVARCHAR(100), @dosage NVARCHAR(100),
    @frequencyType NVARCHAR(50), @frequencyInterval INT = NULL,
    @startDate DATETIME2(0), @endDate DATETIME2(0) = NULL,
    @notes NVARCHAR(255), @TimingDoesNotMatter BIT = 0
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
        WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID
    )
    BEGIN
        SELECT CAST(0 AS INT) AS medID;
        RETURN;
    END;

    IF UPPER(LTRIM(RTRIM(@frequencyType))) = N'HOURLY'
        SET @TimingDoesNotMatter = 0;

    IF @TimingDoesNotMatter = 1
    BEGIN
        SET @startDate = CONVERT(datetime2(0), CONVERT(date, @startDate));
        IF @endDate IS NOT NULL
            SET @endDate = CONVERT(datetime2(0), CONVERT(date, @endDate));
    END;

    DECLARE @medIDTable table (medID int);

    INSERT dbo.Medications
        (petID, medicationName, dosage, frequencyType, frequencyInterval, TimingDoesNotMatter, startDate, endDate, notes)
    OUTPUT INSERTED.medID INTO @medIDTable
    VALUES
        (@petID, @medicationName, @dosage, @frequencyType, ISNULL(@frequencyInterval, 1),
         @TimingDoesNotMatter, @startDate, @endDate, @notes);

    DECLARE @medID int = (SELECT TOP (1) medID FROM @medIDTable);
    EXEC dbo.EnsureMedicationScheduleGenerated @medID = @medID;
    SELECT @medID AS medID;
END;
GO

CREATE OR ALTER PROCEDURE dbo.UpdateMedication
    @UserID INT, @HouseholdID INT,
    @medID INT, @medicationName NVARCHAR(100), @dosage NVARCHAR(100),
    @frequencyType NVARCHAR(50), @frequencyInterval INT = NULL,
    @startDate DATETIME2(0), @endDate DATETIME2(0) = NULL,
    @notes NVARCHAR(255), @TimingDoesNotMatter BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Medications m
        INNER JOIN dbo.Pets p ON p.petID = m.petID
        INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
           AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
        WHERE m.medID = @medID AND p.HouseholdID = @HouseholdID
    )
    BEGIN
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    IF UPPER(LTRIM(RTRIM(@frequencyType))) = N'HOURLY'
        SET @TimingDoesNotMatter = 0;

    IF @TimingDoesNotMatter = 1
    BEGIN
        SET @startDate = CONVERT(datetime2(0), CONVERT(date, @startDate));
        IF @endDate IS NOT NULL
            SET @endDate = CONVERT(datetime2(0), CONVERT(date, @endDate));
    END;

    DECLARE @oldStart datetime2(0), @oldEnd datetime2(0), @oldFrequency nvarchar(50),
            @oldInterval int, @oldTimingDoesNotMatter bit;

    SELECT @oldStart = startDate, @oldEnd = endDate, @oldFrequency = frequencyType,
           @oldInterval = frequencyInterval, @oldTimingDoesNotMatter = TimingDoesNotMatter
    FROM dbo.Medications
    WHERE medID = @medID;

    UPDATE dbo.Medications
    SET medicationName = @medicationName, dosage = @dosage, frequencyType = @frequencyType,
        frequencyInterval = ISNULL(@frequencyInterval, 1), TimingDoesNotMatter = @TimingDoesNotMatter,
        startDate = @startDate, endDate = @endDate, notes = @notes
    WHERE medID = @medID;

    IF ISNULL(@oldStart, CONVERT(datetime2(0), '19000101')) <> @startDate
       OR ISNULL(@oldEnd, CONVERT(datetime2(0), '99991231')) <> ISNULL(@endDate, CONVERT(datetime2(0), '99991231'))
       OR ISNULL(@oldFrequency, N'') <> ISNULL(@frequencyType, N'')
       OR ISNULL(@oldInterval, 1) <> ISNULL(@frequencyInterval, 1)
       OR ISNULL(@oldTimingDoesNotMatter, 0) <> @TimingDoesNotMatter
    BEGIN
        DELETE dbo.MedicationSchedule WHERE medID = @medID AND isConfirmed = 0;
        EXEC dbo.EnsureMedicationScheduleGenerated @medID = @medID;
    END;

    SELECT CAST(1 AS BIT) AS Succeeded;
END;
GO

CREATE OR ALTER PROCEDURE dbo.DeleteMedicationByID
    @UserID INT, @HouseholdID INT, @medID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Medications m WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.Pets p ON p.petID = m.petID
        INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
           AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
        WHERE m.medID = @medID AND p.HouseholdID = @HouseholdID
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    DELETE FROM MedicationSchedule WHERE medID = @medID;
    DELETE FROM Medications WHERE medID = @medID;

    COMMIT TRANSACTION;
    SELECT CAST(1 AS BIT) AS Succeeded;
END;
GO

CREATE OR ALTER PROCEDURE dbo.GetScheduledMedsByPetID_AllTime
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

    SELECT ms.scheduleID, m.medID, m.medicationName, m.dosage, m.frequencyType, m.TimingDoesNotMatter,
           ms.scheduleDate, ms.isConfirmed, ms.confirmedAt, ms.DoseStatus, ms.AdministeredAtUtc,
           ms.RecordedAtUtc, ms.RecordedByUserID, u.name AS RecordedByName, ms.StatusReason, ms.AdministrationNotes
    FROM dbo.MedicationSchedule ms
    INNER JOIN dbo.Medications m ON m.medID = ms.medID
    LEFT JOIN dbo.Users u ON u.userID = ms.RecordedByUserID
    WHERE m.petID = @petID
    ORDER BY ms.scheduleDate;
END;
GO

CREATE OR ALTER PROCEDURE dbo.GetScheduledMedsByPetID_Next2Months
    @UserID INT, @HouseholdID INT, @petID INT, @Today date = NULL
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

    SET @Today = COALESCE(@Today, CAST(SYSUTCDATETIME() AS date));

    SELECT ms.scheduleID, m.medID, m.medicationName, m.dosage, m.frequencyType, m.TimingDoesNotMatter,
           ms.scheduleDate, ms.isConfirmed, ms.confirmedAt, ms.DoseStatus, ms.AdministeredAtUtc,
           ms.RecordedAtUtc, ms.RecordedByUserID, u.name AS RecordedByName, ms.StatusReason, ms.AdministrationNotes
    FROM dbo.MedicationSchedule ms
    INNER JOIN dbo.Medications m ON m.medID = ms.medID
    LEFT JOIN dbo.Users u ON u.userID = ms.RecordedByUserID
    WHERE m.petID = @petID
      AND ms.scheduleDate < DATEADD(MONTH, 2, @Today)
      AND (ms.DoseStatus = N'Due' OR ms.scheduleDate >= @Today)
    ORDER BY ms.scheduleDate;
END;
GO

CREATE OR ALTER PROCEDURE dbo.GetScheduledMedsByPetID
    @UserID INT, @HouseholdID INT, @petID INT
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.GetScheduledMedsByPetID_AllTime @UserID = @UserID, @HouseholdID = @HouseholdID, @petID = @petID;
END;
GO

-- Preserves the exact-time shift behavior and TimingDoesNotMatter date-only
-- behavior from the pre-household procedure unchanged; only the household
-- authorization check and the new adherence-column writes are added.
CREATE OR ALTER PROCEDURE dbo.ConfirmMedicationSchedule
    @UserID INT, @HouseholdID INT,
    @medID INT, @logDate DATETIME2(0), @confirmedAt DATETIME2(0),
    @RecordedByUserID INT, @AdministeredAtUtc DATETIME2(6), @DoseStatus NVARCHAR(40),
    @AdministrationNotes NVARCHAR(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @DoseStatus NOT IN (N'Taken', N'Taken late')
        SET @DoseStatus = N'Taken';

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
    SELECT @timingDoesNotMatter = CASE
        WHEN UPPER(LTRIM(RTRIM(ISNULL(frequencyType, N'')))) = N'HOURLY' THEN 0
        ELSE TimingDoesNotMatter
    END
    FROM dbo.Medications
    WHERE medID = @medID;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @timingDoesNotMatter = 1
        BEGIN
            DECLARE @dateOnlyScheduleID int;

            SELECT TOP (1) @dateOnlyScheduleID = scheduleID
            FROM dbo.MedicationSchedule WITH (UPDLOCK, HOLDLOCK)
            WHERE medID = @medID
              AND CONVERT(date, scheduleDate) = CONVERT(date, @logDate)
            ORDER BY CASE WHEN isConfirmed = 0 THEN 0 ELSE 1 END, scheduleID;

            IF @dateOnlyScheduleID IS NULL
            BEGIN
                INSERT dbo.MedicationSchedule
                    (medID, scheduleDate, isConfirmed, confirmedAt, DoseStatus, AdministeredAtUtc, RecordedAtUtc, RecordedByUserID, AdministrationNotes)
                VALUES
                    (@medID, CONVERT(datetime2(0), CONVERT(date, @logDate)), 1, @confirmedAt,
                     N'Taken', @AdministeredAtUtc, SYSUTCDATETIME(), @RecordedByUserID, @AdministrationNotes);
            END
            ELSE
            BEGIN
                UPDATE dbo.MedicationSchedule
                SET isConfirmed = 1, confirmedAt = @confirmedAt, DoseStatus = N'Taken',
                    AdministeredAtUtc = @AdministeredAtUtc, RecordedAtUtc = SYSUTCDATETIME(),
                    RecordedByUserID = @RecordedByUserID, AdministrationNotes = @AdministrationNotes
                WHERE scheduleID = @dateOnlyScheduleID;
            END;

            COMMIT TRANSACTION;
            RETURN;
        END;

        DECLARE @shiftSeconds int = DATEDIFF(SECOND, @logDate, @confirmedAt);
        DECLARE @nextConfirmedDate datetime2(0);

        SELECT @nextConfirmedDate = MIN(scheduleDate)
        FROM dbo.MedicationSchedule WITH (UPDLOCK, HOLDLOCK)
        WHERE medID = @medID AND scheduleDate > @logDate AND isConfirmed = 1;

        IF EXISTS (SELECT 1 FROM dbo.MedicationSchedule WITH (UPDLOCK, HOLDLOCK) WHERE medID = @medID AND scheduleDate = @logDate)
        BEGIN
            UPDATE dbo.MedicationSchedule
            SET isConfirmed = 1, confirmedAt = @confirmedAt, DoseStatus = @DoseStatus,
                AdministeredAtUtc = @AdministeredAtUtc, RecordedAtUtc = SYSUTCDATETIME(),
                RecordedByUserID = @RecordedByUserID, AdministrationNotes = @AdministrationNotes
            WHERE medID = @medID AND scheduleDate = @logDate;
        END
        ELSE
        BEGIN
            INSERT dbo.MedicationSchedule
                (medID, scheduleDate, isConfirmed, confirmedAt, DoseStatus, AdministeredAtUtc, RecordedAtUtc, RecordedByUserID, AdministrationNotes)
            VALUES
                (@medID, @logDate, 1, @confirmedAt, @DoseStatus, @AdministeredAtUtc, SYSUTCDATETIME(), @RecordedByUserID, @AdministrationNotes);
        END;

        IF @shiftSeconds <> 0
        BEGIN
            DECLARE @RowsToShift table
            (
                scheduleID int PRIMARY KEY,
                oldScheduleDate datetime2(0) NOT NULL,
                newScheduleDate datetime2(0) NOT NULL
            );

            INSERT @RowsToShift (scheduleID, oldScheduleDate, newScheduleDate)
            SELECT ms.scheduleID, ms.scheduleDate, DATEADD(SECOND, @shiftSeconds, ms.scheduleDate)
            FROM dbo.MedicationSchedule AS ms WITH (UPDLOCK, HOLDLOCK)
            WHERE ms.medID = @medID
              AND ms.scheduleDate > @logDate
              AND ms.isConfirmed = 0
              AND (@nextConfirmedDate IS NULL OR ms.scheduleDate < @nextConfirmedDate);

            IF EXISTS
            (
                SELECT 1
                FROM @RowsToShift AS r
                INNER JOIN dbo.MedicationSchedule AS ms WITH (UPDLOCK, HOLDLOCK)
                    ON ms.medID = @medID AND ms.scheduleDate = r.newScheduleDate AND ms.scheduleID <> r.scheduleID
                WHERE NOT EXISTS (SELECT 1 FROM @RowsToShift AS r2 WHERE r2.scheduleID = ms.scheduleID)
            )
            BEGIN
                THROW 51000, 'Cannot shift medication schedule because it would create duplicate schedule times.', 1;
            END;

            UPDATE ms
            SET scheduleDate = r.newScheduleDate
            FROM dbo.MedicationSchedule AS ms
            INNER JOIN @RowsToShift AS r ON r.scheduleID = ms.scheduleID;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO

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

-- Already converted to household-scoping in a prior, uncommitted session;
-- recreated here identically (Owner/Member only — deleting a full visit
-- record is more consequential than editing one) so it's captured in source
-- control and the migration is idempotent against that existing state.
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
