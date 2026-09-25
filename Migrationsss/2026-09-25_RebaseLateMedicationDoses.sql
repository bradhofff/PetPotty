-- Rebase future medication doses when a scheduled dose is given late.
CREATE OR ALTER PROCEDURE dbo.ConfirmMedicationSchedule
    @UserID INT, @HouseholdID INT,
    @medID INT, @logDate DATETIME2(0), @confirmedAt DATETIME2(0),
    @RecordedByUserID INT, @AdministeredAtUtc DATETIME2(6), @DoseStatus NVARCHAR(40),
    @AdministrationNotes NVARCHAR(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @DoseStatus NOT IN (N'Taken', N'Taken late')
        SET @DoseStatus = N'Taken';

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Medications m
        INNER JOIN dbo.Pets p ON p.petID = m.petID
        INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
           AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
        WHERE m.medID = @medID AND p.HouseholdID = @HouseholdID
    )
        RETURN;

    DECLARE @timingDoesNotMatter bit = 0;
    SELECT @timingDoesNotMatter = CASE
        WHEN UPPER(LTRIM(RTRIM(ISNULL(frequencyType, N'')))) = N'HOURLY' THEN 0
        ELSE TimingDoesNotMatter
    END
    FROM dbo.Medications
    WHERE medID = @medID;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @timingDoesNotMatter = 1
        BEGIN
            DECLARE @dateOnlyScheduleID int;

            SELECT TOP (1) @dateOnlyScheduleID = scheduleID
            FROM dbo.MedicationSchedule WITH (UPDLOCK, HOLDLOCK)
            WHERE medID = @medID
              AND CONVERT(date, scheduleDate) = CONVERT(date, @logDate)
            ORDER BY CASE WHEN isConfirmed = 0 THEN 0 ELSE 1 END, scheduleID;

            IF @dateOnlyScheduleID IS NULL
            BEGIN
                INSERT dbo.MedicationSchedule
                    (medID, scheduleDate, isConfirmed, confirmedAt, DoseStatus, AdministeredAtUtc, RecordedAtUtc, RecordedByUserID, AdministrationNotes)
                VALUES
                    (@medID, CONVERT(datetime2(0), CONVERT(date, @logDate)), 1, @confirmedAt,
                     @DoseStatus, @AdministeredAtUtc, SYSUTCDATETIME(), @RecordedByUserID, @AdministrationNotes);
            END
            ELSE
            BEGIN
                UPDATE dbo.MedicationSchedule
                SET isConfirmed = 1, confirmedAt = @confirmedAt, DoseStatus = @DoseStatus,
                    AdministeredAtUtc = @AdministeredAtUtc, RecordedAtUtc = SYSUTCDATETIME(),
                    RecordedByUserID = @RecordedByUserID, AdministrationNotes = @AdministrationNotes
                WHERE scheduleID = @dateOnlyScheduleID;
            END;

            DECLARE @shiftDays int = DATEDIFF(DAY, CONVERT(date, @logDate), CONVERT(date, @confirmedAt));
            IF @shiftDays > 0
            BEGIN
                DECLARE @nextDateOnlyConfirmed datetime2(0);
                SELECT @nextDateOnlyConfirmed = MIN(scheduleDate)
                FROM dbo.MedicationSchedule WITH (UPDLOCK, HOLDLOCK)
                WHERE medID = @medID AND scheduleDate > @logDate AND isConfirmed = 1;

                DECLARE @DateOnlyRowsToShift table
                (
                    scheduleID int PRIMARY KEY,
                    newScheduleDate datetime2(0) NOT NULL
                );

                INSERT @DateOnlyRowsToShift (scheduleID, newScheduleDate)
                SELECT ms.scheduleID, DATEADD(DAY, @shiftDays, ms.scheduleDate)
                FROM dbo.MedicationSchedule AS ms WITH (UPDLOCK, HOLDLOCK)
                WHERE ms.medID = @medID AND ms.scheduleDate > @logDate AND ms.isConfirmed = 0
                  AND ms.DoseStatus = N'Due'
                  AND (@nextDateOnlyConfirmed IS NULL OR ms.scheduleDate < @nextDateOnlyConfirmed);

                IF EXISTS
                (
                    SELECT 1 FROM @DateOnlyRowsToShift AS r
                    INNER JOIN dbo.MedicationSchedule AS ms WITH (UPDLOCK, HOLDLOCK)
                        ON ms.medID = @medID AND ms.scheduleDate = r.newScheduleDate AND ms.scheduleID <> r.scheduleID
                    WHERE NOT EXISTS (SELECT 1 FROM @DateOnlyRowsToShift AS r2 WHERE r2.scheduleID = ms.scheduleID)
                )
                    THROW 51000, 'Cannot shift medication schedule because it would create duplicate schedule dates.', 1;

                UPDATE ms SET scheduleDate = r.newScheduleDate
                FROM dbo.MedicationSchedule AS ms
                INNER JOIN @DateOnlyRowsToShift AS r ON r.scheduleID = ms.scheduleID;
            END;

            COMMIT TRANSACTION;
            RETURN;
        END;

        DECLARE @shiftSeconds int = DATEDIFF(SECOND, @logDate, @confirmedAt);
        DECLARE @nextConfirmedDate datetime2(0);

        SELECT @nextConfirmedDate = MIN(scheduleDate)
        FROM dbo.MedicationSchedule WITH (UPDLOCK, HOLDLOCK)
        WHERE medID = @medID AND scheduleDate > @logDate AND isConfirmed = 1;

        IF EXISTS (SELECT 1 FROM dbo.MedicationSchedule WITH (UPDLOCK, HOLDLOCK) WHERE medID = @medID AND scheduleDate = @logDate)
        BEGIN
            UPDATE dbo.MedicationSchedule
            SET isConfirmed = 1, confirmedAt = @confirmedAt, DoseStatus = @DoseStatus,
                AdministeredAtUtc = @AdministeredAtUtc, RecordedAtUtc = SYSUTCDATETIME(),
                RecordedByUserID = @RecordedByUserID, AdministrationNotes = @AdministrationNotes
            WHERE medID = @medID AND scheduleDate = @logDate;
        END
        ELSE
        BEGIN
            INSERT dbo.MedicationSchedule
                (medID, scheduleDate, isConfirmed, confirmedAt, DoseStatus, AdministeredAtUtc, RecordedAtUtc, RecordedByUserID, AdministrationNotes)
            VALUES
                (@medID, @logDate, 1, @confirmedAt, @DoseStatus, @AdministeredAtUtc, SYSUTCDATETIME(), @RecordedByUserID, @AdministrationNotes);
        END;

        IF @shiftSeconds > 0
        BEGIN
            DECLARE @RowsToShift table
            (
                scheduleID int PRIMARY KEY,
                oldScheduleDate datetime2(0) NOT NULL,
                newScheduleDate datetime2(0) NOT NULL
            );

            INSERT @RowsToShift (scheduleID, oldScheduleDate, newScheduleDate)
            SELECT ms.scheduleID, ms.scheduleDate, DATEADD(SECOND, @shiftSeconds, ms.scheduleDate)
            FROM dbo.MedicationSchedule AS ms WITH (UPDLOCK, HOLDLOCK)
            WHERE ms.medID = @medID
              AND ms.scheduleDate > @logDate
              AND ms.isConfirmed = 0
              AND ms.DoseStatus = N'Due'
              AND (@nextConfirmedDate IS NULL OR ms.scheduleDate < @nextConfirmedDate);

            IF EXISTS
            (
                SELECT 1
                FROM @RowsToShift AS r
                INNER JOIN dbo.MedicationSchedule AS ms WITH (UPDLOCK, HOLDLOCK)
                    ON ms.medID = @medID AND ms.scheduleDate = r.newScheduleDate AND ms.scheduleID <> r.scheduleID
                WHERE NOT EXISTS (SELECT 1 FROM @RowsToShift AS r2 WHERE r2.scheduleID = ms.scheduleID)
            )
            BEGIN
                THROW 51000, 'Cannot shift medication schedule because it would create duplicate schedule times.', 1;
            END;

            UPDATE ms
            SET scheduleDate = r.newScheduleDate
            FROM dbo.MedicationSchedule AS ms
            INNER JOIN @RowsToShift AS r ON r.scheduleID = ms.scheduleID;
        END;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO

