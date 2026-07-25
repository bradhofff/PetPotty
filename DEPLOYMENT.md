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
- Upload each supported document type (PDF, JPEG, PNG, DOCX), reject an
  unsupported or over-10-MB file, download as the owner, and confirm another
  user receives no document.
- Confirm dashboard pet cards only show unconfirmed medication doses and active
  vet visits from today through three calendar days ahead, with at most three
  visible rows.
