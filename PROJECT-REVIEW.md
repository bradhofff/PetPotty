# Homepage and dependency review — September 6, 2026

This is a source review focused on the public homepage, signed-in dashboard, and their dependencies, followed by an attempted browser login using the supplied sample account. No production behavior was changed. The available migration scripts do not contain the complete database schema or all stored procedures, so database-side behavior remains unverified.

## Feature map

| Feature | Responsible code | How it works |
| --- | --- | --- |
| Public homepage `/` | `Pages/Index.cshtml`, `Index.cshtml.cs` | Marketing sections and static dashboard previews. `IsLoggedIn` selects signup/login versus dashboard links. |
| Navigation and theme | `Pages/Shared/_Layout.cshtml` | Reads session for account links and `data-bs-theme`; loads Bootstrap, site CSS, and scripts. `RenderBody` inserts the current page. |
| Dashboard greeting and pet cards | `Pages/Home.cshtml`, `Home.cshtml.cs` | `OnGet` checks session, then `LoadData` retrieves pets through `IPetService`. |
| Last pee/poop labels | `PetAllTasks`, `LastActivityLabel`, `GetLatestActivityTasksByPetID` | Fetches latest matching activity separately from the displayed history. `PetAllTasks` is misleadingly named: it does not hold all tasks. |
| Quick logging | `.quick-log-trigger`, `submitQuickLog`, `OnPostQuickLog` | JavaScript selects browser-local time and submits a form; the handler saves through `IPetService.AddTask`. |
| Task log and history range | `PetTasks`, `OnPostSetTaskView`, `OnPostShowMoreTasks` | Session remembers the range; SQL retrieves matching rows and indicates whether older rows exist. |
| Add/edit/delete tasks | Home modal forms and matching `OnPost...Task` handlers | `asp-page-handler` selects a handler; bound fields provide its inputs; successful posts redirect to reload the dashboard. |
| Pet photos | Home crop/lightbox JavaScript, pet handlers, `IPetImageStorage` | Browser crops the selected image; server validates and saves it; SQL stores its URL. Reset and pet deletion clean up files. |
| Care reminders | `LoadData`, `CareItemLabel`, `PetCareItems` | Merges unconfirmed medication schedules with vet visits, sorts by due date, displays three per pet plus a remainder count. |
| Visual styling | `wwwroot/css/site.css`, Home inline CSS, layout inline CSS | Shared styles plus page-specific overrides. Dashboard JavaScript currently lives in Home's `Scripts` section; `site.js` is a placeholder. |

## Dependency injection

`Program.cs` registers the implementations. When Razor Pages creates `HomeModel`, DI supplies the constructor parameters and recursively supplies dependencies needed by those services.

| Requested interface | Implementation | Lifetime and responsibility |
| --- | --- | --- |
| `IPetService` | `PetService` | Scoped: one instance per request; pet profiles and task SQL. |
| `IMedicationService` | `MedicationService` | Scoped; medication/schedule SQL, including dashboard reminders. |
| `IVetVisitService` | `VetVisitService` | Scoped; visit SQL, dashboard appointments, document paths for pet deletion. |
| `IPetImageStorage` | `PetImageStorage` | Singleton: shared across requests; photo validation and filesystem operations. |
| `IVetVisitDocumentStorage` | `VetVisitDocumentStorage` | Singleton; attachment validation and filesystem operations. Home uses deletion when removing a pet. |
| `ILogger<HomeModel>` | Host logging infrastructure | Reports failures and cleanup problems. |

The host also supplies `IConfiguration`, `IWebHostEnvironment`, and service-specific loggers. SQL services read `DefaultConnection` from configuration. They open/dispose connections within methods; scoped registration does not make a shared connection or transaction. Storage services keep configuration-derived paths and loggers rather than mutable user state, which fits their singleton lifetime.

The existing interface boundaries are useful: database and filesystem details are outside the dashboard view and can be substituted in focused tests. Authentication is a separate issue: cookie authentication is registered, but Login currently sets session values rather than calling `SignInAsync`. Dashboard access relies on explicit session checks.

## Proposed improvements — awaiting discussion

1. **High: enforce task ownership on writes.** `OnPostQuickLog`, `OnPostAddTask`, `OnPostUpdateTask`, and `OnPostDeleteTask` accept client-supplied IDs without checking their owner. The corresponding service calls pass no user ID to SQL. A modified form can therefore request writes against another pet/task; the visible application layer cannot reject them by ownership. Pass the current user ID through the service contract and constrain writes to pets belonging to that user. Verify the stored procedures before changing them. Test that account A cannot create, change, or delete account B's tasks, while its own operations still work.

2. **High: replace direct password comparison.** Login compares `Users.pass` to the submitted password directly and signup sends the raw password to `AddUser`. There is no application password-hash verification in these paths. Introduce salted password hashing and verification with a migration/reset strategy for existing accounts; review Profile's password-change path at the same time. Login also trims passwords while signup does not, so leading/trailing spaces can prevent a newly created account from signing in. Never trim password input. This needs an explicit account migration plan before implementation.

3. **Medium: preserve the old photo until its replacement is committed.** `OnPostEditPetAsync` deletes the old file before updating its database path. If that SQL update fails, the catch deletes the replacement too, leaving the old database URL pointing at a missing file. Commit the new path first, then clean up the old file; test a simulated database failure. Consider making pet-detail/path changes one database transaction.

4. **Medium: use one authentication source.** The registered cookie authentication handler is unused by login, while session state is stored in process memory. An app restart loses the login session even though browser cookies may last 30 days. Move authentication to a signed principal and authorization policies, with session reserved for UI preferences. The raw `userID` cookie must not become trusted identity.

5. **Medium: validate dashboard form inputs on the server.** Home handlers do not check `ModelState.IsValid`, constrain task types, or consistently reject invalid dates/required text before writing. Browser controls can be bypassed. Use separate input models for each form so validating one modal does not fail because unrelated modal fields are absent.

6. **Next maintenance pass:** split the roughly 1,780-line Home view into understandable partials and dedicated CSS/JS; resolve overlapping CSS rules with visual checks. Rename `PetAllTasks` to reflect its actual contents. `LoadData` performs per-pet task/latest-activity/schedule queries; measure with realistic data before batching and moving database calls to async. Establish a consistent timezone policy because browser-local input is compared with server-local dates. Review landing-page claims such as feeding schedules and streak previews against implemented features.

## Verification

`dotnet build --no-restore` succeeded with zero errors and eight nullable warnings in the unchanged Login code. Changes in this review are explanatory comments and this document only. `git diff --check` found no whitespace errors.

The local login form rendered and submitted in the browser, but SQL client returned: "The instance of SQL Server you attempted to connect to requires encryption but this machine does not support it." Credentials could not be verified and the signed-in dashboard could not be exercised. No pet/account records were changed. A local Windows Event Log permission failure was avoided using a process-only logging override; no project settings were edited. Account-isolation tests and database integration remain outstanding.
