# Pet profile image deployment

The application serves `/uploads` through ASP.NET Core from
`/var/www/petpotty/uploads`. Nginx does not need an uploads location.

## Development database

1. Review `Migrations/2026-07-22_AddPetProfileImagePath.sql`. It intentionally
   starts with `USE [PetPottyDb_Dev]`.
2. Run it against the development database with the normal manual `sqlcmd`
   workflow.
3. Do not run the development script against production. For production,
   make a reviewed copy and change only the `USE` database name before the
   manual `sqlcmd` step.

## VPS filesystem

Run the setup script with the user (and optional group) from the `petpotty`
systemd unit:

```bash
sudo ./scripts/setup-pet-uploads.sh <petpotty-service-user> [petpotty-service-group]
```

The script creates `/var/www/petpotty/uploads/pets`, makes the app user its
owner, and applies mode `755`, keeping uploaded content writable by the app and
readable by the static-file middleware.

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
