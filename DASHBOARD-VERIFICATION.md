> Historical dashboard milestone: the subsequent Health/care upgrade supersedes the Today section, query windows and skipped/missed behavior below. See [CARE-DEPLOYMENT.md](CARE-DEPLOYMENT.md) and [CARE-VERIFICATION.md](CARE-VERIFICATION.md) for the current implementation and verification.

# Daily-care dashboard

Pet cards remain first, retaining Pee/Poop time selection, backdating, pet/photo editing, Add Task, and View Health. Following the requested layout adjustment, recent activity sits below the cards, aligned to the same columns and collapsed by default below 768 px and expanded on larger screens, with counts and a manual toggle. Recent / All Time and progressive history remain. Empty periods offer older history; long notes wrap.

Today’s care follows activity. It separates overdue medication, past unresolved appointments needing an update, today, and collapsed upcoming care. Four rows per group are initially shown; remaining rows expand. Dose confirmation and skipped/missed dialogs are shared with Medications, and post to the existing medication handlers and services. Confirmation happens before saving; success appears only after a successful response. Pending requests disable controls, errors retain inputs and restore keyboard focus. Visit actions pass the exact pet and visit to the existing authorized OpenVisit handler.

## Database and date behavior

- No schema, migration, stored procedure, or DDL changes; none were executed.
- Application SQL changed: the medication schedule reader now supports one household-wide, parameterized date-window query using the existing mapper. It selects persisted `Due` rows from 30 local calendar days ago through the next seven days (exclusive end: today + 8). Existing medication-page query behavior is preserved.
- The existing bounded vet query is reused with that window. Its mapping now retains visit IDs and unknown/all-day timing, and no longer drops past timed appointments or fabricates a 9 AM time.
- Both queries retain active-household and active-member restrictions. Writes still use the existing household permissions, ownership checks, transactions, and stored procedures. Antiforgery remains enabled.
- Persisted taken/skipped/missed outcomes are excluded. Doses the existing service calculates as missed but that still have stored status `Due` stay visible as “Missed · not recorded”; they are not treated as completed care.
- Window coverage is stated on the page. Older records and care beyond the window remain accessible through per-pet full schedules. No recurrence is generated, and no full histories are loaded for the care summary.
- Heading, grouping and care classification use the existing browser UTC-offset service. Existing limitations remain: the first request without the offset cookie uses the configured fallback; an offset is not an IANA timezone, so it cannot model historical/future DST changes. Stored local date/time values retain their existing interpretation.
- The browser prevents accidental repeated submissions while saving. This is not durable request idempotency across tabs, app instances, or an uncertain network response. Errors instruct users to check history before retrying.

## Verification

`dotnet build --no-restore` passes with zero warnings/errors. `git diff --check` passes.

An isolated ASP.NET fixture host renders the actual compiled Razor pages and real scripts/styles, replacing all data services with in-memory fixtures. It has no SQL, email, invitation, payment or upload service. It is separate from production startup. Browser checks cover:

- Desktop (1440 px) and mobile (390 px), light and dark, no horizontal overflow.
- Two pets, no pets, no care, load failure, caregiver edit restrictions, older activity with no recent activity.
- Responsive activity defaults (collapsed mobile, expanded desktop), expanded long notes, and progressive long care lists.
- Overdue and any-time doses, future care, exclusion of recorded skipped care, today's appointments and past unresolved visits.
- Exact pet/medication context in confirmation, backdated quick logging, dose/skipped/missed handler success, missing-reason and malformed-date rejection, denied records.
- Duplicate quick-log and dose submits, failed-save input retention, modal focus return, missing-antiforgery rejection.
- Deterministic production helper checks for UTC/local midnight, any-time day boundaries, and timed-dose lateness.

Screenshots in `artifacts/dashboard/` show fixture records, not private care data.

To reproduce: run `sh scripts/dashboard-verification/serve.sh`, then `PLAYWRIGHT_MODULE=/path/to/playwright node scripts/dashboard-verification/checks.cjs` with installed Chrome. The fixture source uses `.cs.txt` so it cannot enter the production compilation. The host is loopback-only on port 5088.

Not verified against a live database: SQL execution/plans, stored-procedure mutation behavior, simultaneous cross-household requests, or actual file upload/delete operations. Authorization was reviewed in code; fake-service tests do not prove live SQL isolation. Existing pet/photo and health workflows are retained but were not mutation-tested against real records. No real records were changed.
