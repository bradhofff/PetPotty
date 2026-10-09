# Health care information and pet-card reminders

## Deployment

1. Review `Migrationsss/2026-10-03_AddPetCareProfiles.sql` against the intended PackTracker database and its existing Pets/Users tables. Back up through the normal deployment process.
2. With explicit database-owner authorization, apply that single transactional script before deploying the application. It creates `dbo.PetCareProfiles` only if absent; it does not modify medication/visit tables or stored procedures. The application never creates tables at startup.
3. Deploy the application, then verify Health and the care-sheet preview with an authorized account. Existing pets need no backfill. Until the migration is installed, Health displays a clear care-information unavailable message while retaining its existing history and management actions.

**Migration status: not applied to any database by this task.** Verification uses an isolated in-memory fixture host. No shared or production records were changed.

The table has a PetID primary key (one row per pet), optional sized Unicode text, UTC update timestamp and updating user. Pet deletion uses the existing Pets delete operation; the new foreign key cascades only its care-profile row. Updating one section cannot clear fields in another section. No existing visit is treated as the primary clinic. Microchip is text and is omitted from the care sheet.

Rollback: restore the previous application build and leave the additive table in place to preserve entered information. Dropping it would delete care information and requires separate authorization.

## Permissions and UI

Care editing uses `ManageCarePlans`, the existing Owner/Member permission for shared medication and vet plans. Caregivers can view/print and keep their existing care-recording capabilities. Every request rechecks access to its pet; SQL joins active membership and the active household. Saves recheck Owner/Member membership under transaction locks. Antiforgery and server length checks apply to section saves; SQL columns come from a fixed catalog. Text is Razor encoded, and client updates use `textContent`.

Health retains its title, shared Current pet selector, statistics, medications, symptoms/incidents, grouped/timeline history, filters, visits and original vet report. New important health information opens expanded; routine and contact sections collapse with short previews. Three modals prepopulate values, retain failed entries and report successful saves by pet. Blank means “Not recorded”; “None known” is shown only if explicitly entered. Print care sheet opens an authorized preview without invoking printing; the user then chooses browser Print / Save as PDF.

## Application query changes (no notification database deployment)

- Medication reminders read existing scheduled-dose rows. Only stored Taken and Taken late are given. Skipped/Missed remain accurately labeled with an exact-dose review link to the existing Undo, then Taken workflow.
- The query has no historical lower bound and no medication end-date exclusion. It returns eight medication rows per pet with a window count, ordered past due first, then scheduled date/identity. Cards show three reminders initially, expandable to the current page, with counted Previous/Next dose links. Each action retains its own medication/date identity. Counts/pages are refreshed on reload; concurrent writes can change page contents.
- The future window includes today and the next three local calendar days, ending exclusively at local midnight on day four. Due-status wording uses the same helper on Dashboard and Medications.
- Timed doses become past due strictly after their scheduled local time. Any-time doses become past due only after their local calendar day ends. Day counts subtract local calendar dates: earlier today is “Past due”; yesterday is “1 day past due”; four dates earlier is “4 days past due.”
- Visits include actionable Scheduled/Confirmed/Rescheduled records in the same four-calendar-day range. An already-triggered explicit reminder can begin showing a farther-future appointment earlier. Once visible, it stays until the appointment cutoff. Explicit Dismissed reminders remain hidden. No reminder state is written by dashboard retrieval.
- A timed visit is removed strictly after its scheduled local appointment time (still eligible at equality). All-day/time-unspecified visits remain through their local calendar day and disappear at next midnight. Completed/cancelled visits are excluded by SQL. Hiding a card reminder does not delete or change the visit.
- Each pet card contains its own notifications. No separate Today section. Empty cards omit reminders; retrieval failure displays an error rather than a completion claim. Dose actions keep the existing medication service, attribution, actual time, notes and transaction behavior; the dashboard reloads only after a successful server response.

## Existing limitations retained

The existing timezone service uses the browser’s UTC-offset cookie, with the configured fallback on first request. An offset cannot represent historical/future daylight-saving transitions like an IANA timezone. This change does not rewrite stored local schedules. Older Vet Visits page reminder-status refreshes still use server-local SQL time; dashboard eligibility calculates its own local cutoff and only treats deliberate Dismissed status as dismissal.

The existing ConfirmMedicationSchedule procedure may rebase later Due schedules after a late administration. This task preserves that business logic and changes no procedure. Reloads show the persisted, potentially moved future schedule. Earlier outstanding doses are not hidden by recording a later dose. Live procedure execution and SQL query plans need deployment-environment verification; the isolated fixtures cannot establish those behaviors against a database.
