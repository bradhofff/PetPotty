# Stored-procedure audit

Audit date: 2026-09-20. Compared every SQL call in the C# code with the stored
procedures deployed in `PetPottyDb_Test` (SQL Server 2022, 48 procedures) and with
the scripts in `Migrationsss/`.

## Result

| | |
| --- | --- |
| SQL call sites in the C# code (13 files) | **78** |
| already call a stored procedure | 15 |
| run inline SQL | **63** |
| procedures in the live database | 48 |
| live procedures with **no** definition in `Migrationsss/` | 31 |
| live procedures whose repo definition differs from the live one | 15 |
| live procedures identical to the repo | 2 |

## Why `AddPet` runs inline SQL

`PetService.AddPet` called `dbo.AddPet` until commit `60c60c6` (2026-09-18, the household
work). That commit needed `HouseholdID` on the new row, replaced the procedure call with an
`INSERT dbo.Pets ... OUTPUT INSERTED.petID`, and left the deployed `dbo.AddPet` procedure
unused. The live `dbo.AddPet` is already household-aware, but it differs from what the app
needs:

* it allows **Owner and Member**; the app's `ManagePets` permission is **Owner only**
  (`Household.cs`, and `HOUSEHOLD-ACCOUNTS.md` "Role behavior");
* it takes `NVARCHAR(255)` text for `varchar(100/50/20)` columns, so a long name raises
  error 8152 instead of being trimmed like the inline `INSERT` does;
* it requires `@createdAt`, and stores `''` for blank text and `0` for a blank age.

The same pattern - a procedure that was correct for an earlier design and was then bypassed
by inline SQL - repeats across the household, health and vet-visit code.

## Findings (most important first)

1. **New accounts cannot sign up cleanly, and cannot finish a first login.**
   * *Signup:* the live `AddUser` has `SET NOCOUNT ON`, so `ExecuteNonQuery()` returns -1 and
     `Signup.cshtml.cs` (`rows > 0`) shows "Failed to create account" although the row was
     inserted. A retry creates a duplicate user (`Users` has no unique constraint on
     `userName`/`email`). Reproduced in the running app before the script and fixed after it.
     **Fixed by `AlignStoredProcedures` (`AddUser`).**
   * *First login:* the inline SQL in `HouseholdService.EnsurePersonalHousehold` computes
     `@Name` in a `DECLARE` that refers to alias `u` from a `FROM` that appears only in the next
     statement, so SQL Server raises `The multi-part identifier "u.name" could not be bound`
     for every user that has no household yet - i.e. every new signup. This is a bug in the C#
     inline SQL, not in any procedure. **Fixed once `EnsurePersonalHousehold` calls
     `CreateDefaultHouseholdForUser`** (updated in the script and tested); until then the
     C# still has the bug.
2. **Role drift.** Live `AddPet` and `UpdatePet` allow the Member role; the app and the
   documentation say pet management is Owner only. The C# blocks Members before calling, so
   today the difference is invisible - but any other caller of the procedures would bypass it.
3. **Vet-visit history does not record who acted.** Live `AddVetVisit`, `UpdateVetVisit`,
   `ChangeVetVisitStatus` and `CompleteVetVisit` write history rows without `ChangedByUserID`
   (the repo copies do write it). Fails `HOUSEHOLD-ACCOUNTS.md` smoke check 17.
4. **`UpdateUser` truncates.** Its parameters are `varchar(50)` for name/userName/email but
   the columns are `varchar(100)`; a 51-100 character name or email is silently cut when
   the profile is saved.
5. **Several live household/health procedures cannot serve the app as they are** (missing
   `PublicID`, wrong argument list, e.g. `GetHealthTimeline` rejects the parameters the app
   sends: "too many arguments"; `GetTasksByPetID_Recent` returns 36 hours where the app
   shows 7 days; `UnconfirmMedicationSchedule` returns nothing so a caller cannot tell success
   from a no-op).
6. **`Migrationsss/` does not describe the database.** 31 live procedures are in no script
   (including six the app calls today: `UpdatePet`, `UpdateTaskByID`, `DeleteTaskByTaskID`,
   `DeleteMedicationByID`, `AddUser`, `UpdateUser`), and 15 more differ from their repo copy.
   Most repo copies are older, pre-household versions (e.g. `AddMedication(@petID, ...)` without
   `@UserID/@HouseholdID`, so the current C# call would fail with "too many arguments"); the
   vet-visit ones differ the other way (the repo copy records `ChangedByUserID`, the live one does
   not). The `AddPet`/`GetPetsByUserID`/`UpdatePetProfileImagePath` repo copies also target
   `dbo.Pet`, a table that no longer exists. A database built only from the repo would not work
   with the current app.
7. **`sqlcmd` builds procedures with `QUOTED_IDENTIFIER OFF` by default.** `VetVisits`,
   `HealthEvents` and `HouseholdInvitations` have filtered indexes, so DML from such a
   procedure fails with error 1934. (`DeletePetByPetID` was hit by this - see the workaround
   comment in `PetService.DeletePet`.) The new scripts set it explicitly.
8. **Not fixed (needs a table change):** `CompleteVetVisit.@Diagnosis` and the page allow
   2000 characters but `VetVisits.Diagnosis` is `nvarchar(1000)`, so a 1001-2000 character
   diagnosis raises error 8152. Fix: `ALTER TABLE dbo.VetVisits ALTER COLUMN Diagnosis nvarchar(2000) NULL;`
9. **Informational.** `Users` has no unique constraint on `userName`/`email`, so the
   `UQ_Users_*` handling in `Signup`/`Profile` can never trigger. Passwords are compared in
   plain text (already noted in `HOUSEHOLD-ACCOUNTS.md`).
10. **Dead procedures in the database:** `GetPetByID`, `GetPetsByUserID` (returns pets by the
    legacy owner column with no household check), `GetScheduledMedsByPetID` (wrapper).
    `GetScheduledMedsByPetID_Month` and `_Next30Days` are in the repo but not in the database.
    Nothing was dropped.

## The scripts

All three are in `Migrationsss/`, idempotent (`CREATE OR ALTER`), pure ASCII, set
`QUOTED_IDENTIFIER ON` themselves, check their prerequisites first and change no tables or data.

| Script | What it does |
| --- | --- |
| `2026-09-20_AlignStoredProcedures.sql` | **26 new + 26 updated procedures** - one for every inline SQL statement, plus the live procedures that differ from what the app needs. The header lists each one and the C# method it serves. |
| `2026-09-20_SnapshotCurrentProcedures.sql` | Exact copy of 17 live procedures the app uses that the script above does not change, so the repo can rebuild an environment. A no-op on the current test database. |
| `2026-09-20_AlignStoredProcedures_Rollback.sql` | Restores the previous definition of the 26 updated procedures and drops the 26 new ones. Those previous definitions were not in source control. |

Only 7 of the 26 updated procedures are called by the app today (`UpdatePet`, `AddVetVisit`,
`UpdateVetVisit`, `ChangeVetVisitStatus`, `CompleteVetVisit`, `AddUser`, `UpdateUser`); their
parameter lists are unchanged (`UpdateUser` only widens the types), so the current C# keeps
working. The other 19 are not called yet.

## Every call site

Legend - **A** = `AlignStoredProcedures`, **B** = `SnapshotCurrentProcedures`;
*new* / *updated* / *live OK*. "Live OK" means the procedure already exists and, on reading its
definition, does what the inline SQL does, so it only needs to be captured in source control (B).
Only the ones marked "verified identical" were also run against the inline SQL; the write
procedures marked live OK (`DeletePetByPetID`, `AddTaskByPetID`, the health-event writes,
`RenameHousehold`) were compared by inspection, not tested - test them when you switch those calls.

### PetService.cs
| Call site | Today | Procedure | |
| --- | --- | --- | --- |
| GetPetsByUser | inline | `GetPetsByHouseholdID` | A updated (tie-break order) |
| GetTasksByPetID (all time) | inline | `GetTasksByPetID` | A updated (tie-break order) |
| GetTasksByPetID (recent) | inline | `GetTasksByPetID_Recent` | A updated (36 h -> 7 days) |
| GetTasksByPetIDSince | inline | `GetTasksByPetIDSince` | A new (2 result sets) |
| GetLatestActivityTasksByPetID | inline | `GetLatestActivityTasksByPetID` | A new |
| **AddPet** | **inline** | `AddPet` | A updated |
| EditPet | proc | `UpdatePet` | A updated (Owner only) |
| UpdatePetProfileImagePath | proc | `UpdatePetProfileImagePath` | B live OK |
| DeletePet | inline | `DeletePetByPetID` | B live OK (QI ON; Owner only) |
| AddTask | inline | `AddTaskByPetID` | B live OK |
| UpdateTask / DeleteTask | proc | `UpdateTaskByID` / `DeleteTaskByTaskID` | B live OK |

### OwnedRecordCommand.cs
| Call site | Today | Procedure | |
| --- | --- | --- | --- |
| Pet / Task / Medication ownership check | inline (3 constants) | `VerifyPetOwnership`, `VerifyTaskOwnership`, `VerifyMedicationOwnership` | A new |

### MedicationService.cs
| Call site | Today | Procedure | |
| --- | --- | --- | --- |
| OwnsMedication | inline | `CheckMedicationAccess` | A new |
| GetMedicationsByPetID | inline | `GetMedicationsByPetID` | A updated (order) |
| GetScheduleByPetID (all time / 2 months) | inline | `GetScheduledMedsByPetID_AllTime` / `_Next2Months` | B live OK (verified identical) |
| AddMedication / UpdateMedication / DeleteMedication | proc | `AddMedication` / `UpdateMedication` / `DeleteMedicationByID` | B live OK |
| RecordDose: ownership | inline | `VerifyMedicationOwnership` | A new |
| RecordDose: confirm | proc | `ConfirmMedicationSchedule` | B live OK |
| RecordDose / Unconfirm: find schedule | inline | `FindMedicationScheduleForDose` | A new |
| RecordDose: final update | inline | `UpdateMedicationScheduleDose` | A new |
| UnconfirmSchedule (ownership + find + update) | inline x3 | `UnconfirmMedicationSchedule` (one call) | A updated |

### HealthService.cs
| Call site | Today | Procedure | |
| --- | --- | --- | --- |
| AddHealthEvent / UpdateHealthEvent / DeleteHealthEvent | inline | `AddHealthEvent` / `UpdateHealthEvent` / `DeleteHealthEvent` | B live OK |
| GetHealthEventByID / GetHealthEvents | inline | `GetHealthEventByID` / `GetHealthEvents` | B live OK (verified identical) |
| GetTimeline | inline | `GetHealthTimeline` | A updated |

### VetVisitService.cs
| Call site | Today | Procedure | |
| --- | --- | --- | --- |
| GetVisits (+ RefreshReminderStatuses) | inline x2 | `GetVetVisits`, `RefreshVetVisitReminderStatuses` | A new |
| GetVisit | inline | `GetVetVisitByID` | A new |
| AddVisit | proc | `AddVetVisit` | A updated |
| AddVisit: follow-up `UPDATE ... CreatedByUserID` | inline | none - `AddVetVisit` already sets it; delete this statement | - |
| UpdateVisit / ChangeStatus / CompleteVisit | proc | `UpdateVetVisit` / `ChangeVetVisitStatus` / `CompleteVetVisit` | A updated |
| DeleteVisit | proc | `DeleteVetVisit` | already in repo, identical |
| DismissReminder | inline | `DismissVetVisitReminder` | A new |
| GetDocuments / GetDocument | inline | `GetVetVisitDocuments` / `GetVetVisitDocumentByID` | A new |
| AddDocument / UpdateDocument | inline | `AddVetVisitDocument` / `UpdateVetVisitDocument` | A new |
| DeleteDocument (select + delete + history) | inline x3 | `DeleteVetVisitDocument` (one call) | A new |
| GetHistory | inline | `GetVetVisitHistory` | A new |
| GetDashboardVisits | inline | `GetDashboardVetVisits` | A new |
| GetDocumentPathsByPet | inline | `GetVetVisitDocumentPathsByPet` | A new |

### Household services
| Call site | Today | Procedure | |
| --- | --- | --- | --- |
| HouseholdContextService.GetActiveHousehold | inline | `GetHouseholdContext` | A updated |
| HouseholdContextService.TrySetActiveHousehold(Guid) | inline | `GetHouseholdIDByPublicID` | A new |
| HouseholdContextService.TrySetActiveHousehold(int) | inline | `GetHouseholdContext` (a row = member) | A updated |
| HouseholdAuthorizationService.GetRole | inline | `GetHouseholdMemberRole` | A new |
| HouseholdService.EnsurePersonalHousehold | inline x3 | `CreateDefaultHouseholdForUser` (one call; **fixes finding 1**) | A updated |
| HouseholdService.GetHouseholds / GetMembers / GetPendingInvitations | inline | `GetHouseholdsForUser` / `GetHouseholdMembers` / `GetPendingHouseholdInvitations` | A updated |
| HouseholdService.RenameHousehold | inline | `RenameHousehold` | B live OK |
| HouseholdService.ChangeMemberRole / RemoveMember | inline | `UpdateHouseholdMemberRole` / `RemoveHouseholdMember` (by member PublicID) | A updated |
| HouseholdInvitationService.Validate | inline | `GetInvitationByTokenHash` | A updated |
| HouseholdInvitationService.CreateAsync | inline x3 | `CreateHouseholdInvitation` (one call) | A updated |
| HouseholdInvitationService.ResendAsync | inline x2 | `ResendHouseholdInvitation` (one call) | A updated |
| HouseholdInvitationService.Revoke | inline | `RevokeHouseholdInvitation` | A updated |
| HouseholdInvitationService.Accept | inline x3 | `AcceptHouseholdInvitation` (one call) | A updated |

### Pages
| Call site | Today | Procedure | |
| --- | --- | --- | --- |
| Login | inline | `AuthenticateUser` | A new |
| Signup | proc | `AddUser` | A updated (**fixes finding 1**) |
| Profile: load / verify password | inline | `GetUserProfile` / `VerifyUserPassword` | A new |
| Profile: change password | inline | `UpdateUser` with `@pass` (parameter already exists) | - |
| Profile: save | proc | `UpdateUser` | A updated |
| Theme (dark mode) | inline | `UpdateUserDarkMode` | A new |

## Switching the C# to the procedures (not done yet)

Parameter names and result-set column names/order match the inline SQL, so most changes are
`new SqlCommand("Name", conn) { CommandType = CommandType.StoredProcedure }`. Things that differ:

* Mutations return a `Succeeded` bit (or an ID, `0` = denied) instead of a row count: use
  `Convert.ToBoolean(cmd.ExecuteScalar())` where the code has `ExecuteNonQuery() == 1`
  (RenameHousehold, ChangeMemberRole, RemoveMember, Revoke, Update/DeleteHealthEvent,
  DismissReminder, Update/DeleteDocument, Theme). The vet-visit code already uses this idiom.
* Members and invitations are addressed by `PublicID` (GUID); the invitation procedures return
  the values needed to send the email (`PublicID`, household and inviter names, `ResultCode`).
* `AcceptHouseholdInvitation` returns `ResultCode`: `Accepted`, `AlreadyAccepted` (success),
  `AlreadyUsed`, `NotFound`, `Revoked`, `Expired`, `EmailMismatch` - map each to the existing message.
* `GetTasksByPetIDSince` returns two result sets (`reader.NextResult()` for `hasOlder`).
* `GetHealthTimeline` takes `@UtcOffsetMinutes`, `@NowUtc`, `@MissedAfterMinutes`.
* Add the household guard parameters to the two medication-dose calls
  (`FindMedicationScheduleForDose`, `UpdateMedicationScheduleDose` take `@UserID`, `@HouseholdID`).

## How this was verified

* Restored the live schema and all 48 live procedures into a **local scratch database** (LocalDB),
  ran the scripts with plain `sqlcmd` (no `-I`, the way `DEPLOYMENT.md` deploys), and checked that
  every procedure ends up with `QUOTED_IDENTIFIER ON`.
* **Parity:** ran the *inline SQL extracted from the C# source* and each new/updated read
  procedure with the same inputs for six users across both households (members, a Caregiver, a
  non-member, a removed member) and compared every row: 467 checks, 723 rows.
* **Behavior:** 107 checks of the write paths - role enforcement, attribution, the invitation
  state machine, nesting inside a caller's transaction, `AddUser`/`UpdateUser`.
* Snapshot leaves all 48 definitions unchanged; rollback returns all 48 to the original text;
  the scripts re-run in any order.
* Ran the real app against the scratch database: Signup fails before the script and succeeds after it;
  add pet and edit pet work with the updated procedures.
* Nothing was executed against `PetPottyDb_Test`; it was only read (catalog views).
