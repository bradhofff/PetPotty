/*
    Read-only verification for the 2026-09-17 household/health MVP migrations.
    Run after both 2026-09-17_AddHouseholdsAndHealthEvents.sql and
    2026-09-17_AddHouseholdHealthStoredProcedures.sql. Safe to run any time —
    it only SELECTs.
*/
SET NOCOUNT ON;

DECLARE @Failures TABLE (CheckName NVARCHAR(200) NOT NULL);

-- Schema present
IF OBJECT_ID(N'dbo.Households', N'U') IS NULL INSERT @Failures VALUES (N'Households table is missing');
IF OBJECT_ID(N'dbo.HouseholdMembers', N'U') IS NULL INSERT @Failures VALUES (N'HouseholdMembers table is missing');
IF OBJECT_ID(N'dbo.HouseholdInvitations', N'U') IS NULL INSERT @Failures VALUES (N'HouseholdInvitations table is missing');
IF OBJECT_ID(N'dbo.HealthEvents', N'U') IS NULL INSERT @Failures VALUES (N'HealthEvents table is missing');
IF COL_LENGTH(N'dbo.Pets', N'HouseholdID') IS NULL INSERT @Failures VALUES (N'Pets.HouseholdID is missing');
IF COL_LENGTH(N'dbo.Tasks', N'RecordedByUserID') IS NULL INSERT @Failures VALUES (N'Tasks.RecordedByUserID is missing');
IF COL_LENGTH(N'dbo.VetVisits', N'CreatedByUserID') IS NULL INSERT @Failures VALUES (N'VetVisits.CreatedByUserID is missing');
IF COL_LENGTH(N'dbo.MedicationSchedule', N'DoseStatus') IS NULL INSERT @Failures VALUES (N'MedicationSchedule.DoseStatus is missing');

-- Every existing pet has a household (Part 4 requirement #6: "verify every existing pet is accessible")
IF EXISTS (SELECT 1 FROM dbo.Pets WHERE HouseholdID IS NULL)
    INSERT @Failures VALUES (N'One or more Pets rows still have a NULL HouseholdID — investigate before relying on household scoping; see the orphan list this script prints below');

-- Pets.HouseholdID should be NOT NULL once every row is backfilled (skip if the prior check already failed)
IF NOT EXISTS (SELECT 1 FROM dbo.Pets WHERE HouseholdID IS NULL)
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Pets') AND name = N'HouseholdID' AND is_nullable = 1)
    INSERT @Failures VALUES (N'Pets.HouseholdID is backfilled but was not tightened to NOT NULL');

-- Every existing user has an Owner membership somewhere (Part 4 requirement #2)
IF EXISTS
(
    SELECT 1 FROM dbo.Users u
    WHERE NOT EXISTS (SELECT 1 FROM dbo.HouseholdMembers hm WHERE hm.UserID = u.userID AND hm.Status = N'Active')
)
    INSERT @Failures VALUES (N'One or more Users have no active household membership at all');

-- No duplicate active membership (composite PK already enforces this structurally, but confirm no Removed+Active pairs slipped through logic bugs)
IF EXISTS
(
    SELECT HouseholdID, UserID, COUNT(*) FROM dbo.HouseholdMembers
    WHERE Status = N'Active'
    GROUP BY HouseholdID, UserID
    HAVING COUNT(*) > 1
)
    INSERT @Failures VALUES (N'Duplicate active HouseholdMembers rows exist for the same household+user (should be impossible under the composite PK)');

-- Exactly one Owner per household (MVP invariant; not DB-enforced, checked here)
IF EXISTS
(
    SELECT HouseholdID, COUNT(*) FROM dbo.HouseholdMembers
    WHERE Status = N'Active' AND Role = N'Owner'
    GROUP BY HouseholdID
    HAVING COUNT(*) <> 1
)
    INSERT @Failures VALUES (N'One or more households do not have exactly one active Owner');

-- Invalid role values (defense-in-depth beyond the CHECK constraint)
IF EXISTS (SELECT 1 FROM dbo.HouseholdMembers WHERE Role NOT IN (N'Owner', N'Member', N'Caregiver'))
    INSERT @Failures VALUES (N'HouseholdMembers contains a role outside Owner/Member/Caregiver');
IF EXISTS (SELECT 1 FROM dbo.HouseholdInvitations WHERE Role NOT IN (N'Member', N'Caregiver'))
    INSERT @Failures VALUES (N'HouseholdInvitations contains a role outside Member/Caregiver (Owner must never be invitable)');

-- Duplicate active (pending) invitations for the same household+email (the filtered unique index should already prevent this)
IF EXISTS
(
    SELECT HouseholdID, NormalizedEmail, COUNT(*) FROM dbo.HouseholdInvitations
    WHERE AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL
    GROUP BY HouseholdID, NormalizedEmail
    HAVING COUNT(*) > 1
)
    INSERT @Failures VALUES (N'Duplicate pending HouseholdInvitations exist for the same household+email');

-- An invitation should never be both accepted and revoked (CHECK constraint already prevents; confirm anyway)
IF EXISTS (SELECT 1 FROM dbo.HouseholdInvitations WHERE AcceptedAtUtc IS NOT NULL AND RevokedAtUtc IS NOT NULL)
    INSERT @Failures VALUES (N'An invitation is both accepted and revoked');

-- Orphaned foreign keys: every Pets.HouseholdID points at a real household
IF EXISTS (SELECT 1 FROM dbo.Pets p WHERE p.HouseholdID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.Households h WHERE h.HouseholdID = p.HouseholdID))
    INSERT @Failures VALUES (N'A Pets row references a HouseholdID that does not exist in Households');

-- HealthEvents always reference a valid pet (FK already enforces; confirm no orphans some other way could produce)
IF EXISTS (SELECT 1 FROM dbo.HealthEvents h WHERE NOT EXISTS (SELECT 1 FROM dbo.Pets p WHERE p.petID = h.PetID))
    INSERT @Failures VALUES (N'A HealthEvents row references a PetID that does not exist');

-- Cross-household consistency: a pet's HouseholdID must not disagree with its owning user's Owner household
-- (informational cross-check on the backfill invariant, not a hard product rule going forward once sharing/reassignment exists)
IF EXISTS
(
    SELECT 1 FROM dbo.Pets p
    INNER JOIN dbo.HouseholdMembers hm ON hm.UserID = p.userID AND hm.Role = N'Owner' AND hm.Status = N'Active'
    WHERE p.HouseholdID <> hm.HouseholdID
)
    INSERT @Failures VALUES (N'A pet''s HouseholdID does not match its legacy owner''s Owner-household — review before trusting the backfill for that pet')

-- Required stored procedures exist
DECLARE @RequiredProcs TABLE (ProcName SYSNAME);
INSERT @RequiredProcs (ProcName) VALUES
    (N'CreateDefaultHouseholdForUser'), (N'GetHouseholdsForUser'), (N'GetHouseholdContext'), (N'RenameHousehold'),
    (N'GetHouseholdMembers'), (N'UpdateHouseholdMemberRole'), (N'RemoveHouseholdMember'),
    (N'CreateHouseholdInvitation'), (N'ResendHouseholdInvitation'), (N'RevokeHouseholdInvitation'),
    (N'GetPendingHouseholdInvitations'), (N'GetInvitationByTokenHash'), (N'AcceptHouseholdInvitation'),
    (N'AddHealthEvent'), (N'UpdateHealthEvent'), (N'DeleteHealthEvent'), (N'GetHealthEventByID'),
    (N'GetHealthEvents'), (N'GetHealthTimeline'),
    (N'GetPetsByHouseholdID'), (N'AddPet'), (N'UpdatePet'), (N'DeletePetByPetID'),
    (N'AddTaskByPetID'), (N'UpdateTaskByID'), (N'DeleteTaskByTaskID'), (N'GetTasksByPetID'), (N'GetTasksByPetID_Recent'),
    (N'GetMedicationsByPetID'), (N'AddMedication'), (N'UpdateMedication'), (N'DeleteMedicationByID'),
    (N'GetScheduledMedsByPetID_AllTime'), (N'GetScheduledMedsByPetID_Next2Months'), (N'GetScheduledMedsByPetID'),
    (N'ConfirmMedicationSchedule'), (N'UnconfirmMedicationSchedule'),
    (N'AddVetVisit'), (N'UpdateVetVisit'), (N'ChangeVetVisitStatus'), (N'CompleteVetVisit'), (N'DeleteVetVisit');

INSERT @Failures (CheckName)
SELECT N'Missing required stored procedure: ' + ProcName
FROM @RequiredProcs
WHERE OBJECT_ID(N'dbo.' + ProcName, N'P') IS NULL;

-- Every household-scoped procedure should now declare @HouseholdID
INSERT @Failures (CheckName)
SELECT N'Procedure is missing an @HouseholdID parameter: ' + ProcName
FROM @RequiredProcs
WHERE OBJECT_ID(N'dbo.' + ProcName, N'P') IS NOT NULL
  AND ProcName NOT IN (N'GetHouseholdsForUser', N'CreateDefaultHouseholdForUser', N'GetInvitationByTokenHash', N'AcceptHouseholdInvitation')
  AND NOT EXISTS
  (
      SELECT 1 FROM sys.parameters
      WHERE object_id = OBJECT_ID(N'dbo.' + ProcName) AND name = N'@HouseholdID'
  );

IF EXISTS (SELECT 1 FROM @Failures)
BEGIN
    SELECT N'FAIL' AS Result, CheckName FROM @Failures ORDER BY CheckName;

    PRINT 'Pets with a NULL HouseholdID (if any):';
    SELECT petID, userID, name FROM dbo.Pets WHERE HouseholdID IS NULL;

    THROW 51000, 'One or more household/health MVP checks failed. See the FAIL rows above.', 1;
END;

SELECT N'PASS' AS Result,
       N'Household, invitation, and health-event schema and stored procedures are present and internally consistent.' AS Details;

-- Informational summary (always printed, pass or fail short-circuits above on fail)
SELECT
    (SELECT COUNT(*) FROM dbo.Households) AS TotalHouseholds,
    (SELECT COUNT(*) FROM dbo.HouseholdMembers WHERE Status = N'Active') AS ActiveMemberships,
    (SELECT COUNT(*) FROM dbo.Pets) AS TotalPets,
    (SELECT COUNT(*) FROM dbo.Pets WHERE HouseholdID IS NOT NULL) AS PetsWithHousehold,
    (SELECT COUNT(*) FROM dbo.HealthEvents WHERE IsDeleted = 0) AS ActiveHealthEvents,
    (SELECT COUNT(*) FROM dbo.HouseholdInvitations WHERE AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL) AS PendingInvitations;
