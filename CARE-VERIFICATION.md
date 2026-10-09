# Care upgrade verification — October 3, 2026

All mutations used the isolated fixture host; no real care data, shared database, SQL migration or stored procedure was changed.

## Results

- Application and fixture builds: zero warnings/errors. `git diff --check`: clean.
- Deterministic production-helper checks: browser-offset midnight conversion; timed vs any-time medication; today/one-day/multi-day wording; three-day boundary; skipped vs explicitly given; timed visit cutoff/equality; all-day and unspecified visits; midnight expiry; dismissal; cancelled/completed exclusion; earlier custom reminder activation.
- Browser tests: empty profile, create/edit/clear, section isolation (including unexpected submitted fields), multiline/HTML-safe rendering, maximum lengths, microchip leading zeros, Owner editing/Caregiver read-only, inaccessible pet denial, antiforgery rejection, missing migration and other read errors, failed-save input retention, single submission and success confirmation.
- Dashboard tests: doses 65 days old, skipped/missed statuses, multiple doses of one medication, eight-row count/paging, giving one without hiding another, later administration leaving earlier skipped records visible, future doses retained, exact-dose correction navigation, any-time vs timed today, day-three included/day-four excluded, visit cutoffs/custom/dismissal, empty and retrieval-error states. Failed dose saves retain inputs and reminders.
- Existing selected-pet regression suite passes across Health/Medications/Vet Visits: shared selection, explicit record precedence, refresh, clearing, legacy/stale/account/household boundary handling, denied IDs, invalid forms and successful add/edit redirects.
- Browser layouts: 1440px and 390px, light and dark; no horizontal overflow; modal scrolling and responsive recent-activity defaults. No JavaScript page errors.
- Care sheet: authorized preview and inaccessible/anonymous denial, current medication instructions and next appointment, generated date, omission of microchip/account/member/history details; print controls excluded from printing. Browser-generated sample fits one A4 page and was visually inspected with Poppler.

## Reproduction and evidence

See `scripts/care-verification/README.md`. The fixture host runs the real compiled Razor pages and production scripts/styles with per-session in-memory data. It includes no SQL service registrations. The prior selected-pet fixture gained only the new care service stub and an optional test-base-URL override.

Screenshots (all fictional fixtures):

- `artifacts/care/health-1440-light.png`, `health-1440-dark.png`, `health-390-light.png`, `health-390-dark.png`.
- `artifacts/care/dashboard-1440-light.png`, `dashboard-1440-dark.png`, `dashboard-390-light.png`, `dashboard-390-dark.png`.
- `artifacts/care/edit-1440-light.png`, `edit-1440-dark.png`, `edit-390-light.png`, `edit-390-dark.png`.
- `artifacts/care/care-sheet.png`; `care-sheet.pdf` is a browser-print verification sample, not an application dependency.

## Limits of this verification

Production SQL was reviewed for active-member/household scoping, role rechecks, section-only update columns, absence of a historical dose cutoff/end-date filter, counted pagination and visit status/dismissal predicates. Those SQL paths, the additive migration, concurrent transactions, database cascade deletion and query performance were not executed against SQL Server. Fixture tests establish page/handler/JavaScript behavior; they do not prove live database isolation or stored-procedure semantics.

The current timezone infrastructure is a UTC-offset cookie, not a DST-aware IANA zone. Existing late-dose schedule rebasing remains in its stored procedure. Existing file uploads, billing, mail and production care mutations were not exercised. Health event add/edit/navigation regression checks use fake-service outcomes; the original vet-report implementation was preserved rather than rewritten. See `CARE-DEPLOYMENT.md` for deployment order and these constraints.
