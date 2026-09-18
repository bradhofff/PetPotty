# Uploaded file and Vet Visits deployment

The application serves `/uploads` through ASP.NET Core from
`/var/www/petpotty/uploads`. Nginx does not need an uploads location.
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

For Households, Health events (symptoms/incidents), and care attribution, run,
in this exact order, against the intended database:

1. `Migrationsss/2026-09-17_AddHouseholdsAndHealthEvents.sql` — creates
   `Households`, `HouseholdMembers`, `HouseholdInvitations`, and `HealthEvents`;
   adds `Pets.HouseholdID`, `Tasks.RecordedByUserID`,
   `VetVisits.CreatedByUserID`, and the `MedicationSchedule` adherence columns;
   and backfills one household per existing user (as Owner) with their pets
   attached. Idempotent and safe to rerun — every step is guarded and no
   existing row is deleted or overwritten destructively.
2. `Migrationsss/2026-09-17_AddHouseholdHealthStoredProcedures.sql` — creates
   the household/invitation/health-event procedures and replaces the existing
   Pet/Task/Medication/VetVisit procedures with household-aware versions
   (`CREATE OR ALTER`, safe to rerun).
3. `Migrationsss/2026-09-17_VerifyHouseholdHealthMvp.sql` — read-only. Must
   print a single `PASS` row. If it prints `FAIL` rows instead, **stop** —
   do not deploy the matching application build until every failure is
   resolved. In particular check for any pet left with a `NULL HouseholdID`
   (the script lists them by name) before proceeding; that would mean a pet
   could not be automatically matched to its owner's household and needs a
   manual, reviewed assignment rather than a guess.

This release also needs new configuration (see `appsettings.json` for the
shape). The production values are now committed directly since none of them
are secret — `packtracker.tech` and `noreply@packtracker.tech` are public by
nature (they appear in outgoing email and DNS), and Resend's SMTP username is
always the literal string `resend`, documented publicly by Resend itself:

- `App:BaseUrl` = `https://packtracker.tech` — used to build the Accept
  Invitation link in invitation emails. Falls back to the current request's
  scheme/host if left blank, which is fine for local testing but is set
  explicitly here so links are always correct regardless of how the request
  arrived (proxy, health check, etc.).
- `Email:FromAddress` = `noreply@packtracker.tech`, `Email:FromName` =
  `Pack Tracker`. The From address must be a verified sender/domain in
  Resend or delivery will fail (see below — not done yet).
- `Email:Smtp:Host` = `smtp.resend.com`, `Port` = `587`, `EnableSsl` = `true`,
  `Username` = `resend`. No SMTP server needs to be installed on the VPS —
  the app connects out to Resend directly, so only outbound TCP 587 needs to
  be allowed from the VPS.
- `Household:InvitationExpiryDays` — optional, defaults to 7 if unset.
- `MedicationAdherence:LateAfterMinutes` / `MedicationAdherence:MissedAfterMinutes`
  — optional, default to 60 / 720 if unset.

**The one remaining secret is `Email__Smtp__Password` (the Resend API key).**
It stays out of every file in this repo. Add it directly to the VPS's
systemd unit the same way `ConnectionStrings__DefaultConnection` is already
set there — an `Environment=` line under `[Service]`:

```ini
Environment=Email__Smtp__Password=re_xxxxxxxxxxxxxxxxxxxxxxxx
```

Then:

```bash
sudo systemctl daemon-reload
sudo systemctl restart petpotty
```

(env vars use `__` as the section separator; `appsettings.json` above uses
`:` — same keys, different notation for the same configuration system.)

**Before this will actually deliver mail:** the Resend account has been
created but the sending domain has not been verified yet. In the Resend
dashboard, add `packtracker.tech` (or a `noreply` subdomain) as a domain and
add the SPF/DKIM (and recommended DMARC) records it gives you at whichever
DNS provider hosts `packtracker.tech`, then wait for Resend to show the
domain as verified. Until that's done, `IsConfigured` will be `true` (host
and From address are now set) so the app *will* attempt delivery, and Resend
will reject/bounce it — invitations still save correctly and show as
pending either way, and delivery failures are logged without ever logging
the password or message body (see `Services/SmtpEmailService.cs`).

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
