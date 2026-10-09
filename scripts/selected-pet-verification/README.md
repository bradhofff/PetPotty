# Shared selected-pet verification

Run `sh scripts/selected-pet-verification/serve.sh`, then in another terminal run
`python3 scripts/selected-pet-verification/checks.py`.

The host uses the production Razor pages and in-memory service fixtures for Abby and
Bella. It never registers SQL services. Successful write calls return simulated
results; they do not persist care records. The test-only `/fixture/*` endpoints exist
only in this separate host, not in the application.

Verified on October 3, 2026:

- `dotnet build --no-restore`: zero warnings/errors; `git diff --check`: clean.
- Shared selection from all three pages, refresh, explicit clear, and removal of
  all three legacy preferences.
- Explicit medication and visit links override another remembered pet. A medication
  opened from an empty preference still opens its edit modal.
- Invalid/unauthorized explicit IDs return 404 without replacing a valid selection.
- Stale IDs and simulated account/household session changes clear the preference.
- Missing selection on the Vet Visits sort handler preserves the preference.
- Validation failures retain the form's pet; medication edits use the record's pet
  even when a different pet was posted or remembered.
- Simulated successful Health, Medications, and Vet Visits add/edit submissions
  retain their pet through redirects.
- Browser: dashboard's Abby View health action, both dashboard cards still visible,
  Abby across pages, and Bella selected on Vet Visits appearing on Health.
- Browser: all three selectors inspected at desktop and 390px mobile widths in
  light and dark themes. Labels, pet names, species icons and switch controls are
  readable and fit the viewport. Temporary viewport override reset afterward.

Limitations: household/account/access-loss checks simulate session/access boundaries;
no real membership changes, pet deletions, login credentials, database writes or
attachment uploads were exercised. The existing production household-scoped data
services still enforce authorization independently of this navigation preference.

Storage: `care-selected-pet` in ASP.NET session, with user and household scope keys.
Household changes and successful login explicitly clear it; logout already clears
session. Pet deletion revalidates it, and each care-page load discards inaccessible
or deleted remembered IDs. Legacy page preferences are removed, never restored.

No schema, migration, stored procedure, package or other database deployment changes
are required.
