# Add Pet optional-field verification

Verified on 2026-09-11 against the local Development SQL Server database through
`http://localhost:5078` using the `qaverifyui2026` QA account.

## Regression cause and fix

Razor Pages converts submitted empty strings to CLR `null` values by default.
Passing those values to `SqlParameterCollection.AddWithValue` left SqlClient
without a parameter type, so `@breed` or `@age` could be omitted from the
`dbo.AddPet` RPC call. The stored procedure then failed with "expects parameter
... which was not supplied."

`PetService.AddPet` now creates typed `NVARCHAR` parameters and converts null
text values to `string.Empty`. `EditPet` uses the same defensive parameter
handling. `HomeModel.OnPostAddPetAsync` also reports database/creation failures
separately from failures that occur while saving an uploaded photo.

No SQL migration was changed. Supplying all eight parameters from the C# caller
fixes the failure without depending on stored-procedure defaults, and the tracked
historical `AddPet` definition references an older table name that should not be
used to infer the live procedure body.

## Manual regression cases

| Case | Submission | Observed result |
| --- | --- | --- |
| Blank Breed | Name and Type supplied; Breed empty | Pet created; success message shown; rendered edit metadata contained an empty Breed value. |
| Blank Age | Breed supplied; Age sent as an empty form value directly to `POST /Home?handler=AddPet` | Pet created; success message shown; neither creation nor photo error displayed. |
| Blank Breed and Age | Breed and Age both sent as empty form values directly to `POST /Home?handler=AddPet` | Pet created; success message shown; neither creation nor photo error displayed. |
| Populated Breed and Age, no photo | `PetService.AddPet` called against the configured development database with Breed `Mixed` and Age `3` | Pet created and read back with both populated values. |
| Edit with blank Breed | The disposable populated pet was updated through `PetService.EditPet` with a CLR-null Breed | `UpdatePet` succeeded and Breed read back as an empty value. |
| Edit with blank Age | The same pet was updated through `PetService.EditPet` with a CLR-null Age | `UpdatePet` succeeded and the pet remained readable; no missing `@age` error occurred. |

The direct POST cases intentionally bypassed the existing `calcAge()` browser
helper, which derives an Age from Birthdate and can replace a blank Age with `0`
before normal form submission. The direct request therefore exercises the
server-side CLR-null path that originally omitted `@age`.

The Edit Pet checks used a disposable record and the real development SQL
database/stored procedures. The record was deleted immediately after the checks.
The Add Pet handler tests submitted no profile photo, confirming that the normal
no-photo path still succeeds. Valid-photo creation was not repeated because the
photo branch was unchanged by this fix and no reusable QA image was present in
the workspace. The distinct database/photo error messages were verified by code
inspection of the separate `try`/`catch` stages in `OnPostAddPetAsync`.

## Build verification

Run:

```powershell
dotnet build PetPotty.csproj
```

Expected: build succeeds with no warnings or errors.
