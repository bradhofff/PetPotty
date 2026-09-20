/* =============================================================================
   2026-09-20_AlignStoredProcedures.sql

   PURPOSE
     Every database call in the application should go through a stored procedure.
     An audit of the C# code against the live SQL Server found:
       * 63 call sites that run inline SQL (e.g. PetService.AddPet, switched from
         dbo.AddPet to inline SQL in commit 60c60c6 when household support needed to
         write HouseholdID), and
       * live procedures whose definition no longer matches what the app needs
         (wrong role rule, missing columns, missing attribution, etc.).
     This script CREATEs the missing procedures and ALTERs the ones that differ,
     using CREATE OR ALTER so it is safe to run repeatedly. It changes NO tables
     and NO data.

   HOW TO RUN
     SSMS : open the script, pick the target database, Execute.
     sqlcmd: sqlcmd -S <server> -d <database> -E -b -i 2026-09-20_AlignStoredProcedures.sql
             (-I is NOT required: the script turns QUOTED_IDENTIFIER ON itself. The
              legacy procs were built with it OFF, which breaks DML on tables that
              have filtered indexes - VetVisits, HealthEvents, HouseholdInvitations.)
     Run 2026-09-20_SnapshotCurrentProcedures.sql as well when rebuilding an
     environment from source control (it is a no-op for the current test DB).

   PREREQUISITES
     The household / health / vet-visit schema migrations must already be applied.
     The script checks for them first and stops without changing anything if not.

   PERMISSIONS
     CREATE OR ALTER keeps the grants of procedures that already exist. If the
     application connects with a limited login that has per-procedure EXECUTE
     grants (rather than GRANT EXECUTE ON SCHEMA::dbo), grant EXECUTE on the NEW
     procedures below. The test database connects as sa, so nothing is needed there.

   ROLLBACK
     2026-09-20_AlignStoredProcedures_Rollback.sql restores the previous definition of
     every UPDATED procedure and drops every NEW one. Those previous definitions were
     not in source control before this script, so keep that file.

   ENCODING
     This file is deliberately pure ASCII. sqlcmd reads a UTF-8 file without a BOM using
     the console code page, which silently turns a literal em dash into "a-hat euro ..."
     garbage. The em dash the timeline text needs is therefore written as NCHAR(8212).

   CONVENTIONS (match the existing household-aware procs)
     * Mutations re-check authorization in SQL and return   Succeeded BIT
       (or a new ID, 0 = denied) - the C# roles are: Owner = everything;
       Member = care plans + care logs; Caregiver = care logs only.
     * Reads are scoped by HouseholdMembers exactly like the inline SQL was, and their
       parameter names and result-set column names/order are identical to the inline
       SQL they replace, so switching a read call site is mechanical. Mutations take
       the caller's @UserID / @HouseholdID in addition to the inline parameters.

   STATUS OF EACH PROCEDURE                              (C# it serves)
   ---- PETS / TASKS ------------------------------------------------------------
   UPDATED GetPetsByHouseholdID           PetService.GetPetsByUser         tie-break order
   UPDATED GetTasksByPetID                PetService.GetTasksByPetID       tie-break order
   UPDATED GetTasksByPetID_Recent         PetService.GetTasksByPetID       was 36 hours, app uses 7 days
   NEW     GetTasksByPetIDSince           PetService.GetTasksByPetIDSince
   NEW     GetLatestActivityTasksByPetID  PetService.GetLatestActivityTasksByPetID
   UPDATED AddPet                         PetService.AddPet                Owner-only; typed params; optional createdAt
   UPDATED UpdatePet                      PetService.EditPet               Owner-only (was Owner+Member)
   NEW     VerifyPetOwnership             OwnedRecordCommand.Pet
   NEW     VerifyTaskOwnership            OwnedRecordCommand.Task
   NEW     VerifyMedicationOwnership      OwnedRecordCommand.Medication / MedicationService.OwnsMedication(tx)
   ---- HOUSEHOLDS --------------------------------------------------------------
   UPDATED GetHouseholdContext            HouseholdContextService.GetActiveHousehold / TrySetActiveHousehold
   NEW     GetHouseholdIDByPublicID       HouseholdContextService.TrySetActiveHousehold(Guid)
   NEW     GetHouseholdMemberRole         HouseholdAuthorizationService.GetRole
   UPDATED CreateDefaultHouseholdForUser  HouseholdService.EnsurePersonalHousehold
   UPDATED GetHouseholdsForUser           HouseholdService.GetHouseholds
   UPDATED GetHouseholdMembers            HouseholdService.GetMembers
   UPDATED GetPendingHouseholdInvitations HouseholdService.GetPendingInvitations
   UPDATED UpdateHouseholdMemberRole      HouseholdService.ChangeMemberRole  (targets a member by PublicID)
   UPDATED RemoveHouseholdMember          HouseholdService.RemoveMember      (targets a member by PublicID)
   ---- INVITATIONS -------------------------------------------------------------
   UPDATED CreateHouseholdInvitation      HouseholdInvitationService.CreateAsync
   UPDATED ResendHouseholdInvitation      HouseholdInvitationService.ResendAsync
   UPDATED RevokeHouseholdInvitation      HouseholdInvitationService.Revoke
   UPDATED GetInvitationByTokenHash       HouseholdInvitationService.Validate
   UPDATED AcceptHouseholdInvitation      HouseholdInvitationService.Accept
   ---- MEDICATIONS -------------------------------------------------------------
   UPDATED GetMedicationsByPetID          MedicationService.GetMedicationsByPetID  order by name
   NEW     CheckMedicationAccess          MedicationService.OwnsMedication
   NEW     FindMedicationScheduleForDose  MedicationService.FindSchedule
   NEW     UpdateMedicationScheduleDose   MedicationService.RecordDose (final UPDATE)
   UPDATED UnconfirmMedicationSchedule    MedicationService.UnconfirmSchedule  now reports success
   ---- HEALTH ------------------------------------------------------------------
   UPDATED GetHealthTimeline              HealthService.GetTimeline  UTC offset + Missed status
   ---- VET VISITS --------------------------------------------------------------
   UPDATED AddVetVisit                    VetVisitService.AddVisit           records ChangedByUserID
   UPDATED UpdateVetVisit                 VetVisitService.UpdateVisit        records ChangedByUserID
   UPDATED ChangeVetVisitStatus           VetVisitService.ChangeStatus       records ChangedByUserID
   UPDATED CompleteVetVisit               VetVisitService.CompleteVisit      records ChangedByUserID
   NEW     GetVetVisits                   VetVisitService.GetVisits
   NEW     GetVetVisitByID                VetVisitService.GetVisit
   NEW     RefreshVetVisitReminderStatuses VetVisitService.RefreshReminderStatuses
   NEW     DismissVetVisitReminder        VetVisitService.DismissReminder
   NEW     GetVetVisitDocuments           VetVisitService.GetDocuments
   NEW     GetVetVisitDocumentByID        VetVisitService.GetDocument
   NEW     AddVetVisitDocument            VetVisitService.AddDocument
   NEW     UpdateVetVisitDocument         VetVisitService.UpdateDocument
   NEW     DeleteVetVisitDocument         VetVisitService.DeleteDocument (+ history row)
   NEW     GetVetVisitHistory             VetVisitService.GetHistory
   NEW     GetDashboardVetVisits          VetVisitService.GetDashboardVisits
   NEW     GetVetVisitDocumentPathsByPet  VetVisitService.GetDocumentPathsByPet
   ---- USERS / AUTH ------------------------------------------------------------
   NEW     AuthenticateUser               Login.OnPost
   NEW     GetUserProfile                 Profile.LoadProfile
   NEW     VerifyUserPassword             Profile.VerifyCurrentPassword
   NEW     UpdateUserDarkMode             Theme.OnPost
   UPDATED AddUser                        Signup.OnPost   ExecuteNonQuery() returned -1 (NOCOUNT ON)
   UPDATED UpdateUser                     Profile.OnPost  params were narrower than the columns
   ============================================================================= */

SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------- prerequisite guard: stop without changing anything if the schema is behind ---------- */
DECLARE @Missing nvarchar(max) = N'';
IF OBJECT_ID(N'dbo.Households',           N'U') IS NULL SET @Missing += N' dbo.Households';
IF OBJECT_ID(N'dbo.HouseholdMembers',     N'U') IS NULL SET @Missing += N' dbo.HouseholdMembers';
IF OBJECT_ID(N'dbo.HouseholdInvitations', N'U') IS NULL SET @Missing += N' dbo.HouseholdInvitations';
IF OBJECT_ID(N'dbo.HealthEvents',         N'U') IS NULL SET @Missing += N' dbo.HealthEvents';
IF OBJECT_ID(N'dbo.VetVisitReminders',    N'U') IS NULL SET @Missing += N' dbo.VetVisitReminders';
IF OBJECT_ID(N'dbo.VetVisitDocuments',    N'U') IS NULL SET @Missing += N' dbo.VetVisitDocuments';
IF OBJECT_ID(N'dbo.VetVisitHistory',      N'U') IS NULL SET @Missing += N' dbo.VetVisitHistory';
IF COL_LENGTH(N'dbo.Pets',               N'HouseholdID')      IS NULL SET @Missing += N' Pets.HouseholdID';
IF COL_LENGTH(N'dbo.Pets',               N'ProfileImagePath') IS NULL SET @Missing += N' Pets.ProfileImagePath';
IF COL_LENGTH(N'dbo.Users',              N'PublicID')         IS NULL SET @Missing += N' Users.PublicID';
IF COL_LENGTH(N'dbo.Users',              N'DarkMode')         IS NULL SET @Missing += N' Users.DarkMode';
IF COL_LENGTH(N'dbo.Tasks',              N'RecordedByUserID') IS NULL SET @Missing += N' Tasks.RecordedByUserID';
IF COL_LENGTH(N'dbo.VetVisits',          N'CreatedByUserID')  IS NULL SET @Missing += N' VetVisits.CreatedByUserID';
IF COL_LENGTH(N'dbo.VetVisitHistory',    N'ChangedByUserID')  IS NULL SET @Missing += N' VetVisitHistory.ChangedByUserID';
IF COL_LENGTH(N'dbo.MedicationSchedule', N'DoseStatus')       IS NULL SET @Missing += N' MedicationSchedule.DoseStatus';
IF COL_LENGTH(N'dbo.HouseholdInvitations', N'LastSentAtUtc')  IS NULL SET @Missing += N' HouseholdInvitations.LastSentAtUtc';
IF LEN(@Missing) > 0
BEGIN
    EXEC sys.sp_set_session_context @key = N'AlignStoredProcedures_Aborted', @value = 1;
    RAISERROR(N'Prerequisite schema objects are missing:%s. Apply the earlier Migrationsss scripts first. NOTHING WAS CHANGED.', 16, 1, @Missing);
    SET NOEXEC ON;   -- skip every remaining batch
END;
GO


/* =============================================================================
   PETS / TASKS
   ============================================================================= */

-- [UPDATED] GetPetsByHouseholdID  -  PetService.GetPetsByUser
-- Was ORDER BY createdAt only, so pets created in the same instant came back in arbitrary order.
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
    ORDER BY p.createdAt, p.petID;
END;
GO

-- [UPDATED] GetTasksByPetID (all time)  -  PetService.GetTasksByPetID(allTime: true)
-- Adds the taskID tie-break the app uses so same-second tasks keep a stable order.
CREATE OR ALTER PROCEDURE dbo.GetTasksByPetID
    @UserID INT, @HouseholdID INT, @petID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT t.taskID, t.petID, p.name AS petName, t.taskType, t.notes,
           t.createdAt, t.RecordedByUserID, u.name AS RecordedByName
    FROM dbo.Tasks t
    INNER JOIN dbo.Pets p ON p.petID = t.petID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    LEFT JOIN dbo.Users u ON u.userID = t.RecordedByUserID
    WHERE t.petID = @petID AND p.HouseholdID = @HouseholdID
    ORDER BY t.createdAt DESC, t.taskID DESC;
END;
GO

-- [UPDATED] GetTasksByPetID_Recent  -  PetService.GetTasksByPetID(allTime: false)
-- The deployed procedure returned the last 36 HOURS; the app's "recent" view is the last 7 DAYS.
CREATE OR ALTER PROCEDURE dbo.GetTasksByPetID_Recent
    @UserID INT, @HouseholdID INT, @petID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT t.taskID, t.petID, p.name AS petName, t.taskType, t.notes,
           t.createdAt, t.RecordedByUserID, u.name AS RecordedByName
    FROM dbo.Tasks t
    INNER JOIN dbo.Pets p ON p.petID = t.petID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    LEFT JOIN dbo.Users u ON u.userID = t.RecordedByUserID
    WHERE t.petID = @petID AND p.HouseholdID = @HouseholdID
      AND t.createdAt >= DATEADD(DAY, -7, SYSDATETIME())
    ORDER BY t.createdAt DESC, t.taskID DESC;
END;
GO

-- [NEW] GetTasksByPetIDSince  -  PetService.GetTasksByPetIDSince
-- Result set 1: tasks on/after @startDate.  Result set 2: single row  hasOlder (bit).
CREATE OR ALTER PROCEDURE dbo.GetTasksByPetIDSince
    @UserID INT, @HouseholdID INT, @petID INT, @startDate DATETIME2
AS
BEGIN
    SET NOCOUNT ON;

    SELECT t.taskID, t.petID, p.[name] AS petName, t.taskType, t.notes,
           t.createdAt, t.RecordedByUserID, u.name AS RecordedByName
    FROM dbo.Tasks AS t
    INNER JOIN dbo.Pets AS p ON p.petID = t.petID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    LEFT JOIN dbo.Users u ON u.userID = t.RecordedByUserID
    WHERE t.petID = @petID
      AND p.HouseholdID = @HouseholdID
      AND t.createdAt >= @startDate
    ORDER BY t.createdAt DESC;

    SELECT CAST(CASE WHEN EXISTS
    (
        SELECT 1
        FROM dbo.Tasks AS older
        INNER JOIN dbo.Pets olderPet ON olderPet.petID = older.petID
        INNER JOIN dbo.HouseholdMembers olderMember
          ON olderMember.HouseholdID = olderPet.HouseholdID
         AND olderMember.UserID = @UserID AND olderMember.Status = N'Active'
        WHERE older.petID = @petID AND olderPet.HouseholdID = @HouseholdID
          AND older.createdAt < @startDate
    ) THEN 1 ELSE 0 END AS bit) AS hasOlder;
END;
GO

-- [NEW] GetLatestActivityTasksByPetID  -  PetService.GetLatestActivityTasksByPetID
-- The most recent Pee and the most recent Poop entry ('Pee & Poop' counts for both).
CREATE OR ALTER PROCEDURE dbo.GetLatestActivityTasksByPetID
    @UserID INT, @HouseholdID INT, @petID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT DISTINCT
        latest.taskID, latest.petID, latest.petName, latest.taskType, latest.notes,
        latest.createdAt, latest.RecordedByUserID, latest.RecordedByName
    FROM (VALUES ('Pee'), ('Poop')) AS activity(activityType)
    CROSS APPLY
    (
        SELECT TOP (1)
            t.taskID, t.petID, p.[name] AS petName, t.taskType, t.notes,
            t.createdAt, t.RecordedByUserID, u.name AS RecordedByName
        FROM dbo.Tasks AS t
        INNER JOIN dbo.Pets AS p ON p.petID = t.petID
        INNER JOIN dbo.HouseholdMembers hm
          ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
        LEFT JOIN dbo.Users u ON u.userID = t.RecordedByUserID
        WHERE t.petID = @petID
          AND p.HouseholdID = @HouseholdID
          AND (t.taskType = activity.activityType OR t.taskType = 'Pee & Poop')
        ORDER BY t.createdAt DESC
    ) AS latest
    ORDER BY latest.createdAt DESC;
END;
GO

-- [UPDATED] AddPet  -  PetService.AddPet   (the call site that prompted this audit)
-- Deployed version: allowed Owner AND Member (the app's ManagePets rule is Owner only, see
-- HOUSEHOLD-ACCOUNTS.md "Role behavior"), took NVARCHAR(255) text that overflowed the
-- varchar(100)/(50)/(20) columns, made @createdAt mandatory, and stored '' for blank text.
-- Now: Owner only, column-sized params, blank text/age/birthdate -> NULL, createdAt defaults to
-- SYSDATETIME(). Same name and parameter names as before, so older callers still bind.
-- Single guarded INSERT: no explicit transaction, so it is safe inside a caller's transaction.
-- Returns  petID  (0 = not authorized).
CREATE OR ALTER PROCEDURE dbo.AddPet
    @UserID      INT,
    @HouseholdID INT,
    @name        VARCHAR(100),
    @type        VARCHAR(50)  = NULL,
    @breed       VARCHAR(50)  = NULL,
    @age         INT          = NULL,
    @birthdate   DATE         = NULL,
    @gender      VARCHAR(20)  = NULL,
    @createdAt   DATETIME     = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT dbo.Pets (userID, HouseholdID, [name], [type], breed, age, birthdate, gender, createdAt)
    SELECT @UserID, @HouseholdID,
           LTRIM(RTRIM(COALESCE(@name, ''))),
           NULLIF(LTRIM(RTRIM(@type)), ''),
           NULLIF(LTRIM(RTRIM(@breed)), ''),
           @age, @birthdate,
           NULLIF(LTRIM(RTRIM(@gender)), ''),
           COALESCE(@createdAt, SYSDATETIME())
    WHERE EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers WITH (UPDLOCK, HOLDLOCK)
        WHERE HouseholdID = @HouseholdID AND UserID = @UserID
          AND Status = N'Active' AND Role = N'Owner'
    );

    SELECT CASE WHEN @@ROWCOUNT = 1 THEN CONVERT(INT, SCOPE_IDENTITY()) ELSE 0 END AS petID;
END;
GO

-- [UPDATED] UpdatePet  -  PetService.EditPet
-- Deployed version allowed Owner AND Member; the app's ManagePets rule is Owner only.
-- (@age is INT here on purpose - pre-existing; the app sends NULL for a blank age.)
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
       AND hm.Status = N'Active' AND hm.Role = N'Owner'
    WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [NEW] VerifyPetOwnership  -  OwnedRecordCommand.Pet
-- Returns one row when @RecordID is a pet in @HouseholdID and @UserID is an active member.
-- Locks are held only if the caller is inside a transaction (that is how OwnedRecordCommand uses it).
CREATE OR ALTER PROCEDURE dbo.VerifyPetOwnership
    @UserID INT, @HouseholdID INT, @RecordID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 1 AS Owned
    FROM dbo.Pets p WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.HouseholdMembers hm WITH (UPDLOCK, HOLDLOCK)
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE p.petID = @RecordID AND p.HouseholdID = @HouseholdID;
END;
GO

-- [NEW] VerifyTaskOwnership  -  OwnedRecordCommand.Task
CREATE OR ALTER PROCEDURE dbo.VerifyTaskOwnership
    @UserID INT, @HouseholdID INT, @RecordID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 1 AS Owned
    FROM dbo.Tasks t WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p WITH (UPDLOCK, HOLDLOCK) ON p.petID = t.petID
    INNER JOIN dbo.HouseholdMembers hm WITH (UPDLOCK, HOLDLOCK)
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE t.taskID = @RecordID AND p.HouseholdID = @HouseholdID;
END;
GO

-- [NEW] VerifyMedicationOwnership  -  OwnedRecordCommand.Medication and MedicationService.OwnsMedication(tx)
CREATE OR ALTER PROCEDURE dbo.VerifyMedicationOwnership
    @UserID INT, @HouseholdID INT, @RecordID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 1 AS Owned
    FROM dbo.Medications m WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p WITH (UPDLOCK, HOLDLOCK) ON p.petID = m.petID
    INNER JOIN dbo.HouseholdMembers hm WITH (UPDLOCK, HOLDLOCK)
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE m.medID = @RecordID AND p.HouseholdID = @HouseholdID;
END;
GO


/* =============================================================================
   HOUSEHOLDS
   ============================================================================= */

-- [UPDATED] GetHouseholdContext  -  HouseholdContextService.GetActiveHousehold / TrySetActiveHousehold(int)
-- Deployed version required @HouseholdID and did not return PublicID, so it could not replace the
-- inline "pick the active household, else fall back to the best one" query. Now @HouseholdID is
-- optional (NULL = best household for the user: Owner first, then oldest membership).
-- Backward compatible: (UserID, HouseholdID) still returns the single matching row, plus PublicID.
CREATE OR ALTER PROCEDURE dbo.GetHouseholdContext
    @UserID INT,
    @HouseholdID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (1) h.HouseholdID, h.PublicID, h.Name, hm.Role
    FROM dbo.HouseholdMembers hm
    INNER JOIN dbo.Households h ON h.HouseholdID = hm.HouseholdID
    WHERE hm.UserID = @UserID
      AND hm.Status = N'Active'
      AND (@HouseholdID IS NULL OR hm.HouseholdID = @HouseholdID)
    ORDER BY CASE hm.Role WHEN N'Owner' THEN 0 WHEN N'Member' THEN 1 ELSE 2 END,
             hm.JoinedAtUtc, h.HouseholdID;
END;
GO

-- [NEW] GetHouseholdIDByPublicID  -  HouseholdContextService.TrySetActiveHousehold(userID, Guid)
CREATE OR ALTER PROCEDURE dbo.GetHouseholdIDByPublicID
    @UserID INT, @PublicID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT h.HouseholdID
    FROM dbo.Households h
    INNER JOIN dbo.HouseholdMembers hm ON hm.HouseholdID = h.HouseholdID
    WHERE h.PublicID = @PublicID AND hm.UserID = @UserID AND hm.Status = N'Active';
END;
GO

-- [NEW] GetHouseholdMemberRole  -  HouseholdAuthorizationService.GetRole
-- One row (Role) when the user is an active member, otherwise no rows.
CREATE OR ALTER PROCEDURE dbo.GetHouseholdMemberRole
    @UserID INT, @HouseholdID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Role
    FROM dbo.HouseholdMembers
    WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active';
END;
GO

-- [UPDATED] CreateDefaultHouseholdForUser  -  HouseholdService.EnsurePersonalHousehold
-- Idempotent: returns the user's existing household or creates "<name> Household" and makes the
-- user its Owner. Deployed version returned only HouseholdID and required a caller-supplied name;
-- the app needs (HouseholdID, PublicID, Name, Role) and derives the name from the Users row
-- (name -> userName -> 'My'). @UserName stays as an optional override for existing callers.
-- Returns no rows when the user does not exist.
CREATE OR ALTER PROCEDURE dbo.CreateDefaultHouseholdForUser
    @UserID INT,
    @UserName NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @HouseholdID INT;
    DECLARE @Created TABLE (HouseholdID INT);
    BEGIN TRANSACTION;

    SELECT TOP (1) @HouseholdID = hm.HouseholdID
    FROM dbo.HouseholdMembers hm WITH (UPDLOCK, HOLDLOCK)
    WHERE hm.UserID = @UserID AND hm.Status = N'Active'
    ORDER BY hm.JoinedAtUtc, hm.HouseholdID;

    IF @HouseholdID IS NULL
    BEGIN
        INSERT dbo.Households (Name, CreatedByUserID, CreatedAtUtc, UpdatedAtUtc)
        OUTPUT INSERTED.HouseholdID INTO @Created
        SELECT LEFT(CONCAT(COALESCE(NULLIF(LTRIM(RTRIM(@UserName)), N''),
                                    NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100), u.name))), N''),
                                    NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100), u.userName))), N''),
                                    N'My'), N' Household'), 150),
               u.userID, SYSUTCDATETIME(), SYSUTCDATETIME()
        FROM dbo.Users u
        WHERE u.userID = @UserID;

        SELECT @HouseholdID = HouseholdID FROM @Created;

        IF @HouseholdID IS NULL
        BEGIN
            ROLLBACK TRANSACTION;   -- the user no longer exists
            RETURN;
        END;

        INSERT dbo.HouseholdMembers (HouseholdID, UserID, Role, Status, JoinedAtUtc, UpdatedAtUtc)
        VALUES (@HouseholdID, @UserID, N'Owner', N'Active', SYSUTCDATETIME(), SYSUTCDATETIME());
    END;

    COMMIT TRANSACTION;

    SELECT h.HouseholdID, h.PublicID, h.Name, hm.Role
    FROM dbo.Households h
    INNER JOIN dbo.HouseholdMembers hm ON hm.HouseholdID = h.HouseholdID AND hm.UserID = @UserID
    WHERE h.HouseholdID = @HouseholdID;
END;
GO

-- [UPDATED] GetHouseholdsForUser  -  HouseholdService.GetHouseholds
-- Deployed version omitted PublicID (the app maps it) and ordered by join date; the app orders by name.
CREATE OR ALTER PROCEDURE dbo.GetHouseholdsForUser
    @UserID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT h.HouseholdID, h.PublicID, h.Name, hm.Role, hm.JoinedAtUtc
    FROM dbo.HouseholdMembers hm
    INNER JOIN dbo.Households h ON h.HouseholdID = hm.HouseholdID
    WHERE hm.UserID = @UserID AND hm.Status = N'Active'
    ORDER BY h.Name, h.HouseholdID;
END;
GO

-- [UPDATED] GetHouseholdMembers  -  HouseholdService.GetMembers
-- The app reads these columns BY POSITION: PublicID, name, email, Role, JoinedAtUtc, userID.
-- Members are identified to the UI by Users.PublicID (a GUID), never the integer userID.
CREATE OR ALTER PROCEDURE dbo.GetHouseholdMembers
    @UserID INT, @HouseholdID INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers
        WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active'
    )
        RETURN;

    SELECT u.PublicID, u.name, u.email, hm.Role, hm.JoinedAtUtc, u.userID
    FROM dbo.HouseholdMembers hm
    INNER JOIN dbo.Users u ON u.userID = hm.UserID
    WHERE hm.HouseholdID = @HouseholdID AND hm.Status = N'Active'
    ORDER BY CASE hm.Role WHEN N'Owner' THEN 0 WHEN N'Member' THEN 1 ELSE 2 END,
             u.name, u.userID;
END;
GO

-- [UPDATED] GetPendingHouseholdInvitations  -  HouseholdService.GetPendingInvitations
-- The app reads these columns BY POSITION: PublicID, Email, Role, InvitedByName, CreatedAtUtc, ExpiresAtUtc,
-- and shows only invitations that have not expired. Owner only (ManageMembers).
CREATE OR ALTER PROCEDURE dbo.GetPendingHouseholdInvitations
    @UserID INT, @HouseholdID INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers
        WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active' AND Role = N'Owner'
    )
        RETURN;

    SELECT i.PublicID, i.Email, i.Role, u.name AS InvitedByName,
           i.CreatedAtUtc, i.ExpiresAtUtc
    FROM dbo.HouseholdInvitations i
    INNER JOIN dbo.Users u ON u.userID = i.InvitedByUserID
    WHERE i.HouseholdID = @HouseholdID
      AND i.AcceptedAtUtc IS NULL AND i.RevokedAtUtc IS NULL
      AND i.ExpiresAtUtc > SYSUTCDATETIME()
    ORDER BY i.CreatedAtUtc DESC;
END;
GO

-- [UPDATED] UpdateHouseholdMemberRole  -  HouseholdService.ChangeMemberRole
-- Owner only. Deployed version targeted the member by integer userID; the app identifies members by
-- Users.PublicID. Can never set or change the Owner role.  Returns Succeeded.
CREATE OR ALTER PROCEDURE dbo.UpdateHouseholdMemberRole
    @UserID INT,
    @HouseholdID INT,
    @MemberPublicID UNIQUEIDENTIFIER,
    @NewRole NVARCHAR(20)
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

    UPDATE hm
    SET Role = @NewRole, UpdatedAtUtc = SYSUTCDATETIME()
    FROM dbo.HouseholdMembers hm
    INNER JOIN dbo.Users u ON u.userID = hm.UserID
    WHERE hm.HouseholdID = @HouseholdID AND u.PublicID = @MemberPublicID
      AND hm.Status = N'Active' AND hm.Role IN (N'Member', N'Caregiver');

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [UPDATED] RemoveHouseholdMember  -  HouseholdService.RemoveMember
-- Owner only. Soft-removes (Status = 'Removed'). Deployed version targeted the member by integer
-- userID; the app uses Users.PublicID. An Owner can never be removed (nor remove themselves).
CREATE OR ALTER PROCEDURE dbo.RemoveHouseholdMember
    @UserID INT,
    @HouseholdID INT,
    @MemberPublicID UNIQUEIDENTIFIER
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

    UPDATE hm
    SET Status = N'Removed', RemovedAtUtc = SYSUTCDATETIME(),
        RemovedByUserID = @UserID, UpdatedAtUtc = SYSUTCDATETIME()
    FROM dbo.HouseholdMembers hm
    INNER JOIN dbo.Users u ON u.userID = hm.UserID
    WHERE hm.HouseholdID = @HouseholdID AND u.PublicID = @MemberPublicID
      AND hm.Status = N'Active' AND hm.Role IN (N'Member', N'Caregiver')
      AND hm.UserID <> @UserID;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO


/* =============================================================================
   HOUSEHOLD INVITATIONS
   ============================================================================= */

-- [UPDATED] CreateHouseholdInvitation  -  HouseholdInvitationService.CreateAsync
-- ResultCode: Created | AlreadyPending | AlreadyMember | InvalidRole | NotAuthorized
-- Deployed version did not return what the app needs to send the email: the invitation PublicID,
-- the household name and the inviter's name. An expired-but-unrevoked invitation still occupies the
-- unique "pending email" slot, so it is renewed in place instead of inserting a duplicate.
CREATE OR ALTER PROCEDURE dbo.CreateHouseholdInvitation
    @UserID INT,
    @HouseholdID INT,
    @Email NVARCHAR(320),
    @NormalizedEmail NVARCHAR(320),
    @Role NVARCHAR(20),
    @TokenHash VARBINARY(32),
    @ExpiresAtUtc DATETIME2(6)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @InvitationID INT = 0, @PublicID UNIQUEIDENTIFIER = NULL,
            @HouseholdName NVARCHAR(150) = NULL, @InviterName NVARCHAR(100) = NULL,
            @ExistingID INT, @ExistingExpires DATETIME2(6);

    IF @Role NOT IN (N'Member', N'Caregiver')
    BEGIN
        SELECT @InvitationID AS HouseholdInvitationID, @PublicID AS PublicID, N'InvalidRole' AS ResultCode,
               @HouseholdName AS HouseholdName, @InviterName AS InviterName;
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
        SELECT @InvitationID AS HouseholdInvitationID, @PublicID AS PublicID, N'NotAuthorized' AS ResultCode,
               @HouseholdName AS HouseholdName, @InviterName AS InviterName;
        RETURN;
    END;

    SELECT @HouseholdName = h.Name, @InviterName = CONVERT(nvarchar(100), u.name)
    FROM dbo.Households h
    INNER JOIN dbo.Users u ON u.userID = @UserID
    WHERE h.HouseholdID = @HouseholdID;

    IF EXISTS
    (
        SELECT 1
        FROM dbo.HouseholdMembers hm
        INNER JOIN dbo.Users u ON u.userID = hm.UserID
        WHERE hm.HouseholdID = @HouseholdID AND hm.Status = N'Active'
          AND LOWER(LTRIM(RTRIM(CONVERT(nvarchar(320), u.email)))) = @NormalizedEmail
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT @InvitationID AS HouseholdInvitationID, @PublicID AS PublicID, N'AlreadyMember' AS ResultCode,
               @HouseholdName AS HouseholdName, @InviterName AS InviterName;
        RETURN;
    END;

    SELECT TOP (1) @ExistingID = HouseholdInvitationID, @ExistingExpires = ExpiresAtUtc, @PublicID = PublicID
    FROM dbo.HouseholdInvitations WITH (UPDLOCK, HOLDLOCK)
    WHERE HouseholdID = @HouseholdID AND NormalizedEmail = @NormalizedEmail
      AND AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL;

    IF @ExistingID IS NOT NULL AND @ExistingExpires > SYSUTCDATETIME()
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT @ExistingID AS HouseholdInvitationID, @PublicID AS PublicID, N'AlreadyPending' AS ResultCode,
               @HouseholdName AS HouseholdName, @InviterName AS InviterName;
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
        SELECT @ExistingID AS HouseholdInvitationID, @PublicID AS PublicID, N'Created' AS ResultCode,
               @HouseholdName AS HouseholdName, @InviterName AS InviterName;
        RETURN;
    END;

    DECLARE @Inserted TABLE (HouseholdInvitationID INT, PublicID UNIQUEIDENTIFIER);

    INSERT dbo.HouseholdInvitations
        (HouseholdID, Email, NormalizedEmail, Role, TokenHash, InvitedByUserID, CreatedAtUtc, ExpiresAtUtc)
    OUTPUT INSERTED.HouseholdInvitationID, INSERTED.PublicID INTO @Inserted
    VALUES
        (@HouseholdID, @Email, @NormalizedEmail, @Role, @TokenHash, @UserID, SYSUTCDATETIME(), @ExpiresAtUtc);

    COMMIT TRANSACTION;

    SELECT HouseholdInvitationID, PublicID, N'Created' AS ResultCode,
           @HouseholdName AS HouseholdName, @InviterName AS InviterName
    FROM @Inserted;
END;
GO

-- [UPDATED] ResendHouseholdInvitation  -  HouseholdInvitationService.ResendAsync
-- ResultCode: Sent | NotAuthorized | NotPending | TooSoon
-- Deployed version addressed the invitation by integer id (the app uses its PublicID GUID), did not
-- return the recipient/household/inviter needed to send the email, and enforced a 30 s throttle the app
-- never had. @MinResendIntervalSeconds now defaults to 0 (no throttle); pass 30 to re-enable it.
CREATE OR ALTER PROCEDURE dbo.ResendHouseholdInvitation
    @UserID INT,
    @HouseholdID INT,
    @InvitationPublicID UNIQUEIDENTIFIER,
    @NewTokenHash VARBINARY(32),
    @NewExpiresAtUtc DATETIME2(6),
    @MinResendIntervalSeconds INT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @InvitationID INT, @Email NVARCHAR(320), @Role NVARCHAR(20), @LastSent DATETIME2(6),
            @HouseholdName NVARCHAR(150), @InviterName NVARCHAR(100);

    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers WITH (UPDLOCK, HOLDLOCK)
        WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active' AND Role = N'Owner'
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'NotAuthorized' AS ResultCode, CAST(NULL AS NVARCHAR(320)) AS Email,
               CAST(NULL AS NVARCHAR(150)) AS HouseholdName, CAST(NULL AS NVARCHAR(100)) AS InviterName,
               CAST(NULL AS NVARCHAR(20)) AS Role;
        RETURN;
    END;

    SELECT @InvitationID = i.HouseholdInvitationID, @Email = i.Email, @Role = i.Role,
           @LastSent = i.LastSentAtUtc, @HouseholdName = h.Name
    FROM dbo.HouseholdInvitations i WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Households h ON h.HouseholdID = i.HouseholdID
    WHERE i.PublicID = @InvitationPublicID AND i.HouseholdID = @HouseholdID
      AND i.AcceptedAtUtc IS NULL AND i.RevokedAtUtc IS NULL;

    IF @InvitationID IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'NotPending' AS ResultCode, CAST(NULL AS NVARCHAR(320)) AS Email,
               CAST(NULL AS NVARCHAR(150)) AS HouseholdName, CAST(NULL AS NVARCHAR(100)) AS InviterName,
               CAST(NULL AS NVARCHAR(20)) AS Role;
        RETURN;
    END;

    IF @MinResendIntervalSeconds > 0 AND @LastSent IS NOT NULL
       AND DATEDIFF(SECOND, @LastSent, SYSUTCDATETIME()) < @MinResendIntervalSeconds
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'TooSoon' AS ResultCode, CAST(NULL AS NVARCHAR(320)) AS Email,
               CAST(NULL AS NVARCHAR(150)) AS HouseholdName, CAST(NULL AS NVARCHAR(100)) AS InviterName,
               CAST(NULL AS NVARCHAR(20)) AS Role;
        RETURN;
    END;

    SELECT @InviterName = CONVERT(nvarchar(100), name) FROM dbo.Users WHERE userID = @UserID;

    UPDATE dbo.HouseholdInvitations
    SET TokenHash = @NewTokenHash, CreatedAtUtc = SYSUTCDATETIME(),
        ExpiresAtUtc = @NewExpiresAtUtc, InvitedByUserID = @UserID,
        LastSentAtUtc = SYSUTCDATETIME()
    WHERE HouseholdInvitationID = @InvitationID;

    COMMIT TRANSACTION;
    SELECT CAST(1 AS BIT) AS Succeeded, N'Sent' AS ResultCode, @Email AS Email,
           @HouseholdName AS HouseholdName, @InviterName AS InviterName, @Role AS Role;
END;
GO

-- [UPDATED] RevokeHouseholdInvitation  -  HouseholdInvitationService.Revoke
-- Owner only. Deployed version addressed the invitation by integer id; the app uses its PublicID.
CREATE OR ALTER PROCEDURE dbo.RevokeHouseholdInvitation
    @UserID INT,
    @HouseholdID INT,
    @InvitationPublicID UNIQUEIDENTIFIER
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
    WHERE PublicID = @InvitationPublicID AND HouseholdID = @HouseholdID
      AND AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [UPDATED] GetInvitationByTokenHash  -  HouseholdInvitationService.Validate
-- Anonymous-safe lookup by hashed token (needed before the recipient signs in). Adds PublicID and
-- AcceptedByUserID, which the app maps, to the deployed column list.
CREATE OR ALTER PROCEDURE dbo.GetInvitationByTokenHash
    @TokenHash VARBINARY(32)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT i.PublicID, i.HouseholdInvitationID, i.HouseholdID, h.Name AS HouseholdName,
           i.Email, i.NormalizedEmail, i.Role, u.name AS InvitedByName,
           i.ExpiresAtUtc, i.AcceptedAtUtc, i.RevokedAtUtc, i.AcceptedByUserID
    FROM dbo.HouseholdInvitations i
    INNER JOIN dbo.Households h ON h.HouseholdID = i.HouseholdID
    INNER JOIN dbo.Users u ON u.userID = i.InvitedByUserID
    WHERE i.TokenHash = @TokenHash;
END;
GO

-- [UPDATED] AcceptHouseholdInvitation  -  HouseholdInvitationService.Accept
-- ResultCode: Accepted | AlreadyAccepted (same user, Succeeded=1) | AlreadyUsed (a different user)
--             | NotFound | Revoked | Expired | EmailMismatch
-- The accepting user's email is read from Users inside the transaction (the caller can no longer
-- pass one). An already-active member keeps their current role instead of being re-assigned the
-- invited role. Columns returned: Succeeded, ResultCode, HouseholdID, HouseholdName, InvitationPublicID.
CREATE OR ALTER PROCEDURE dbo.AcceptHouseholdInvitation
    @TokenHash VARBINARY(32),
    @AcceptingUserID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @InvitationID INT, @InvitationPublicID UNIQUEIDENTIFIER, @HouseholdID INT, @HouseholdName NVARCHAR(150),
            @InviteEmail NVARCHAR(320), @Role NVARCHAR(20), @ExpiresAtUtc DATETIME2(6),
            @AcceptedAtUtc DATETIME2(6), @AcceptedByUserID INT, @RevokedAtUtc DATETIME2(6), @AccountEmail NVARCHAR(320);

    BEGIN TRANSACTION;

    SELECT @InvitationID = i.HouseholdInvitationID, @InvitationPublicID = i.PublicID, @HouseholdID = i.HouseholdID,
           @HouseholdName = h.Name, @InviteEmail = LOWER(LTRIM(RTRIM(i.Email))), @Role = i.Role,
           @ExpiresAtUtc = i.ExpiresAtUtc, @AcceptedAtUtc = i.AcceptedAtUtc,
           @AcceptedByUserID = i.AcceptedByUserID, @RevokedAtUtc = i.RevokedAtUtc
    FROM dbo.HouseholdInvitations i WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Households h ON h.HouseholdID = i.HouseholdID
    WHERE i.TokenHash = @TokenHash;

    IF @InvitationID IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'NotFound' AS ResultCode, CAST(NULL AS INT) AS HouseholdID,
               CAST(NULL AS NVARCHAR(150)) AS HouseholdName, CAST(NULL AS UNIQUEIDENTIFIER) AS InvitationPublicID;
        RETURN;
    END;

    IF @RevokedAtUtc IS NOT NULL
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'Revoked' AS ResultCode, @HouseholdID AS HouseholdID,
               @HouseholdName AS HouseholdName, @InvitationPublicID AS InvitationPublicID;
        RETURN;
    END;

    IF @AcceptedAtUtc IS NOT NULL
    BEGIN
        IF @AcceptedByUserID = @AcceptingUserID
        BEGIN
            COMMIT TRANSACTION;
            SELECT CAST(1 AS BIT) AS Succeeded, N'AlreadyAccepted' AS ResultCode, @HouseholdID AS HouseholdID,
                   @HouseholdName AS HouseholdName, @InvitationPublicID AS InvitationPublicID;
            RETURN;
        END;

        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'AlreadyUsed' AS ResultCode, @HouseholdID AS HouseholdID,
               @HouseholdName AS HouseholdName, @InvitationPublicID AS InvitationPublicID;
        RETURN;
    END;

    IF @ExpiresAtUtc <= SYSUTCDATETIME()
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'Expired' AS ResultCode, @HouseholdID AS HouseholdID,
               @HouseholdName AS HouseholdName, @InvitationPublicID AS InvitationPublicID;
        RETURN;
    END;

    SELECT @AccountEmail = LOWER(LTRIM(RTRIM(CONVERT(nvarchar(320), email))))
    FROM dbo.Users WITH (UPDLOCK, HOLDLOCK)
    WHERE userID = @AcceptingUserID;

    IF @AccountEmail IS NULL OR @AccountEmail <> @InviteEmail
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded, N'EmailMismatch' AS ResultCode, @HouseholdID AS HouseholdID,
               @HouseholdName AS HouseholdName, @InvitationPublicID AS InvitationPublicID;
        RETURN;
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.HouseholdMembers WITH (UPDLOCK, HOLDLOCK)
        WHERE HouseholdID = @HouseholdID AND UserID = @AcceptingUserID AND Status = N'Active'
    )
    BEGIN
        MERGE dbo.HouseholdMembers WITH (HOLDLOCK) AS target
        USING (SELECT @HouseholdID AS HouseholdID, @AcceptingUserID AS UserID) AS source
           ON target.HouseholdID = source.HouseholdID AND target.UserID = source.UserID
        WHEN MATCHED THEN UPDATE
            SET Role = @Role, Status = N'Active', JoinedAtUtc = SYSUTCDATETIME(),
                UpdatedAtUtc = SYSUTCDATETIME(), RemovedAtUtc = NULL, RemovedByUserID = NULL
        WHEN NOT MATCHED THEN INSERT
            (HouseholdID, UserID, Role, Status, JoinedAtUtc, UpdatedAtUtc)
            VALUES (@HouseholdID, @AcceptingUserID, @Role, N'Active', SYSUTCDATETIME(), SYSUTCDATETIME());
    END;

    UPDATE dbo.HouseholdInvitations
    SET AcceptedAtUtc = SYSUTCDATETIME(), AcceptedByUserID = @AcceptingUserID
    WHERE HouseholdInvitationID = @InvitationID AND AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL;

    COMMIT TRANSACTION;
    SELECT CAST(1 AS BIT) AS Succeeded, N'Accepted' AS ResultCode, @HouseholdID AS HouseholdID,
           @HouseholdName AS HouseholdName, @InvitationPublicID AS InvitationPublicID;
END;
GO


/* =============================================================================
   MEDICATIONS
   ============================================================================= */

-- [UPDATED] GetMedicationsByPetID  -  MedicationService.GetMedicationsByPetID
-- Deployed version ordered by startDate DESC; the app lists medications by name. Adds petID
-- (part of the inline column list).
CREATE OR ALTER PROCEDURE dbo.GetMedicationsByPetID
    @UserID INT, @HouseholdID INT, @petID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT m.medID, m.petID, m.medicationName, m.dosage, m.frequencyType,
           m.frequencyInterval, m.TimingDoesNotMatter, m.startDate, m.endDate, m.notes
    FROM dbo.Medications m
    INNER JOIN dbo.Pets p ON p.petID = m.petID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE m.petID = @petID AND p.HouseholdID = @HouseholdID
    ORDER BY m.medicationName, m.medID;
END;
GO

-- [NEW] CheckMedicationAccess  -  MedicationService.OwnsMedication  (read-only, takes no locks)
CREATE OR ALTER PROCEDURE dbo.CheckMedicationAccess
    @UserID INT, @HouseholdID INT, @MedID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 1 AS HasAccess
    FROM dbo.Medications m
    INNER JOIN dbo.Pets p ON p.petID = m.petID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE m.medID = @MedID AND p.HouseholdID = @HouseholdID;
END;
GO

-- [NEW] FindMedicationScheduleForDose  -  MedicationService.FindSchedule
-- Locks and returns the schedule row a dose applies to: the same calendar day when the medication's
-- timing does not matter, otherwise the exact scheduled instant. Prefers an already-confirmed row.
CREATE OR ALTER PROCEDURE dbo.FindMedicationScheduleForDose
    @UserID INT, @HouseholdID INT, @MedID INT, @LogDate DATETIME2
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (1) ms.scheduleID, m.TimingDoesNotMatter
    FROM dbo.MedicationSchedule ms WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Medications m ON m.medID = ms.medID
    INNER JOIN dbo.Pets p ON p.petID = m.petID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE ms.medID = @MedID AND p.HouseholdID = @HouseholdID
      AND
      (
          (m.TimingDoesNotMatter = 1
           AND CONVERT(date, ms.scheduleDate) = CONVERT(date, @LogDate))
          OR
          (m.TimingDoesNotMatter = 0 AND ms.scheduleDate = @LogDate)
      )
    ORDER BY CASE WHEN ms.isConfirmed = 1 THEN 0 ELSE 1 END, ms.scheduleID;
END;
GO

-- [NEW] UpdateMedicationScheduleDose  -  MedicationService.RecordDose (the final UPDATE)
-- Records a dose outcome (Taken / Taken late / Skipped / Missed) on one schedule row. The inline
-- statement relied on an earlier ownership check; the household guard is now inside the statement.
CREATE OR ALTER PROCEDURE dbo.UpdateMedicationScheduleDose
    @UserID INT, @HouseholdID INT, @ScheduleID INT,
    @DoseStatus NVARCHAR(20), @IsConfirmed BIT, @AdministeredAtUtc DATETIME2 = NULL,
    @StatusReason NVARCHAR(500) = NULL, @AdministrationNotes NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE ms
    SET DoseStatus = @DoseStatus,
        isConfirmed = @IsConfirmed,
        confirmedAt = CASE WHEN @IsConfirmed = 1 THEN ms.confirmedAt ELSE NULL END,
        AdministeredAtUtc = @AdministeredAtUtc,
        RecordedAtUtc = SYSUTCDATETIME(),
        RecordedByUserID = @UserID,
        StatusReason = @StatusReason,
        AdministrationNotes = @AdministrationNotes
    FROM dbo.MedicationSchedule ms
    INNER JOIN dbo.Medications m ON m.medID = ms.medID
    INNER JOIN dbo.Pets p ON p.petID = m.petID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE ms.scheduleID = @ScheduleID AND p.HouseholdID = @HouseholdID;

    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [UPDATED] UnconfirmMedicationSchedule  -  MedicationService.UnconfirmSchedule
-- The deployed version returned nothing, so the caller could not tell "reset" from "nothing matched".
-- Now returns Succeeded and does the find + reset atomically. Any active member may do it (RecordCare).
CREATE OR ALTER PROCEDURE dbo.UnconfirmMedicationSchedule
    @UserID INT, @HouseholdID INT, @medID INT, @logDate DATETIME2(0)
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
           AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
        WHERE m.medID = @medID AND p.HouseholdID = @HouseholdID
    )
    BEGIN
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    DECLARE @timingDoesNotMatter bit = 0;
    DECLARE @scheduleID int;

    BEGIN TRANSACTION;

    SELECT @timingDoesNotMatter = CASE
        WHEN UPPER(LTRIM(RTRIM(ISNULL(frequencyType, N'')))) = N'HOURLY' THEN 0
        ELSE TimingDoesNotMatter
    END
    FROM dbo.Medications
    WHERE medID = @medID;

    SELECT TOP (1) @scheduleID = scheduleID
    FROM dbo.MedicationSchedule WITH (UPDLOCK, HOLDLOCK)
    WHERE medID = @medID
      AND
      (
          (@timingDoesNotMatter = 1 AND CONVERT(date, scheduleDate) = CONVERT(date, @logDate))
          OR
          (@timingDoesNotMatter = 0 AND scheduleDate = @logDate)
      )
    ORDER BY CASE WHEN isConfirmed = 1 THEN 0 ELSE 1 END, scheduleID;

    IF @scheduleID IS NULL
    BEGIN
        COMMIT TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    UPDATE dbo.MedicationSchedule
    SET isConfirmed = 0, confirmedAt = NULL, DoseStatus = N'Due',
        AdministeredAtUtc = NULL, RecordedAtUtc = NULL, RecordedByUserID = NULL,
        AdministrationNotes = NULL, StatusReason = NULL
    WHERE scheduleID = @scheduleID;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO


/* =============================================================================
   HEALTH
   ============================================================================= */

-- [UPDATED] GetHealthTimeline  -  HealthService.GetTimeline
-- The deployed version returned raw dose data for an app-side adherence calculation that no longer
-- exists. The app now needs the timeline in the CALLER'S time zone with the Missed status computed
-- here: @UtcOffsetMinutes / @NowUtc / @MissedAfterMinutes (defaults: UTC, now, 720 = configuration
-- MedicationAdherence:MissedAfterMinutes). Columns: SourceType, SourceID, PetID, PetName, EventAtUtc,
-- Title, Summary, Attribution, Status, Severity, Url.
CREATE OR ALTER PROCEDURE dbo.GetHealthTimeline
    @UserID INT, @HouseholdID INT, @PetID INT = NULL,
    @StartUtc DATETIME2(6), @EndUtc DATETIME2(6),
    @UtcOffsetMinutes INT = 0, @NowUtc DATETIME2(6) = NULL, @MissedAfterMinutes INT = 720
AS
BEGIN
    SET NOCOUNT ON;

    SET @NowUtc = COALESCE(@NowUtc, SYSUTCDATETIME());

    SELECT SourceType, SourceID, PetID, PetName, EventAtUtc, Title, Summary,
           Attribution, Status, Severity, Url
    FROM
    (
        SELECT h.EventKind AS SourceType,
               h.HealthEventID AS SourceID,
               h.PetID,
               CONVERT(nvarchar(100), p.name) AS PetName,
               h.OccurredAtUtc AS EventAtUtc,
               h.EventType AS Title,
               h.Description AS Summary,
               CONVERT(nvarchar(100), u.name) AS Attribution,
               h.RecoveryStatus AS Status,
               CONVERT(int, h.Severity) AS Severity,
               CONCAT(N'/Health?petID=', h.PetID, N'#health-event-', h.HealthEventID) AS Url
        FROM dbo.HealthEvents h
        INNER JOIN dbo.Pets p ON p.petID = h.PetID
        LEFT JOIN dbo.Users u ON u.userID = h.CreatedByUserID
        INNER JOIN dbo.HouseholdMembers hm
          ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
        WHERE p.HouseholdID = @HouseholdID AND h.IsDeleted = 0
          AND (@PetID IS NULL OR h.PetID = @PetID)

        UNION ALL

        SELECT N'Medication', ms.scheduleID, m.petID, CONVERT(nvarchar(100), p.name),
               COALESCE(ms.AdministeredAtUtc,
                   DATEADD(MINUTE, @UtcOffsetMinutes, CONVERT(datetime2(0), ms.scheduleDate))),
               CONVERT(nvarchar(100), m.medicationName),
               CONVERT(nvarchar(2000), CONCAT(ISNULL(m.dosage, N''),
                   CASE WHEN NULLIF(ms.AdministrationNotes, N'') IS NULL THEN N''
                        ELSE CONCAT(N' ', NCHAR(8212), N' ', ms.AdministrationNotes) END)),
               CONVERT(nvarchar(100), u.name),
               CASE
                   WHEN ms.DoseStatus <> N'Due' THEN ms.DoseStatus
                   WHEN m.TimingDoesNotMatter = 1 AND @NowUtc >= DATEADD(MINUTE, @MissedAfterMinutes,
                       DATEADD(MINUTE, @UtcOffsetMinutes,
                           DATEADD(DAY, 1, CONVERT(datetime2(0), CONVERT(date, ms.scheduleDate))))) THEN N'Missed'
                   WHEN m.TimingDoesNotMatter = 0 AND @NowUtc >= DATEADD(MINUTE, @MissedAfterMinutes,
                       DATEADD(MINUTE, @UtcOffsetMinutes, CONVERT(datetime2(0), ms.scheduleDate))) THEN N'Missed'
                   ELSE N'Due'
               END,
               NULL,
               CONCAT(N'/Medications?petID=', m.petID, N'&editMedID=', m.medID)
        FROM dbo.MedicationSchedule ms
        INNER JOIN dbo.Medications m ON m.medID = ms.medID
        INNER JOIN dbo.Pets p ON p.petID = m.petID
        LEFT JOIN dbo.Users u ON u.userID = ms.RecordedByUserID
        INNER JOIN dbo.HouseholdMembers medicationMember
          ON medicationMember.HouseholdID = p.HouseholdID
         AND medicationMember.UserID = @UserID AND medicationMember.Status = N'Active'
        WHERE p.HouseholdID = @HouseholdID AND (@PetID IS NULL OR m.petID = @PetID)

        UNION ALL

        SELECT N'VetVisit', v.VetVisitID, v.PetID, CONVERT(nvarchar(100), p.name),
               DATEADD(MINUTE, @UtcOffsetMinutes,
                   DATEADD(SECOND,
                       DATEDIFF(SECOND, CONVERT(time(0), '00:00'),
                           COALESCE(v.VisitTime, CONVERT(time(0), '09:00'))),
                       CONVERT(datetime2(0), CONVERT(date, v.VisitDate)))),
               CONVERT(nvarchar(100), CONCAT(N'Vet visit: ', v.VisitReason)),
               CONVERT(nvarchar(2000), CONCAT(v.ClinicName,
                   CASE WHEN NULLIF(v.VisitSummary, N'') IS NULL THEN N''
                        ELSE CONCAT(N' ', NCHAR(8212), N' ', v.VisitSummary) END)),
               CONVERT(nvarchar(100), visitUser.name), CONVERT(nvarchar(30), v.Status), NULL,
               CONCAT(N'/VetVisits?petID=', v.PetID, N'&vetVisitID=', v.VetVisitID)
        FROM dbo.VetVisits v
        INNER JOIN dbo.Pets p ON p.petID = v.PetID
        LEFT JOIN dbo.Users visitUser ON visitUser.userID = v.CreatedByUserID
        INNER JOIN dbo.HouseholdMembers visitMember
          ON visitMember.HouseholdID = p.HouseholdID
         AND visitMember.UserID = @UserID AND visitMember.Status = N'Active'
        WHERE p.HouseholdID = @HouseholdID AND v.IsDeleted = 0
          AND (@PetID IS NULL OR v.PetID = @PetID)
    ) timeline
    WHERE EventAtUtc >= @StartUtc AND EventAtUtc < @EndUtc
    ORDER BY EventAtUtc, SourceType, SourceID;
END;
GO


/* =============================================================================
   VET VISITS
   ============================================================================= */

-- [UPDATED] AddVetVisit  -  VetVisitService.AddVisit
-- The deployed version wrote the 'Created' history row WITHOUT ChangedByUserID, so the history could not
-- show who created the visit (HOUSEHOLD-ACCOUNTS.md smoke check 17). It already stores
-- CreatedByUserID on the visit itself, which makes the C# follow-up "attribution" UPDATE redundant.
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

    INSERT dbo.VetVisitHistory (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt, ChangedByUserID)
    VALUES (@VetVisitID, N'Created', NULL, @Status, N'Visit record created.', SYSDATETIME(), @UserID);

    COMMIT TRANSACTION;
    SELECT @VetVisitID AS VetVisitID;
END;
GO

-- [UPDATED] UpdateVetVisit  -  VetVisitService.UpdateVisit
-- Keeps the deployed race-safe MERGE for the reminder row and adds ChangedByUserID to the history row.
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
    INSERT dbo.VetVisitHistory (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt, ChangedByUserID)
    VALUES
        (@VetVisitID, CASE WHEN @StatusChanged = 1 THEN N'Status changed' ELSE N'Updated' END,
         CASE WHEN @StatusChanged = 1 THEN @OldStatus ELSE NULL END,
         CASE WHEN @StatusChanged = 1 THEN @Status ELSE NULL END,
         CASE WHEN @StatusChanged = 1 THEN CONCAT(N'Status changed from ', @OldStatus, N' to ', @Status, N'.')
              ELSE N'Visit details updated.' END,
         SYSDATETIME(), @UserID);

    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

-- [UPDATED] ChangeVetVisitStatus  -  VetVisitService.ChangeStatus  (adds ChangedByUserID to history)
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

    INSERT dbo.VetVisitHistory (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt, ChangedByUserID)
    VALUES
        (@VetVisitID, N'Status changed', @OldStatus, @Status,
         CASE WHEN LTRIM(RTRIM(COALESCE(@Details, N''))) = N'' THEN CONCAT(N'Status changed to ', @Status, N'.')
              ELSE LTRIM(RTRIM(@Details)) END,
         SYSDATETIME(), @UserID);

    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

-- [UPDATED] CompleteVetVisit  -  VetVisitService.CompleteVisit  (adds ChangedByUserID to history)
-- NOTE (not changed here): @Diagnosis is nvarchar(2000) and the app allows 2000 characters, but
-- VetVisits.Diagnosis is nvarchar(1000). A diagnosis of 1001-2000 characters raises error 8152.
-- Fix with:  ALTER TABLE dbo.VetVisits ALTER COLUMN Diagnosis nvarchar(2000) NULL;
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

    INSERT dbo.VetVisitHistory (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt, ChangedByUserID)
    VALUES (@VetVisitID, N'Completed', @OldStatus, N'Completed', N'Visit marked completed and medical outcome recorded.', SYSDATETIME(), @UserID);

    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO

-- [NEW] GetVetVisits  -  VetVisitService.GetVisits   (@PetID NULL = all pets in the household)
CREATE OR ALTER PROCEDURE dbo.GetVetVisits
    @UserID INT, @HouseholdID INT, @PetID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT v.VetVisitID, v.PetID, p.name AS PetName, v.VisitDate, v.VisitTime,
           v.IsAllDay, v.ClinicName, v.VeterinarianName, v.VisitReason,
           v.VisitType, v.Location, v.PhoneNumber, v.Status, v.Notes,
           v.FollowUpDate, v.Cost, v.IsEmergency, v.PreparationInstructions,
           v.VisitSummary, v.Diagnosis, v.TreatmentProvided,
           v.VaccinationsReceived, v.Prescriptions, v.FollowUpInstructions,
           v.CreatedAt, v.UpdatedAt, v.CreatedByUserID, creator.name AS CreatedByName,
           r.VetVisitReminderID AS ReminderID,
           r.ReminderAt, r.Status AS ReminderStatus
    FROM dbo.VetVisits v
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    LEFT JOIN dbo.Users creator ON creator.userID = v.CreatedByUserID
    LEFT JOIN dbo.VetVisitReminders r ON r.VetVisitID = v.VetVisitID
    WHERE p.HouseholdID = @HouseholdID
      AND v.IsDeleted = 0
      AND (@PetID IS NULL OR v.PetID = @PetID)
    ORDER BY v.VisitDate DESC,
             CASE WHEN v.VisitTime IS NULL THEN 1 ELSE 0 END,
             v.VisitTime DESC, v.VetVisitID DESC;
END;
GO

-- [NEW] GetVetVisitByID  -  VetVisitService.GetVisit
CREATE OR ALTER PROCEDURE dbo.GetVetVisitByID
    @UserID INT, @HouseholdID INT, @VetVisitID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT v.VetVisitID, v.PetID, p.name AS PetName, v.VisitDate, v.VisitTime,
           v.IsAllDay, v.ClinicName, v.VeterinarianName, v.VisitReason,
           v.VisitType, v.Location, v.PhoneNumber, v.Status, v.Notes,
           v.FollowUpDate, v.Cost, v.IsEmergency, v.PreparationInstructions,
           v.VisitSummary, v.Diagnosis, v.TreatmentProvided,
           v.VaccinationsReceived, v.Prescriptions, v.FollowUpInstructions,
           v.CreatedAt, v.UpdatedAt, v.CreatedByUserID, creator.name AS CreatedByName,
           r.VetVisitReminderID AS ReminderID,
           r.ReminderAt, r.Status AS ReminderStatus
    FROM dbo.VetVisits v
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    LEFT JOIN dbo.Users creator ON creator.userID = v.CreatedByUserID
    LEFT JOIN dbo.VetVisitReminders r ON r.VetVisitID = v.VetVisitID
    WHERE p.HouseholdID = @HouseholdID AND v.VetVisitID = @VetVisitID AND v.IsDeleted = 0;
END;
GO

-- [NEW] RefreshVetVisitReminderStatuses  -  VetVisitService.RefreshReminderStatuses
-- Moves Pending/Displayed reminders to Displayed/Expired/Cancelled based on the visit and the clock.
-- Returns nothing.
CREATE OR ALTER PROCEDURE dbo.RefreshVetVisitReminderStatuses
    @UserID INT, @HouseholdID INT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE r
    SET Status = CASE
        WHEN v.Status NOT IN (N'Scheduled', N'Confirmed', N'Rescheduled') THEN N'Cancelled'
        WHEN v.VisitDate < CAST(SYSDATETIME() AS date)
          OR (v.VisitDate = CAST(SYSDATETIME() AS date)
              AND v.VisitTime IS NOT NULL
              AND DATEADD(SECOND,
                  DATEDIFF(SECOND, CAST(N'00:00:00' AS time), v.VisitTime),
                  CAST(v.VisitDate AS datetime2)) < SYSDATETIME())
        THEN N'Expired'
        WHEN r.Status = N'Pending' AND r.ReminderAt <= SYSDATETIME() THEN N'Displayed'
        ELSE r.Status END,
        DisplayedAt = CASE
            WHEN r.Status = N'Pending' AND r.ReminderAt <= SYSDATETIME()
            THEN COALESCE(r.DisplayedAt, SYSDATETIME()) ELSE r.DisplayedAt END,
        UpdatedAt = SYSDATETIME()
    FROM dbo.VetVisitReminders r
    INNER JOIN dbo.VetVisits v ON v.VetVisitID = r.VetVisitID
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE p.HouseholdID = @HouseholdID
      AND r.Status IN (N'Pending', N'Displayed');
END;
GO

-- [NEW] DismissVetVisitReminder  -  VetVisitService.DismissReminder   (any active member: RecordCare)
CREATE OR ALTER PROCEDURE dbo.DismissVetVisitReminder
    @UserID INT, @HouseholdID INT, @ReminderID INT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE r SET Status = N'Dismissed', DismissedAt = SYSDATETIME()
    FROM dbo.VetVisitReminders r
    INNER JOIN dbo.VetVisits v ON v.VetVisitID = r.VetVisitID
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE r.VetVisitReminderID = @ReminderID AND p.HouseholdID = @HouseholdID
      AND r.Status IN (N'Pending', N'Displayed');

    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [NEW] GetVetVisitDocuments  -  VetVisitService.GetDocuments
CREATE OR ALTER PROCEDURE dbo.GetVetVisitDocuments
    @UserID INT, @HouseholdID INT, @VetVisitID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT d.VetVisitDocumentID, d.VetVisitID, d.DocumentType, d.DisplayName,
           d.OriginalFileName, d.StoredPath, d.ContentType, d.FileSizeBytes,
           d.Description, d.CreatedAt
    FROM dbo.VetVisitDocuments d
    INNER JOIN dbo.VetVisits v ON v.VetVisitID = d.VetVisitID
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE d.VetVisitID = @VetVisitID AND p.HouseholdID = @HouseholdID AND v.IsDeleted = 0
    ORDER BY d.CreatedAt DESC;
END;
GO

-- [NEW] GetVetVisitDocumentByID  -  VetVisitService.GetDocument
CREATE OR ALTER PROCEDURE dbo.GetVetVisitDocumentByID
    @UserID INT, @HouseholdID INT, @DocumentID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT d.VetVisitDocumentID, d.VetVisitID, d.DocumentType, d.DisplayName,
           d.OriginalFileName, d.StoredPath, d.ContentType, d.FileSizeBytes,
           d.Description, d.CreatedAt
    FROM dbo.VetVisitDocuments d
    INNER JOIN dbo.VetVisits v ON v.VetVisitID = d.VetVisitID
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE d.VetVisitDocumentID = @DocumentID AND p.HouseholdID = @HouseholdID
      AND v.IsDeleted = 0;
END;
GO

-- [NEW] AddVetVisitDocument  -  VetVisitService.AddDocument
-- Owner/Member only (ManageCarePlans). Returns VetVisitDocumentID (0 = denied).
CREATE OR ALTER PROCEDURE dbo.AddVetVisitDocument
    @UserID INT, @HouseholdID INT, @VetVisitID INT,
    @DocumentType NVARCHAR(50), @DisplayName NVARCHAR(260), @OriginalFileName NVARCHAR(260),
    @StoredPath NVARCHAR(500), @ContentType NVARCHAR(150), @FileSizeBytes BIGINT,
    @Description NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT dbo.VetVisitDocuments
        (VetVisitID, DocumentType, DisplayName, OriginalFileName, StoredPath,
         ContentType, FileSizeBytes, Description, CreatedAt)
    SELECT @VetVisitID, @DocumentType, @DisplayName, @OriginalFileName, @StoredPath,
           @ContentType, @FileSizeBytes, @Description, SYSDATETIME()
    WHERE EXISTS
        (SELECT 1
         FROM dbo.VetVisits v
         INNER JOIN dbo.Pets p ON p.petID = v.PetID
         INNER JOIN dbo.HouseholdMembers hm
           ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
          AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
         WHERE v.VetVisitID = @VetVisitID AND p.HouseholdID = @HouseholdID AND v.IsDeleted = 0);

    SELECT CASE WHEN @@ROWCOUNT = 1 THEN CONVERT(INT, SCOPE_IDENTITY()) ELSE 0 END AS VetVisitDocumentID;
END;
GO

-- [NEW] UpdateVetVisitDocument  -  VetVisitService.UpdateDocument   (Owner/Member only)
CREATE OR ALTER PROCEDURE dbo.UpdateVetVisitDocument
    @UserID INT, @HouseholdID INT, @DocumentID INT,
    @DocumentType NVARCHAR(50), @DisplayName NVARCHAR(260), @Description NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE d
    SET DocumentType = @DocumentType, DisplayName = @DisplayName,
        Description = @Description
    FROM dbo.VetVisitDocuments d
    INNER JOIN dbo.VetVisits v ON v.VetVisitID = d.VetVisitID
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
     AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
    WHERE d.VetVisitDocumentID = @DocumentID AND p.HouseholdID = @HouseholdID
      AND v.IsDeleted = 0;

    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [NEW] DeleteVetVisitDocument  -  VetVisitService.DeleteDocument   (Owner/Member only)
-- Deletes the row, writes the 'Document removed' history entry (attributed to @UserID) and returns
-- the deleted row so the app can remove the file from disk. Zero rows = nothing deleted / denied.
CREATE OR ALTER PROCEDURE dbo.DeleteVetVisitDocument
    @UserID INT, @HouseholdID INT, @DocumentID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Deleted TABLE
    (
        VetVisitDocumentID INT, VetVisitID INT, DocumentType NVARCHAR(50), DisplayName NVARCHAR(260),
        OriginalFileName NVARCHAR(260), StoredPath NVARCHAR(500), ContentType NVARCHAR(150),
        FileSizeBytes BIGINT, Description NVARCHAR(1000), CreatedAt DATETIME2(0)
    );

    BEGIN TRANSACTION;

    DELETE d
    OUTPUT DELETED.VetVisitDocumentID, DELETED.VetVisitID, DELETED.DocumentType, DELETED.DisplayName,
           DELETED.OriginalFileName, DELETED.StoredPath, DELETED.ContentType,
           DELETED.FileSizeBytes, DELETED.Description, DELETED.CreatedAt
    INTO @Deleted
    FROM dbo.VetVisitDocuments d
    INNER JOIN dbo.VetVisits v ON v.VetVisitID = d.VetVisitID
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
     AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
    WHERE d.VetVisitDocumentID = @DocumentID AND p.HouseholdID = @HouseholdID
      AND v.IsDeleted = 0;

    INSERT dbo.VetVisitHistory (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt, ChangedByUserID)
    SELECT VetVisitID, N'Document removed', NULL, NULL,
           LEFT(CONCAT(N'Document ''', DisplayName, N''' removed.'), 1000),
           SYSDATETIME(), @UserID
    FROM @Deleted;

    COMMIT TRANSACTION;

    SELECT VetVisitDocumentID, VetVisitID, DocumentType, DisplayName, OriginalFileName,
           StoredPath, ContentType, FileSizeBytes, Description, CreatedAt
    FROM @Deleted;
END;
GO

-- [NEW] GetVetVisitHistory  -  VetVisitService.GetHistory
CREATE OR ALTER PROCEDURE dbo.GetVetVisitHistory
    @UserID INT, @HouseholdID INT, @VetVisitID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT h.VetVisitHistoryID, h.VetVisitID, h.ChangeType, h.OldStatus,
           h.NewStatus, h.Details, h.ChangedAt, h.ChangedByUserID,
           changedBy.name AS ChangedByName
    FROM dbo.VetVisitHistory h
    INNER JOIN dbo.VetVisits v ON v.VetVisitID = h.VetVisitID
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    LEFT JOIN dbo.Users changedBy ON changedBy.userID = h.ChangedByUserID
    WHERE h.VetVisitID = @VetVisitID AND p.HouseholdID = @HouseholdID
    ORDER BY h.ChangedAt DESC, h.VetVisitHistoryID DESC;
END;
GO

-- [NEW] GetDashboardVetVisits  -  VetVisitService.GetDashboardVisits
CREATE OR ALTER PROCEDURE dbo.GetDashboardVetVisits
    @UserID INT, @HouseholdID INT, @StartDate DATE, @EndDate DATE
AS
BEGIN
    SET NOCOUNT ON;

    SELECT v.VetVisitID, v.PetID, v.VisitDate, v.VisitTime, v.IsAllDay,
           v.VisitReason, v.ClinicName
    FROM dbo.VetVisits v
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE p.HouseholdID = @HouseholdID AND v.IsDeleted = 0
      AND v.Status IN (N'Scheduled', N'Confirmed', N'Rescheduled')
      AND v.VisitDate >= @StartDate AND v.VisitDate < @EndDate
    ORDER BY v.VisitDate, CASE WHEN v.VisitTime IS NULL THEN 1 ELSE 0 END, v.VisitTime;
END;
GO

-- [NEW] GetVetVisitDocumentPathsByPet  -  VetVisitService.GetDocumentPathsByPet
-- Used to delete the files of a pet's vet documents when the pet is deleted.
CREATE OR ALTER PROCEDURE dbo.GetVetVisitDocumentPathsByPet
    @UserID INT, @HouseholdID INT, @PetID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT d.StoredPath
    FROM dbo.VetVisitDocuments d
    INNER JOIN dbo.VetVisits v ON v.VetVisitID = d.VetVisitID
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    WHERE v.PetID = @PetID AND p.HouseholdID = @HouseholdID;
END;
GO


/* =============================================================================
   USERS / AUTH
   ============================================================================= */

-- [NEW] AuthenticateUser  -  Login.OnPost
-- Mirrors the existing inline credential check (plain-text password comparison - see
-- HOUSEHOLD-ACCOUNTS.md "Existing authentication limitation"; hashing is out of scope here).
CREATE OR ALTER PROCEDURE dbo.AuthenticateUser
    @userName VARCHAR(100), @pass VARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT userID, name, DarkMode
    FROM dbo.Users
    WHERE userName = @userName AND pass = @pass;
END;
GO

-- [NEW] GetUserProfile  -  Profile.LoadProfile
CREATE OR ALTER PROCEDURE dbo.GetUserProfile
    @UserID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT name, userName, email, phone
    FROM dbo.Users
    WHERE userID = @UserID;
END;
GO

-- [NEW] VerifyUserPassword  -  Profile.VerifyCurrentPassword     Returns Matches (0 or 1).
CREATE OR ALTER PROCEDURE dbo.VerifyUserPassword
    @UserID INT, @Password VARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT COUNT(1) AS Matches
    FROM dbo.Users
    WHERE userID = @UserID AND pass = @Password;
END;
GO

-- [NEW] UpdateUserDarkMode  -  Theme.OnPost     Returns Succeeded.
CREATE OR ALTER PROCEDURE dbo.UpdateUserDarkMode
    @UserID INT, @DarkMode BIT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.Users SET DarkMode = @DarkMode WHERE userID = @UserID;

    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [UPDATED] AddUser  -  Signup.OnPost
-- BUG FIX. Signup runs ExecuteNonQuery() and treats "rows > 0" as success. The deployed procedure has
-- SET NOCOUNT ON, which makes ExecuteNonQuery() return -1, so every successful signup was reported
-- as "Failed to create account" (and a retry created a duplicate user - Users has no unique
-- constraint on userName/email). SET NOCOUNT ON is therefore deliberately NOT used here: the INSERT
-- reports 1 row, while the SELECT still returns the new userID for ExecuteScalar() callers.
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
    INSERT INTO dbo.Users (name, userName, email, phone, pass, createdAt, isAdmin)
    VALUES (@name, @userName, @email, @phone, @pass, @createdAt, @isAdmin);

    SELECT CONVERT(INT, SCOPE_IDENTITY()) AS userID;
END;
GO

-- [UPDATED] UpdateUser  -  Profile.OnPost
-- The deployed parameters were narrower than the columns (name/userName/email varchar(50) vs
-- varchar(100); phone varchar(20) vs varchar(15)), so a 51-100 character name or email was silently
-- truncated when saving a profile. Widths now match dbo.Users. @pass (optional) still updates the
-- password when supplied.
CREATE OR ALTER PROCEDURE dbo.UpdateUser
    @userID     INT,
    @name       VARCHAR(100),
    @userName   VARCHAR(100),
    @email      VARCHAR(100),
    @phone      VARCHAR(15),
    @pass       VARCHAR(100) = NULL
AS
BEGIN
    IF @pass IS NOT NULL
    BEGIN
        UPDATE dbo.Users
        SET name = @name, userName = @userName, email = @email,
            phone = @phone, pass = @pass
        WHERE userID = @userID;
    END
    ELSE
    BEGIN
        UPDATE dbo.Users
        SET name = @name, userName = @userName, email = @email, phone = @phone
        WHERE userID = @userID;
    END
END;
GO


/* ---------- post-check: every procedure above must exist and carry QUOTED_IDENTIFIER ON ---------- */
SET NOEXEC OFF;
GO

IF SESSION_CONTEXT(N'AlignStoredProcedures_Aborted') IS NOT NULL
BEGIN
    EXEC sys.sp_set_session_context @key = N'AlignStoredProcedures_Aborted', @value = NULL;
    PRINT N'Aborted: prerequisite schema objects are missing. Nothing was changed.';
    RETURN;
END;

DECLARE @Expected TABLE (Name sysname PRIMARY KEY);
INSERT @Expected (Name) VALUES
 (N'GetPetsByHouseholdID'), (N'GetTasksByPetID'), (N'GetTasksByPetID_Recent'), (N'GetTasksByPetIDSince'),
 (N'GetLatestActivityTasksByPetID'), (N'AddPet'), (N'UpdatePet'), (N'VerifyPetOwnership'),
 (N'VerifyTaskOwnership'), (N'VerifyMedicationOwnership'), (N'GetHouseholdContext'),
 (N'GetHouseholdIDByPublicID'), (N'GetHouseholdMemberRole'), (N'CreateDefaultHouseholdForUser'),
 (N'GetHouseholdsForUser'), (N'GetHouseholdMembers'), (N'GetPendingHouseholdInvitations'),
 (N'UpdateHouseholdMemberRole'), (N'RemoveHouseholdMember'), (N'CreateHouseholdInvitation'),
 (N'ResendHouseholdInvitation'), (N'RevokeHouseholdInvitation'), (N'GetInvitationByTokenHash'),
 (N'AcceptHouseholdInvitation'), (N'GetMedicationsByPetID'), (N'CheckMedicationAccess'),
 (N'FindMedicationScheduleForDose'), (N'UpdateMedicationScheduleDose'), (N'UnconfirmMedicationSchedule'),
 (N'GetHealthTimeline'), (N'AddVetVisit'), (N'UpdateVetVisit'), (N'ChangeVetVisitStatus'),
 (N'CompleteVetVisit'), (N'GetVetVisits'), (N'GetVetVisitByID'), (N'RefreshVetVisitReminderStatuses'),
 (N'DismissVetVisitReminder'), (N'GetVetVisitDocuments'), (N'GetVetVisitDocumentByID'),
 (N'AddVetVisitDocument'), (N'UpdateVetVisitDocument'), (N'DeleteVetVisitDocument'),
 (N'GetVetVisitHistory'), (N'GetDashboardVetVisits'), (N'GetVetVisitDocumentPathsByPet'),
 (N'AuthenticateUser'), (N'GetUserProfile'), (N'VerifyUserPassword'), (N'UpdateUserDarkMode'),
 (N'AddUser'), (N'UpdateUser');

DECLARE @Bad nvarchar(max) = N'';
SELECT @Bad = @Bad + N' ' + e.Name + CASE WHEN p.object_id IS NULL THEN N'(missing)' ELSE N'(QUOTED_IDENTIFIER OFF)' END
FROM @Expected e
LEFT JOIN sys.procedures p ON p.name = e.Name AND p.schema_id = SCHEMA_ID(N'dbo')
LEFT JOIN sys.sql_modules m ON m.object_id = p.object_id
WHERE p.object_id IS NULL OR m.uses_quoted_identifier = 0;

IF LEN(@Bad) > 0
    RAISERROR(N'2026-09-20_AlignStoredProcedures did not complete cleanly:%s', 16, 1, @Bad);
ELSE
    SELECT COUNT(*) AS ProceduresVerified,
           N'All procedures present with QUOTED_IDENTIFIER ON' AS Result
    FROM @Expected;
GO
