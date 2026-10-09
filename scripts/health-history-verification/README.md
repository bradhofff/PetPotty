# Health history and upcoming care verification

Start the isolated host with `sh scripts/care-verification/serve.sh`, then run
`node scripts/health-history-verification/checks.cjs` (Chrome / loopback access may
require sandbox approval). Set `PLAYWRIGHT_MODULE` or `PACKTRACKER_FIXTURE_URL`
when needed. The `history` scenario supplies 127 fictional history records,
future doses and visits. No SQL services or production records are used.

Verified October 9, 2026:
- Default Recent covers two calendar months through today; boundary included.
- All time persists across reload, pages 50 rows at a time and reaches old records.
- Search, type, sort and custom dates are applied before paging; mode changes reset
  the page while retaining other filters. Reset restores Recent.
- Upcoming care stays independent of history, displays five entries per page and
  includes the full two-calendar-month boundary date and excludes later doses/visits;
  explicit links retain pet/record context.
- All-day and unspecified appointments stay through today; past timed,
  cancelled and completed visits are excluded. Given doses are excluded.
- Antiforgery/invalid selection checks; preserved care and weight controls.
- Desktop/mobile, light/dark, no horizontal overflow or JavaScript errors.
- Existing care-information and weight regression suites passed; application and
  fixture builds passed with zero warnings/errors.

Screenshots: `artifacts/health-history`. Production helper cutoff checks run at host
startup. SQL authorization/search/pagination were reviewed in source, not executed
against SQL Server; fixtures validate handlers/rendering/navigation, not live SQL
query plans or isolation. No new migration, schema, or stored procedure deployment.

The existing overview still uses its existing full-schedule service read to compute
medication summaries/adherence. The history timeline itself is paged in SQL.
Upcoming care reuses the already-loaded schedules/visits and pages its rendered list.
