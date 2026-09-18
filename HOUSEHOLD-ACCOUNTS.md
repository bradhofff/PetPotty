# Household accounts

Apply `Migrationsss/2026-09-13_AddHouseholdFamilyAccounts.sql` before deploying
the application build. The script is idempotent, keeps `Pets.userID` for
rolling-deployment compatibility, creates one Owner household per existing
user, and moves each existing pet into that user's household.

The migration finishes with verification result sets. All three
`FailureCount` values must be zero. Then run the read-only
`Migrationsss/2026-09-13_VerifyHouseholdFamilyAccounts.sql` script and compare
the final entity totals with the pre-deployment counts before allowing
application traffic.

## Email configuration

Invitation email uses SMTP through `IEmailService`. Store production values in
environment variables or the deployment secret store; do not commit them.

```text
App__BaseUrl=https://your-petpotty-host.example
Email__FromAddress=notifications@your-domain.example
Email__FromName=PetPotty
Email__Smtp__Host=smtp-provider.example
Email__Smtp__Port=587
Email__Smtp__EnableSsl=true
Email__Smtp__Username=deployment-secret
Email__Smtp__Password=deployment-secret
HouseholdInvitations__ExpirationDays=7
```

If SMTP is absent or delivery fails, the invitation remains Pending and the UI
reports that it was not delivered. Resend rotates the secure token, extends the
expiry, and invalidates the old link.

## Role behavior

- Owner: household/member settings, pet management, care plans, and care logs.
- Member: care-plan management and normal care/health records.
- Caregiver: view pets and health records, record normal care, symptoms, and
  incidents, and complete or correct medication administrations.

Owner removal and ownership transfer are intentionally not available in this
release, so a household cannot be left without an owner.

## Deployment smoke checks

1. Back up the production database and record entity row counts.
2. Run both SQL scripts; confirm every migration failure count is zero and the
   read-only verification script passes.
3. Log in as an existing user and confirm every prior pet and related record is
   still present.
4. Confirm the existing user is Owner of the generated personal household.
5. Invite a Member and confirm the pending row, email, role, and expiry.
6. Invite a Caregiver and confirm the pending row, email, role, and expiry.
7. Attempt household-management POSTs as Member and confirm they are denied.
8. Attempt the same POSTs as Caregiver and confirm they are denied.
9. Confirm the composite membership key prevents a duplicate membership.
10. Confirm a second active invitation for the same household/email is rejected.
11. Accept an invitation using an existing matching-email account.
12. Register from an invitation, log in, and accept it without a duplicate user.
13. Try the invitation with a different signed-in email and confirm rejection.
14. Confirm expired and revoked invitations are rejected with distinct messages.
15. Reload/double-submit an accepted link and confirm no duplicate membership.
16. Change a pet/record ID to one from another household and confirm a 404 or
    denied operation across pets, medications, health, vet records, photos, and reports.
17. Record potty, feeding, activity, medication, symptom, incident, and vet
    activity as different users; confirm the server records the signed-in actor.
18. Confirm historical rows with null attribution render without a fabricated name.
19. Switch repeatedly between two households and confirm no selected-pet session
    state or page data leaks across the active boundary.
20. Confirm Owner cannot remove/demote themselves, then check Profile,
    invitation, medication, health, and vet pages at phone widths and in dark mode.

The repository has no unit/integration test project. The solution build and SQL
`PARSEONLY` verification are the automated checks available without mutating a
configured database. The existing HTTP audit scripts can be extended for a
disposable migrated database in follow-up work.

## Existing authentication limitation

This feature preserves the application's current session authentication so it
does not break existing accounts. Passwords are still stored and compared by
the legacy authentication code. Migrate them to ASP.NET Core Identity or a
slow password hash in a dedicated authentication change.
