# Weight tracking verification

Start the isolated host with `sh scripts/care-verification/serve.sh`, then run
`node scripts/weight-verification/checks.cjs`. Set `PLAYWRIGHT_MODULE` if Playwright
is elsewhere, or `PACKTRACKER_FIXTURE_URL` for a different loopback port.
Chrome and the local test server may require sandbox approval.

The host uses session-backed fixture data and never registers SQL services or runs
production app startup. Checks exercise the compiled Razor page, actual form posts,
and compact weight card/modal: first weigh-in, editing, deletion, latest-by-measurement-date,
pounds/kilograms, timezone round-trip, modal open/close, invalid/missing input, future
dates, pet isolation, antiforgery, caregiver recording, error retention, and responsive
light/dark rendering. Screenshots go to `artifacts/weight`.

The SQL service scopes reads and writes to the active household, active membership,
and pet; updates/deletes also match the entry ID. These SQL checks and the pet-delete
transaction are reviewed in source, not executed by the in-memory tests. Live database
persistence is not verified by this suite.

## Database and deployment

The feature expects the exact `dbo.PetWeights` table from
`Migrationsss/2026-10-04_AddPetWeights.sql`. If the SQL supplied in the chat was already
run, no further schema change is required. The app does not apply migrations at startup.
Deploy/restart the updated application to expose the Health page's Weight overview card.

Measurements retain their entered value and unit. The change summary converts the previous entry to the latest entry's unit.
The history always shows the original values. Measurement times are stored in UTC
using the app's existing browser-offset timezone behavior. Editing is to minute precision.
Deleting a weight requires the inline confirmation and permanently removes that entry.
Deleting a pet removes its weight entries inside the existing pet-deletion transaction.
