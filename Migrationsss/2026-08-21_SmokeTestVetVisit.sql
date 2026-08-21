/*
  Transactional live SQL Server smoke test.
  Replace 0 with an existing PetID that belongs to the account being tested.
  The inserted visit and all related history/reminders are rolled back.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @TestPetID int = 0; -- REPLACE WITH AN EXISTING PET ID
DECLARE @TestUserID int =
    (SELECT userID FROM dbo.Pets WHERE petID = @TestPetID);

IF @TestUserID IS NULL
    THROW 51001, 'Set @TestPetID to an existing pet before running this smoke test.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @Added table (VetVisitID int);
    INSERT @Added (VetVisitID)
    EXEC dbo.AddVetVisit
        @UserID = @TestUserID,
        @PetID = @TestPetID,
        @VisitDate = DATEADD(DAY, 30, CAST(GETDATE() AS date)),
        @VisitTime = NULL,
        @IsAllDay = 1,
        @ClinicName = N'Codex SQL Smoke Test Clinic',
        @VeterinarianName = N'Dr. Rollback',
        @VisitReason = N'Rollback-only database verification',
        @VisitType = N'Wellness exam',
        @Status = N'Scheduled',
        @Notes = N'Preparation / Notes smoke test',
        @FollowUpDate = NULL,
        @Cost = NULL,
        @IsEmergency = 0,
        @ReminderAt = NULL;

    DECLARE @VetVisitID int = (SELECT TOP (1) VetVisitID FROM @Added);
    IF COALESCE(@VetVisitID, 0) = 0
        THROW 51002, 'AddVetVisit rejected the simplified input.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.VetVisits
        WHERE VetVisitID = @VetVisitID
          AND Cost IS NULL
          AND Location = N''
          AND PhoneNumber = N''
          AND PreparationInstructions = N''
          AND Notes = N'Preparation / Notes smoke test'
    )
        THROW 51003, 'The added visit did not store the simplified values correctly.', 1;

    DECLARE @Updated table (Succeeded bit);
    INSERT @Updated (Succeeded)
    EXEC dbo.UpdateVetVisit
        @UserID = @TestUserID,
        @VetVisitID = @VetVisitID,
        @PetID = @TestPetID,
        @VisitDate = DATEADD(DAY, 31, CAST(GETDATE() AS date)),
        @VisitTime = NULL,
        @IsAllDay = 1,
        @ClinicName = N'Codex SQL Smoke Test Clinic',
        @VeterinarianName = N'Dr. Rollback',
        @VisitReason = N'Updated rollback-only database verification',
        @VisitType = N'Wellness exam',
        @Status = N'Confirmed',
        @Notes = N'Updated Preparation / Notes smoke test',
        @FollowUpDate = NULL,
        @Cost = NULL,
        @IsEmergency = 0,
        @ReminderAt = NULL;

    IF NOT EXISTS (SELECT 1 FROM @Updated WHERE Succeeded = 1)
        THROW 51004, 'UpdateVetVisit rejected the simplified input.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.VetVisits
        WHERE VetVisitID = @VetVisitID
          AND Status = N'Confirmed'
          AND Cost IS NULL
          AND Notes = N'Updated Preparation / Notes smoke test'
    )
        THROW 51005, 'The updated visit did not store the simplified values correctly.', 1;

    ROLLBACK TRANSACTION;
    SELECT N'PASS' AS Result,
           N'AddVetVisit and UpdateVetVisit passed; all smoke-test rows were rolled back.' AS Details;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
