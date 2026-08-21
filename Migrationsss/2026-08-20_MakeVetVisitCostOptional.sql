/*
  Allows AddVetVisit and UpdateVetVisit to accept a NULL cost.
  The VetVisits.Cost column is already nullable and its check constraint
  already permits NULL values.
*/

CREATE OR ALTER PROCEDURE dbo.AddVetVisit
    @UserID                  int,
    @PetID                   int,
    @VisitDate               date,
    @VisitTime               time(0) = NULL,
    @IsAllDay                bit = 0,
    @ClinicName              nvarchar(200) = N'',
    @VeterinarianName        nvarchar(150) = N'',
    @VisitReason             nvarchar(500),
    @VisitType               nvarchar(50),
    @Location                nvarchar(400) = N'',
    @PhoneNumber             nvarchar(50) = N'',
    @Status                  nvarchar(25) = N'Scheduled',
    @Notes                   nvarchar(4000) = N'',
    @FollowUpDate            date = NULL,
    @Cost                    decimal(10,2) = NULL,
    @IsEmergency             bit = 0,
    @PreparationInstructions nvarchar(2000) = N'',
    @ReminderAt              datetime2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS
        (SELECT 1 FROM dbo.Pets WHERE petID = @PetID AND userID = @UserID)
       OR @Status NOT IN (N'Scheduled', N'Confirmed', N'Cancelled', N'Missed', N'Rescheduled')
       OR LTRIM(RTRIM(COALESCE(@ClinicName, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@Location, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@PhoneNumber, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VisitReason, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VisitType, N''))) = N''
       OR (@FollowUpDate IS NOT NULL AND @FollowUpDate < @VisitDate)
       OR (@Cost IS NOT NULL AND @Cost < 0)
    BEGIN
        SELECT CAST(0 AS int) AS VetVisitID;
        RETURN;
    END;

    BEGIN TRANSACTION;

    INSERT dbo.VetVisits
        (PetID, VisitDate, VisitTime, IsAllDay, ClinicName, VeterinarianName,
         VisitReason, VisitType, Location, PhoneNumber, Status, Notes,
         FollowUpDate, Cost, IsEmergency, PreparationInstructions,
         CreatedAt, UpdatedAt, IsDeleted)
    VALUES
        (@PetID, @VisitDate, CASE WHEN @IsAllDay = 1 THEN NULL ELSE @VisitTime END,
         @IsAllDay, LTRIM(RTRIM(COALESCE(@ClinicName, N''))),
         LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))),
         LTRIM(RTRIM(@VisitReason)), LTRIM(RTRIM(@VisitType)),
         LTRIM(RTRIM(COALESCE(@Location, N''))),
         LTRIM(RTRIM(COALESCE(@PhoneNumber, N''))), @Status,
         LTRIM(RTRIM(COALESCE(@Notes, N''))), @FollowUpDate, @Cost,
         @IsEmergency, LTRIM(RTRIM(COALESCE(@PreparationInstructions, N''))),
         SYSDATETIME(), SYSDATETIME(), 0);

    DECLARE @VetVisitID int = CONVERT(int, SCOPE_IDENTITY());

    IF @ReminderAt IS NOT NULL
       AND @Status IN (N'Scheduled', N'Confirmed', N'Rescheduled')
    BEGIN
        INSERT dbo.VetVisitReminders
            (VetVisitID, ReminderAt, Status, CreatedAt, UpdatedAt)
        VALUES
            (@VetVisitID, @ReminderAt, N'Pending', SYSDATETIME(), SYSDATETIME());
    END;

    INSERT dbo.VetVisitHistory
        (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt)
    VALUES
        (@VetVisitID, N'Created', NULL, @Status, N'Visit record created.', SYSDATETIME());

    COMMIT TRANSACTION;
    SELECT @VetVisitID AS VetVisitID;
END;
GO

CREATE OR ALTER PROCEDURE dbo.UpdateVetVisit
    @UserID                  int,
    @VetVisitID              int,
    @PetID                   int,
    @VisitDate               date,
    @VisitTime               time(0) = NULL,
    @IsAllDay                bit = 0,
    @ClinicName              nvarchar(200) = N'',
    @VeterinarianName        nvarchar(150) = N'',
    @VisitReason             nvarchar(500),
    @VisitType               nvarchar(50),
    @Location                nvarchar(400) = N'',
    @PhoneNumber             nvarchar(50) = N'',
    @Status                  nvarchar(25),
    @Notes                   nvarchar(4000) = N'',
    @FollowUpDate            date = NULL,
    @Cost                    decimal(10,2) = NULL,
    @IsEmergency             bit = 0,
    @PreparationInstructions nvarchar(2000) = N'',
    @ReminderAt              datetime2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @OldStatus nvarchar(25);
    SELECT @OldStatus = v.Status
    FROM dbo.VetVisits v WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = v.PetID
    WHERE v.VetVisitID = @VetVisitID
      AND p.userID = @UserID
      AND v.IsDeleted = 0;

    IF @OldStatus IS NULL
       OR NOT EXISTS
          (SELECT 1 FROM dbo.Pets WHERE petID = @PetID AND userID = @UserID)
       OR @Status NOT IN (N'Scheduled', N'Confirmed', N'Completed', N'Cancelled', N'Missed', N'Rescheduled')
       OR (@OldStatus = N'Completed' AND @Status <> N'Completed')
       OR (@OldStatus <> N'Completed' AND @Status = N'Completed')
       OR LTRIM(RTRIM(COALESCE(@ClinicName, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@Location, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@PhoneNumber, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VisitReason, N''))) = N''
       OR LTRIM(RTRIM(COALESCE(@VisitType, N''))) = N''
       OR (@FollowUpDate IS NOT NULL AND @FollowUpDate < @VisitDate)
       OR (@Cost IS NOT NULL AND @Cost < 0)
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS bit) AS Succeeded;
        RETURN;
    END;

    UPDATE dbo.VetVisits
    SET PetID = @PetID,
        VisitDate = @VisitDate,
        VisitTime = CASE WHEN @IsAllDay = 1 THEN NULL ELSE @VisitTime END,
        IsAllDay = @IsAllDay,
        ClinicName = LTRIM(RTRIM(COALESCE(@ClinicName, N''))),
        VeterinarianName = LTRIM(RTRIM(COALESCE(@VeterinarianName, N''))),
        VisitReason = LTRIM(RTRIM(@VisitReason)),
        VisitType = LTRIM(RTRIM(@VisitType)),
        Location = LTRIM(RTRIM(COALESCE(@Location, N''))),
        PhoneNumber = LTRIM(RTRIM(COALESCE(@PhoneNumber, N''))),
        Status = @Status,
        Notes = LTRIM(RTRIM(COALESCE(@Notes, N''))),
        FollowUpDate = @FollowUpDate,
        Cost = @Cost,
        IsEmergency = @IsEmergency,
        PreparationInstructions = LTRIM(RTRIM(COALESCE(@PreparationInstructions, N''))),
        UpdatedAt = SYSDATETIME()
    WHERE VetVisitID = @VetVisitID;

    IF @ReminderAt IS NOT NULL
       AND @Status IN (N'Scheduled', N'Confirmed', N'Rescheduled')
    BEGIN
        MERGE dbo.VetVisitReminders WITH (HOLDLOCK) AS target
        USING (SELECT @VetVisitID AS VetVisitID) AS source
           ON target.VetVisitID = source.VetVisitID
        WHEN MATCHED THEN
            UPDATE SET ReminderAt = @ReminderAt, Status = N'Pending',
                       DisplayedAt = NULL, DismissedAt = NULL,
                       UpdatedAt = SYSDATETIME()
        WHEN NOT MATCHED THEN
            INSERT (VetVisitID, ReminderAt, Status, CreatedAt, UpdatedAt)
            VALUES (@VetVisitID, @ReminderAt, N'Pending', SYSDATETIME(), SYSDATETIME());
    END
    ELSE
    BEGIN
        UPDATE dbo.VetVisitReminders
        SET Status = N'Cancelled', UpdatedAt = SYSDATETIME()
        WHERE VetVisitID = @VetVisitID;
    END;

    DECLARE @StatusChanged bit =
        CASE WHEN @OldStatus <> @Status THEN 1 ELSE 0 END;
    INSERT dbo.VetVisitHistory
        (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt)
    VALUES
        (@VetVisitID,
         CASE WHEN @StatusChanged = 1 THEN N'Status changed' ELSE N'Updated' END,
         CASE WHEN @StatusChanged = 1 THEN @OldStatus ELSE NULL END,
         CASE WHEN @StatusChanged = 1 THEN @Status ELSE NULL END,
         CASE WHEN @StatusChanged = 1
              THEN CONCAT(N'Status changed from ', @OldStatus, N' to ', @Status, N'.')
              ELSE N'Visit details updated.' END,
         SYSDATETIME());

    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS Succeeded;
END;
GO
