# Isolated care verification

Run `sh scripts/care-verification/serve.sh`, then `node scripts/care-verification/checks.cjs`.
Use `PLAYWRIGHT_MODULE=/absolute/path/to/playwright` when the runtime is elsewhere.
Chrome must be installed. Local network/process access may require sandbox approval.
The test host is loopback-only at port 5091 and registers in-memory data services;
it never executes application startup or registers SQL/email/upload/payment services.
Fixture edits persist only in that fixture browser session. No real care records are used.

The host runs deterministic checks using production timezone, dose wording and visit
eligibility helpers. Browser checks exercise the compiled Razor pages and actual JS:
create/clear/update profile sections, unexpected-field isolation, encoded multiline text,
length errors, role and inaccessible-pet rejection, antiforgery, missing migration/read
failure, save failure retention, counted dose paging, individual given outcomes, exact
skipped/missed correction links, failed-dose persistence, print authorization and exclusions,
and desktop/mobile light/dark layout. Images and browser-print output go to `artifacts/care`.

The existing selected-pet suite can target another port via `PACKTRACKER_FIXTURE_URL`.
Production SQL authorization and persistence are reviewed in source, not proved by these
in-memory doubles. A database integration run must use a verified disposable SQL Server
database or separately authorized test records. See `CARE-DEPLOYMENT.md`.
