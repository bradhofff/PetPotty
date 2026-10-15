# PetPotty

PetPotty (also called PackTracker in parts of the UI) is an ASP.NET Core 10 Razor Pages app for tracking a household's pets. It uses SQL Server for accounts, care records, schedules, and billing status. Pet photos and vet documents are stored on disk and served through authorized app endpoints.

## What the app does

| Area | Current behavior |
| --- | --- |
| Dashboard | Shows pets, recent activity, medication and vet reminders; lets members record potty, food, walks, play, and other care. Owners can add, edit, and remove pets and photos. |
| Medications | Manages prescriptions and generated dose schedules; records taken, late, skipped, and missed doses with time, notes, and attribution. |
| Health | Logs symptoms and incidents, and shows a medical timeline combining those events with medication and vet records. |
| Vet visits | Tracks appointments, reminders, outcomes, history, and private attachments. |
| Reports | Generates printable 30-day, 90-day, or custom-range pet health reports. |
| Households | Lets an owner invite members or caregivers, manage roles, and switch the active household. Each pet and its records belong to a household. |
| Premium | Offers household-wide Monthly, Yearly, and Lifetime plans through Stripe Checkout. The purchaser can manage a recurring subscription in Stripe's billing portal. |

The public landing page is `/`; signed-in work starts at `/Home`. The main feature pages are `/Medications`, `/Health`, `/VetVisits`, `/Reports`, `/Profile`, and `/Purchase`. `/Premium` redirects to the purchase flow. A saved light/dark preference is available from the shared layout.

## Code and data

- [`Pages/`](Pages/) contains Razor pages and their handlers; [`Services/`](Services/) contains the business and SQL access code. [`Program.cs`](Program.cs) registers services, session/authentication middleware, file handling, and the Stripe webhook.
- SQL access is mostly hand-written ADO.NET with `Microsoft.Data.SqlClient`; some paths use stored procedures and others use parameterized inline SQL. EF Core packages are referenced, but the app has no `DbContext` or EF migrations.
- [`Migrationsss/`](Migrationsss/) contains dated SQL changes, verification scripts, procedure snapshots, and rollback scripts. These scripts are **not** a complete base database schema. Review each script and its target database before applying it.
- [`wwwroot/`](wwwroot/) holds CSS, JavaScript, Bootstrap, and other public assets. [`scripts/`](scripts/) holds setup and historical audit tools/evidence.
- `uploads/` and `vet-documents/` are private runtime data. `bin/`, `obj/`, and `publish/` are generated output. These are excluded from Git or deployment as appropriate.

### Access rules

The active household determines which pets and records a signed-in person can see. `HouseholdAccessRules` grants everyone view and normal care-recording access; Owner and Member can manage care plans; only Owner can manage pets, household settings, and members. Invitation links have an expiry and are checked against the signed-in email. The account that bought a recurring Premium plan is the only member allowed into its Stripe billing portal.

Handlers and services check the server-side session and household membership; browser-supplied record IDs do not establish access. Pet photos at `/uploads/pets/{fileName}` and vet documents are returned by authorized Razor handlers, not by public static-file mappings. Nginx must proxy those requests to ASP.NET Core rather than expose either storage directory directly.

Authentication is still based on the `userID` in an in-memory ASP.NET Core session. Cookie authentication is registered, but login does not create a claims principal. Existing password storage/comparison is legacy plaintext behavior. Changing either requires a deliberate account migration. In-memory sessions also disappear on app restart.

### Time and history

New health-event and dose-administration timestamps use UTC. Older task, schedule, and visit fields contain local wall-clock values; the app uses a browser offset for display and report projections, with `TimeZone:FallbackUtcOffsetMinutes` for non-browser requests. Historical local values cannot be safely converted to UTC without knowing the original time zone. Medication adherence defaults to 60 minutes for “late” and 720 minutes for “missed” in [`appsettings.json`](appsettings.json).

## Run locally

Install the .NET 10 SDK and use a reachable SQL Server database with the required schema and migrations. A clone does not include a database backup or a full initial schema. Create `appsettings.Development.json` from [`appsettings.Development.example.json`](appsettings.Development.example.json), then provide `ConnectionStrings:DefaultConnection` and any SMTP/Stripe test credentials you need. The filename's capitalization matters on case-sensitive systems. Keep this file private; do not put secrets in tracked `appsettings.json`.

```bash
dotnet restore PetPotty.csproj
dotnet run --project PetPotty.csproj --launch-profile http
```

Open `http://localhost:5078`. The HTTPS launch profile uses `https://localhost:7200`. To check compilation without starting the app, run `dotnet build PetPotty.csproj`. A Mac can edit/build the source with the .NET 10 SDK, but running it still requires network access to the configured SQL Server. If transferring a private archive that includes local settings or uploads, keep that archive out of Git and share it privately.

On this Windows checkout, a build check on October 15, 2026 failed with duplicate assembly attributes from ignored, generated files under `tests/PetPotty.Billing.Tests/obj/`. That directory is not tracked; a clean checkout does not include it. Clear stale generated test output or exclude nested test output from the web project's compile items before treating a local build as a source-code failure.

For invitation email, configure `App:BaseUrl`, `Email:FromAddress`, and `Email:Smtp:{Host,Port,EnableSsl,Username,Password}`. If SMTP delivery fails, the invitation stays pending; resend rotates its token and expiry.

## Database changes and deployment

Back up the target database and record key row counts before a migration. Run SQL scripts manually against the intended database, then run the corresponding read-only verification scripts and compare counts before sending traffic to the new build. The main release sequence is:

| Feature | Key SQL files in `Migrationsss/` |
| --- | --- |
| Pet images, visits, theme, and medication scheduling | `2026-07-22_AddPetProfileImagePath.sql`, `2026-07-25_AddVetVisitsFeature.sql`, `2026-07-29_AddUserDarkModePreference.sql`, and the dated August schedule/visit updates |
| Health history and adherence | `2026-09-10_AddHealthHistoryMvp.sql`, then `2026-09-10_VerifyHealthHistoryMvp.sql` |
| Household accounts and permissions | `2026-09-13_AddHouseholdFamilyAccounts.sql`, then `2026-09-13_VerifyHouseholdFamilyAccounts.sql`; all three migration `FailureCount` values must be zero |
| Stored procedure alignment | `2026-09-20_AlignStoredProcedures.sql` and `2026-09-20_SnapshotCurrentProcedures.sql`; a rollback script is also present |
| Household Premium | `2026-09-23_AddHouseholdPremium.sql`, then `2026-09-25_AddHouseholdPremiumPlanType.sql`; rerun the September 25 script when deploying the current checkout/webhook logic |

The July 22 image migration explicitly selects `PetPottyDb_Dev`; review and adapt its database target before any production use. The September 20 procedure scripts address differences found between source-controlled procedures and a previously deployed test database; do not assume the scripts can create a fresh database by themselves. `2026-09-23_FixGetTasksByPetIDRecent.sql` is a targeted repair if the dashboard reports “too many arguments” for that procedure.

On the VPS, run [`scripts/setup-pet-uploads.sh`](scripts/setup-pet-uploads.sh) with the `petpotty` service account. It prepares `/var/www/petpotty/uploads/pets` and private `/var/www/petpotty/vet-documents`. Keep uploads outside `wwwroot`; serve them only through the app. The production service reads environment settings from `/etc/petpotty/petpotty.env`; restart `petpotty` after changing deployed code or environment values.

After a release, smoke-test an existing account and household, invitations and role boundaries, pet/photo access, medication reminders and dose outcomes, health/report date ranges, vet attachments, and a second account's inability to read or change foreign records. A prior HTTP authorization audit recorded 60 passing cross-account denials and 27 passing owner controls; its PowerShell harness and JSON evidence remain in `scripts/`. Those results are historical, not a substitute for checking a new deployment.

## Premium billing

Premium belongs to the active household. Monthly and Yearly are subscriptions; Lifetime is a one-time payment. A successful Checkout return alone does not grant access: a signed Stripe webhook records the event in `HouseholdPremiumTransactions` and updates the entitlement on `Households`. The ledger's unique event ID prevents duplicate processing. `/PremiumTest` provides a simple entitlement check for a signed-in member.

The tracked [`appsettings.json`](appsettings.json) contains public **live** Price IDs and displayed prices. Keep `Stripe:SecretKey`, `Stripe:WebhookSecret`, and the billing portal configuration ID in the environment or a private development file. Production checkout requires a live Stripe key and HTTPS `App:BaseUrl`; development requires a test key and matching test Prices. Confirm each test Price's amount **and billing interval** in Stripe before using it; an older Year ID in the sample configuration was found to bill monthly.

Register `POST /stripe/webhook` for Checkout completion/asynchronous success, subscription create/update/delete, invoice paid, and invoice payment failed events. For local testing, a Stripe CLI listener can forward events to `http://localhost:5078/stripe/webhook`; use the listener's `whsec_...` value locally. A live endpoint needs its own signing secret. The webhook's verified events, not the browser redirect, drive entitlement.

For recurring plans, the purchaser's **Manage billing** button opens a configured Stripe Customer Portal with cancellation at period end, payment-method updates, and invoice history; plan switching is disabled. On return, the app refreshes the subscription status and end date from Stripe, while protecting against older webhook updates. A scheduled cancellation keeps access through its paid period and then ends it even if the final webhook is delayed. Lifetime has no cancellation portal.

Before offering live checkout, verify the SQL migrations, live Prices and labels, HTTPS base URL, live webhook deliveries, portal configuration, and one authorized purchase. Confirm both the household's entitlement and a free household's denial. The October 1, 2026 sandbox checks covered Yearly and Lifetime purchases and Yearly cancellation; they did not constitute a live payment or deployment check.

## Historical reviews and current limits

This guide replaces the project-authored Markdown files formerly named `ADD-PET-OPTIONAL-FIELDS-VERIFICATION.md`, `AUTHORIZATION-AUDIT.md`, `DEPLOYMENT.md`, `HEALTH-PLATFORM-AUDIT.md`, `HOUSEHOLD-ACCOUNTS.md`, `PREMIUM-ACCOUNTS.md`, `PROJECT-REVIEW.md`, and `STORED-PROCEDURE-AUDIT.md`. Their detailed dated reports remain recoverable from Git history at commit `d13ed08`. The optional-field verification confirmed that adding/editing a pet with blank Breed or Age works; the old MarketingCapture screenshot test project was removed before this commit. Historical findings must be checked against the current code and deployed database before being treated as open issues.

Notable follow-up work from those reviews includes moving off plaintext passwords/session-only identity, reconciling Stripe state when a webhook is missed, adding automated integration coverage for household and billing boundaries, and closing remaining schema/procedure drift. The stored-procedure audit specifically noted a possible `VetVisits.Diagnosis` length mismatch and absent database uniqueness constraints on username/email; validate those against the target database before changing them.

The third-party [jQuery Validation license](wwwroot/lib/jquery-validation/LICENSE.md) stays beside the vendored library because its copyright and permission notice must accompany that code.
