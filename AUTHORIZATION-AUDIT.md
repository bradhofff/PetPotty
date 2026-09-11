# PetPotty authorization audit — 2026-09-06

**Result:** all 60 final cross-account cases passed, along with 27 owner-operation controls and 8 supplementary checks. Every authenticated attempt to operate on another user's record returned **404**, with no foreign record disclosure or mutation. The supplementary JSON request also returned 404. Anonymous document/photo requests returned 401; an anonymous dashboard request redirected to Login. Build and whitespace checks passed.

The application remains running at http://127.0.0.1:5187. This was a local ASP.NET Core application using the configured **PetPottyDb_Test** SQL Server database, not a locally hosted database. No database schema or stored procedures were changed. Existing uncommitted work was preserved.

## Method and fixtures

The PowerShell HTTP harness creates fresh User A and User B accounts through Signup, logs in using separate cookie containers, and creates a pet, task, medication, vet visit, reminder, photo, and document for each account through the application. It uses real antiforgery tokens and session cookies; denied POSTs therefore reach the authorization checks rather than merely failing CSRF validation. Redirect following is disabled so the actual response status is recorded.

The final pair was:

| Record | User A | User B |
|---|---|---|
| Account | `authaudit_7be3801fb512_A` (24) | `authaudit_4e952a8bb5b1_B` (25) |
| Pet | 24 | 25 |
| Task | 39 | 40 |
| Medication | 1023 | 1024 |
| Vet visit | 28 | 29 |
| Reminder | 28 | 29 |
| Document | 22 | 23 |

Hidden form values are tampered with through equivalent URL-encoded POST bodies, with additional multipart photo/document submissions. Query-string variants move bound values out of the body. Route tests replace path segments; the three main pages have no ID-bearing route templates, so those paths return 404. The photo route does contain an identifying filename. Mixed-owner tests use a foreign medication with A's selected pet, a foreign visit with A's pet, A's visit with B's pet, and mismatched reminder/pet IDs. B starts the final run with a confirmed dose, so unconfirmation denial is checked against a real confirmed record.

For every final attack, the harness compares full, deterministically ordered JSON snapshots of B's Pets, Tasks, Medications, MedicationSchedule, VetVisits, VetVisitDocuments, VetVisitReminders, and VetVisitHistory before and after the request. It also compares uploaded photo/document SHA-256 hashes and checks response bodies for B's unique fixture marker and private document content. Owner controls check actual database effects and confirm B remains unchanged. Early baseline snapshots used SQL's first JSON result chunk; final snapshots explicitly aggregate the full JSON value, and final checks also cover file hashes. No final request returned foreign file content.

## Confirmed vulnerabilities and fixes

| Finding | Responsible handler / component | Baseline evidence | Fix |
|---|---|---|---|
| Cross-account task creation | `HomeModel.OnPostQuickLog`, `OnPostAddTask` | 302; B's task rows changed | Pass the session user ID into the service; lock and query the target pet by both pet ID and user ID before insertion. Return 404 on denial. |
| Cross-account task edit/delete | `HomeModel.OnPostUpdateTask`, `OnPostDeleteTask` | 302; B's task changed/deleted | Join Tasks to Pets and require the session user ID within the mutation transaction. |
| Cross-account medication edit/delete | `MedicationsModel.OnPostEditMedication`, `OnPostDeleteMedication` | 302; B's medication changed/deleted | User-scoped Medications-to-Pets check inside the transaction; early ownership check before edit validation. |
| Cross-account dose confirmation/unconfirmation | `MedicationsModel.OnPostConfirmSchedule`, `OnPostUnconfirmSchedule` | 302; B's medication schedule changed | Authorize the medication through its owning pet before running the schedule procedure, in the same transaction. |
| Public uploaded pet photos | `Program.cs` static `/uploads` middleware (no Razor handler) | B's photo returned 200 to A | Remove the public uploads middleware. New `PetImageModel.OnGet` serves only a photo referenced by a pet belonging to the session user; 401/404 otherwise, private/no-store caching. |
| Inconsistent denial status | Pet edit/delete/reset; medication select/add/GET; vet visit handlers | 200 or 302 despite preserving B's data; `Forbid()` redirected to `/AccessDenied` | Return direct 404. Validate foreign pet/visit selections before rendering or processing forms, including visit reparenting. |

`Services/OwnedRecordCommand.cs` performs user-scoped authorization and the existing mutation in the same SQL transaction with `UPDLOCK, HOLDLOCK`, preventing an ownership/parent change between the check and the write. Pet mutation methods also require the session user ID. Vet services already scoped queries/procedure calls to user ID; their existing protections were verified, and their denial behavior was corrected.

An owner-operation control additionally found that the legacy `DeletePetByPetID` procedure was created with `QUOTED_IDENTIFIER OFF`, causing SQL error 1934 against filtered indexes. `PetService.DeletePet` now executes the same cascade using explicit user-scoped SQL, correct SET options, and the ownership transaction. Owner deletion and deletion of seeded pets with related records were re-tested successfully.

## Re-test history

| Phase | Outcome |
|---|---|
| Baseline | Eight mutation handlers vulnerable; nine mutation requests changed B's data because QuickLog was exercised twice. Only 3/32 requests met the strict failure-status requirement. |
| Home fixes | 11/32 passed; task and pet attacks returned 404, B unchanged. |
| Medication fixes | 18/32 passed; medication and dose attacks returned 404, B unchanged. |
| Vet denial fixes | 32/32 passed. |
| Photo expansion before photo fix | 32/33 passed; foreign photo returned 200. |
| Final fixes and expanded cases | 60/60 passed; all 404, all B snapshots/files unchanged. |
| Owner controls | 27/27 passed after correcting the pet deletion SQL failure. |
| Supplementary | 2/2 forged-user-ID attacks and 6/6 other checks passed. |

The intermediate Home re-test initially detected a remaining success redirect despite the database correctly denying the write; the handler result was corrected and the retained Home results are the successful re-test. The final owner test likewise was repeated after the deletion correction.

## Endpoint coverage

The table below groups final cross-account requests by endpoint. Each row returned 404 and preserved B's records/files. The JSON evidence retains each exact URL, submitted field, variant, status, redirect location, and comparison result. `form` means tampered form/hidden values; `query` means identifiers supplied in the query string.

| Endpoint | Variants | Requests |
|---|---|---|
| `GET /Home/{id}` | route | 1 |
| `GET /Medications` | query | 1 |
| `GET /Medications/{id}` | route | 1 |
| `GET /uploads/pets/{fileName}` | photo path | 1 |
| `GET /VetVisits` | query | 1 |
| `GET /VetVisits?handler=PreviewDocument` | query | 1 |
| `GET /VetVisits/{id}` | route | 1 |
| `POST /Home?handler=AddTask` | form, query | 2 |
| `POST /Home?handler=DeletePet` | form, query | 2 |
| `POST /Home?handler=DeleteTask` | body task ID, query | 2 |
| `POST /Home?handler=EditPet` | form, multipart, query | 3 |
| `POST /Home?handler=QuickLog` | form, query | 3 |
| `POST /Home?handler=ResetPetImage` | form, query | 2 |
| `POST /Home?handler=UpdateTask` | form, query | 2 |
| `POST /Medications?handler=AddMedication` | form, query | 2 |
| `POST /Medications?handler=ConfirmSchedule` | form, query | 2 |
| `POST /Medications?handler=DeleteMedication` | form, query | 2 |
| `POST /Medications?handler=EditMedication` | form, invalid form foreign ID, query | 3 |
| `POST /Medications?handler=SelectPet` | form, query | 2 |
| `POST /Medications?handler=UnconfirmSchedule` | body medication ID, query | 2 |
| `POST /VetVisits` | form, query | 2 |
| `POST /VetVisits?handler=AddVisit` | form, query | 2 |
| `POST /VetVisits?handler=ChangeStatus` | body visit ID, query | 2 |
| `POST /VetVisits?handler=CompleteVisit` | form, query | 2 |
| `POST /VetVisits?handler=DeleteDocument` | body document ID, query | 2 |
| `POST /VetVisits?handler=DeleteVisit` | form, query | 2 |
| `POST /VetVisits?handler=DismissReminder` | form, own reminder foreign pet, query | 3 |
| `POST /VetVisits?handler=DownloadDocument` | form, query | 2 |
| `POST /VetVisits?handler=EditVisit` | foreign visit own pet, form, query, reparent | 4 |
| `POST /VetVisits?handler=UpdateDocument` | form, query | 2 |
| `POST /VetVisits?handler=UploadDocument` | multipart | 1 |

### Setup and owner endpoints

| Endpoint | Observed result |
|---|---|
| `GET /Signup` | 200; antiforgery form |
| `GET /Login` | 200; antiforgery form |
| `POST /Signup` | 302; login or created record verified |
| `POST /Login` | 302; login or created record verified |
| `POST /Home?handler=AddPet` | 302; login or created record verified |
| `POST /Home?handler=AddTask` | 302; login or created record verified |
| `POST /Medications?handler=AddMedication` | 302; login or created record verified |
| `POST /VetVisits?handler=AddVisit` | 302; login or created record verified |
| `POST /VetVisits?handler=UploadDocument` | 302; login or created record verified |
| `GET /Home` | 200; owner control passed |
| `GET /uploads/pets/{fileName}` | 200; owner control passed |
| `GET /Medications` | 200; owner control passed |
| `POST /Medications?handler=SelectPet` | 302; owner control passed |
| `POST /Medications?handler=SetScheduleView` | 302; owner control passed |
| `POST /Home?handler=SetTaskView` | 302; owner control passed |
| `POST /Home?handler=ShowMoreTasks` | 302; owner control passed |
| `POST /Home?handler=EditPet` | 302; owner control passed |
| `POST /Home?handler=QuickLog` | 302; owner control passed |
| `POST /Home?handler=UpdateTask` | 302; owner control passed |
| `POST /Home?handler=DeleteTask` | 302; owner control passed |
| `POST /Medications?handler=EditMedication` | 302; owner control passed |
| `POST /Medications?handler=ConfirmSchedule` | 302; owner control passed |
| `POST /Medications?handler=UnconfirmSchedule` | 302; owner control passed |
| `POST /Medications?handler=DeleteMedication` | 302; owner control passed |
| `POST /VetVisits` | 302; owner control passed |
| `POST /VetVisits?handler=EditVisit` | 302; owner control passed |
| `GET /VetVisits?handler=PreviewDocument` | 200; owner control passed |
| `POST /VetVisits?handler=DownloadDocument` | 200; owner control passed |
| `POST /VetVisits?handler=UpdateDocument` | 302; owner control passed |
| `POST /VetVisits?handler=DeleteDocument` | 302; owner control passed |
| `POST /VetVisits?handler=DismissReminder` | 302; owner control passed |
| `POST /VetVisits?handler=ChangeStatus` | 302; owner control passed |
| `POST /VetVisits?handler=CompleteVisit` | 302; owner control passed |
| `POST /VetVisits?handler=DeleteVisit` | 302; owner control passed |
| `POST /Home?handler=ResetPetImage` | 302; owner control passed |
| `POST /Home?handler=DeletePet` | 302; owner control passed |

### Supplementary requests

| Request | Result |
|---|---|
| GET `/Home`, forged `userID`/`userName` cookies, no session | 302 to Login, no B data |
| GET B photo, forged identity cookies, no session | 401 |
| GET `/VetVisits?handler=PreviewDocument&documentID=23`, no session | 401 |
| POST `/Home?handler=DeleteTask`, B task plus forged userID cookie/body | 404, unchanged |
| POST `/Medications?handler=ConfirmSchedule`, B medication plus forged userID cookie/body | 404, unchanged |
| POST `/Home?handler=AddPet`, forged `UserID=25` and `NewPetID=25` | 302; new pet belongs to A; B unchanged; new pet then deleted |
| POST `/Home?handler=DeleteTask`, B task, no antiforgery token | 400, unchanged |
| JSON POST `/Home?handler=DeleteTask&taskID=40`, valid antiforgery header | 404, unchanged |

The application has no standalone task-detail or task-completion handler: tasks are activity logs displayed on Home. Completion was tested for medication doses and vet visits. Unsupported ID-bearing page paths were tested as routes and returned 404. Form submissions were exercised as real HTTP requests rather than browser clicks; client-side validation cannot prevent these attacks.

## Evidence and reproduction

- `scripts/authorization-audit.ps1`: fixture setup, attacks, full snapshots/file hashes, owner assertions.
- `scripts/authorization-audit-extra.ps1`: forged identity, overposting, JSON and CSRF checks.
- `scripts/audit-baseline.json`, `audit-home-fixed.json`, `audit-medications-fixed.json`, `audit-vet-fixed.json`, `audit-photo-baseline.json`: retained intermediate results.
- `scripts/audit-final.json`: 60 final attacks; `audit-final-controls.json`: 27 owner checks.
- `scripts/audit-extra.json`, `audit-extra-controls.json`: supplementary results.
- `scripts/audit-*-fixtures.json`: exact account and record identifiers, no passwords/session tokens.
- `scripts/audit-cleanup.json`: removal of all 20 disposable accounts created during this audit and their associated records/files. Fixture IDs in evidence are historical and no longer exist.

Run from the repository directory with PowerShell 7 and the configured test database available:

```powershell
dotnet run --no-launch-profile -- --urls http://127.0.0.1:5187 --environment Development --Logging:EventLog:LogLevel:Default None
# In a second terminal:
pwsh -NoProfile -File scripts/authorization-audit.ps1 -Phase final
pwsh -NoProfile -File scripts/authorization-audit-extra.ps1
pwsh -NoProfile -File scripts/audit-cleanup.ps1 -CurrentFinal
```

The local server needed execution outside the restricted sandbox for Windows SQL TLS support. Event Log logging was disabled only via launch arguments because sandbox Event Log access prevented local requests. Neither workaround changes application configuration or database credentials. Final `dotnet build --no-restore`: 0 errors, 0 warnings (incremental build); earlier compilation reported existing Login nullable warnings. `git diff --check` passed.

No production deployment was performed. Nginx must continue proxying uploads to ASP.NET Core; a separate static uploads alias would bypass the new photo handler. This audit establishes the tested record-ownership boundary, not a general authentication, password-storage, concurrency/load, or infrastructure security certification.
