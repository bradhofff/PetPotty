# Uploaded file and Vet Visits deployment

The application serves `/uploads/pets/{fileName}` through an ownership-checked
Razor Page from `/var/www/petpotty/uploads`. Keep uploads outside `wwwroot`.
Nginx must proxy these requests to ASP.NET Core; do not configure an uploads
alias or static-file location that bypasses the session and ownership checks.
Vet visit documents are deliberately stored outside the publish and static-file
directories at `/var/www/petpotty/vet-documents`; they are only returned by an
owner-authorized Razor Page handler.

## Development database

1. Review `Migrations/2026-07-22_AddPetProfileImagePath.sql`. It intentionally
   starts with `USE [PetPottyDb_Dev]`.
2. Run it against the development database with the normal manual `sqlcmd`
   workflow.
3. Do not run the development script against production. For production,
   make a reviewed copy and change only the `USE` database name before the
   manual `sqlcmd` step.

For Vet Visits, review and run
`Migrationsss/2026-07-25_AddVetVisitsFeature.sql` against the intended database.
The script does not contain a `USE` statement and upgrades the earlier draft
`VetVisits` table when present.

For the saved light/dark account preference, review and run
`Migrationsss/2026-07-29_AddUserDarkModePreference.sql` against the intended
database before deploying the matching application build. The script is
idempotent and does not contain a `USE` statement.

For the medication schedule range toggle, review and run
`Migrationsss/2026-08-20_UpdateMedicationScheduleRange.sql` against the intended
database before deploying the matching application build. It creates the
all-time and next-two-month stored procedures used by the application.

For optional vet-visit costs, review and run
`Migrationsss/2026-08-20_MakeVetVisitCostOptional.sql` against the intended
database before deploying the matching application build. It updates the add
and edit procedures so a missing cost is stored as `NULL`.

For the simplified vet-visit forms and Add/Edit attachments, review and run
`Migrationsss/2026-08-21_SimplifyVetVisitForms.sql` against the intended
database before deploying the matching application build. It removes the
stored-procedure requirement for address and phone, combines legacy preparation
text into notes, and expands that combined field to `nvarchar(max)`.

For unconfirmed medication reminders that were dropping off the dashboard
overnight, review and run
`Migrationsss/2026-08-28_KeepUnconfirmedMedsVisible.sql` against the intended
database before deploying the matching application build. It updates
`GetScheduledMedsByPetID_Next2Months` so an unconfirmed dose stays in the
result set no matter how far in the past its schedule date is; only confirmed
doses are still limited to the forward-looking window.

After applying the release SQL, run
`Migrationsss/2026-08-21_VerifyCurrentRelease.sql` for read-only schema and
procedure checks. For a functional Add/Update check, set an existing pet ID in
`Migrationsss/2026-08-21_SmokeTestVetVisit.sql` and run it; the test performs an
outer transaction rollback and leaves no test visit behind.

For the health timeline, structured symptoms/incidents, and medication
adherence release, review and run
`Migrationsss/2026-09-10_AddHealthHistoryMvp.sql` before deploying the matching
application build. It is additive: it creates `HealthEvents`, extends existing
medication schedule rows, backfills confirmed rows as Taken, adds task/visit
creator attribution, and retains the legacy scheduling columns and procedures.
It does not reinterpret historical local timestamps as UTC. Run
`Migrationsss/2026-09-10_VerifyHealthHistoryMvp.sql` afterward for read-only
schema/procedure checks.

If the dashboard fails with `GetTasksByPetID_Recent has too many arguments
specified`, the database still has the pre-household three-parameter procedure.
Run `Migrationsss/2026-09-23_FixGetTasksByPetIDRecent.sql` as a targeted repair,
or rerun the full `Migrationsss/2026-09-20_AlignStoredProcedures.sql` release
script before restarting the application.

The adherence thresholds are configuration values:

```json
"MedicationAdherence": {
  "LateAfterMinutes": 60,
  "MissedAfterMinutes": 720
}
```

`LateAfterMinutes` determines whether an exact-time administered dose is
recorded as Taken late. An unresolved dose becomes effectively Missed after
`MissedAfterMinutes`; for timing-does-not-matter medication the countdown
starts at the end of the user's local scheduled day. The browser offset cookie
is used for display and local-to-UTC conversion. Configure
`TimeZone:FallbackUtcOffsetMinutes` for non-browser requests; zero (UTC) is the
safe default.

## VPS filesystem

Run the setup script with the user (and optional group) from the `petpotty`
systemd unit:

```bash
sudo ./scripts/setup-pet-uploads.sh <petpotty-service-user> [petpotty-service-group]
```

The script creates `/var/www/petpotty/uploads/pets` with mode `755` and the
private `/var/www/petpotty/vet-documents` directory with mode `750`. Both are
owned by the application service account.

## Manual acceptance checks

- Add a pet with a JPEG and a PNG smaller than 2 MB. Confirm the card image is
  still 52 by 52 pixels, the file is under `uploads/pets`, and the database
  stores `/uploads/pets/{petId}_{guid}.{ext}`.
- Confirm a pet without an image still displays its emoji placeholder.
- Confirm files over 2 MB, non-image extensions, and a non-image renamed to
  `.jpg` are rejected in the modal without creating a pet or file.
- Replace a pet image and confirm the old file is removed, the new file exists,
  and the database path changes.
- Select a photo, drag and zoom it in the circular crop editor, and confirm the
  pet card matches the portion shown inside the crop circle.
- Use **Reset to default** and confirm the database path becomes `NULL`, the
  uploaded file is removed, and the emoji placeholder returns.
- Exercise the user2 reset process with an imaged pet. No user2 reset code is
  present in this repository, so also verify that the external reset process
  handles files referenced by the rows it deletes; otherwise those files need
  cleanup in that process.
- Add, edit, reschedule, cancel, and complete an owned vet visit. Confirm each
  status change appears in record history and a second user cannot access it by
  changing posted IDs.
- Upload each supported document type (PDF, DOC, DOCX, JPEG, PNG), reject an
  unsupported or over-10-MB file, download as the owner, and confirm another
  user receives no document.
- Add and update a vet visit with an attachment. Confirm the file appears in
  the visit's Attachments list and can only be downloaded by its owner.
- Confirm dashboard pet cards only show unconfirmed medication doses from today
  through three calendar days ahead, and active vet visits from today through
  six calendar days ahead (a 7-day heads-up), with at most three visible rows.
- Leave a medication dose unconfirmed past its scheduled time/day and confirm
  its reminder stays on the pet card (does not silently disappear) until it is
  actually confirmed on the Medications page.
- Confirm the pet card's last pee/poop label shows just the time for same-day
  activity, and "Day of week @ time" (e.g. "Wednesday @ 9:15 PM") once the
  activity happened on a different calendar day than today.
- Log a minimal symptom and a detailed incident. Confirm each appears only on
  the correct pet's Health timeline and that a second account receives 404 for
  forged pet/event IDs.
- Confirm an exact-time dose on time and more than the configured late window
  afterward. Confirm Taken and Taken late remain distinct, actual time and user
  are recorded, and future exact-time occurrences retain the existing shift
  behavior.
- Mark doses Skipped and Missed with reasons, then undo them. Confirm the
  outcomes remain distinct and do not appear as administered doses.
- Generate 30-day, 90-day, and custom reports. Confirm the inclusive local date
  boundaries, health event types, adherence summary, vet visits, and daily care
  counts; print or save the report as PDF from the browser.
