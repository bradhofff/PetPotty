/*
  Adds calendar-day medication scheduling while preserving exact-time and
  hourly occurrence behavior. Safe to run more than once on SQL Server.
*/
SET XACT_ABORT ON;

IF COL_LENGTH(N'dbo.Medications', N'TimingDoesNotMatter') IS NULL
BEGIN
    ALTER TABLE dbo.Medications ADD TimingDoesNotMatter bit NULL;
END;
GO

UPDATE dbo.Medications
SET TimingDoesNotMatter =
    CASE
        WHEN UPPER(LTRIM(RTRIM(ISNULL(frequencyType, N'')))) IN
             (N'DAILY', N'WEEKLY', N'MONTHLY') THEN 1
        ELSE 0
    END
WHERE TimingDoesNotMatter IS NULL;

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Medications')
      AND name = N'TimingDoesNotMatter'
      AND is_nullable = 1
)
BEGIN
    ALTER TABLE dbo.Medications
        ALTER COLUMN TimingDoesNotMatter bit NOT NULL;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.default_constraints AS dc
    INNER JOIN sys.columns AS c
        ON c.object_id = dc.parent_object_id
       AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.Medications')
      AND c.name = N'TimingDoesNotMatter'
)
BEGIN
    ALTER TABLE dbo.Medications
        ADD CONSTRAINT DF_Medications_TimingDoesNotMatter
            DEFAULT (0) FOR TimingDoesNotMatter;
END;
GO

CREATE OR ALTER PROCEDURE dbo.GetMedicationsByPetID
    @petID int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        medID,
        medicationName,
        dosage,
        frequencyType,
        frequencyInterval,
        TimingDoesNotMatter,
        startDate,
        endDate,
        notes
    FROM dbo.Medications
    WHERE petID = @petID
    ORDER BY startDate DESC;
END;
GO

CREATE OR ALTER PROCEDURE dbo.EnsureMedicationScheduleGenerated
    @medID int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE
        @frequencyType nvarchar(50),
        @frequencyInterval int,
        @timingDoesNotMatter bit,
        @startDate datetime2(0),
        @endDate datetime2(0),
        @nextDate datetime2(0),
        @generateUntil datetime2(0);

    SELECT
        @frequencyType = frequencyType,
        @frequencyInterval = CASE
            WHEN ISNULL(frequencyInterval, 1) < 1 THEN 1
            ELSE ISNULL(frequencyInterval, 1)
        END,
        @timingDoesNotMatter = TimingDoesNotMatter,
        @startDate = startDate,
        @endDate = endDate
    FROM dbo.Medications
    WHERE medID = @medID;

    IF @startDate IS NULL
        RETURN;

    IF UPPER(LTRIM(RTRIM(ISNULL(@frequencyType, N'')))) = N'HOURLY'
        SET @timingDoesNotMatter = 0;

    IF @timingDoesNotMatter = 1
    BEGIN
        SET @startDate = CONVERT(datetime2(0), CONVERT(date, @startDate));
        IF @endDate IS NOT NULL
            SET @endDate = CONVERT(datetime2(0), CONVERT(date, @endDate));
    END;

    SET @generateUntil =
        CASE UPPER(LTRIM(RTRIM(ISNULL(@frequencyType, N''))))
            WHEN N'HOURLY'  THEN DATEADD(DAY, 30, SYSDATETIME())
            WHEN N'DAILY'   THEN DATEADD(MONTH, 6, SYSDATETIME())
            WHEN N'WEEKLY'  THEN DATEADD(MONTH, 12, SYSDATETIME())
            WHEN N'MONTHLY' THEN DATEADD(MONTH, 24, SYSDATETIME())
            ELSE DATEADD(MONTH, 6, SYSDATETIME())
        END;

    IF @endDate IS NOT NULL AND @endDate < @generateUntil
        SET @generateUntil = @endDate;

    SET @nextDate = @startDate;

    WHILE @nextDate <= @generateUntil
    BEGIN
        IF NOT EXISTS
        (
            SELECT 1
            FROM dbo.MedicationSchedule
            WHERE medID = @medID
              AND
              (
                  (@timingDoesNotMatter = 1
                   AND CONVERT(date, scheduleDate) = CONVERT(date, @nextDate))
                  OR
                  (@timingDoesNotMatter = 0 AND scheduleDate = @nextDate)
              )
        )
        BEGIN
            INSERT dbo.MedicationSchedule (medID, scheduleDate, isConfirmed)
            VALUES (@medID, @nextDate, 0);
        END;

        SET @nextDate =
            CASE UPPER(LTRIM(RTRIM(ISNULL(@frequencyType, N''))))
                WHEN N'HOURLY'  THEN DATEADD(HOUR, @frequencyInterval, @nextDate)
                WHEN N'DAILY'   THEN DATEADD(DAY, @frequencyInterval, @nextDate)
                WHEN N'WEEKLY'  THEN DATEADD(WEEK, @frequencyInterval, @nextDate)
                WHEN N'MONTHLY' THEN DATEADD(MONTH, @frequencyInterval, @nextDate)
                ELSE DATEADD(DAY, @frequencyInterval, @nextDate)
            END;
    END;
END;
GO

CREATE OR ALTER PROCEDURE dbo.AddMedication
    @petID int,
    @medicationName nvarchar(100),
    @dosage nvarchar(100),
    @frequencyType nvarchar(50),
    @frequencyInterval int = NULL,
    @startDate datetime2(0),
    @endDate datetime2(0) = NULL,
    @notes nvarchar(255),
    @TimingDoesNotMatter bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF UPPER(LTRIM(RTRIM(@frequencyType))) = N'HOURLY'
        SET @TimingDoesNotMatter = 0;

    IF @TimingDoesNotMatter = 1
    BEGIN
        SET @startDate = CONVERT(datetime2(0), CONVERT(date, @startDate));
        IF @endDate IS NOT NULL
            SET @endDate = CONVERT(datetime2(0), CONVERT(date, @endDate));
    END;

    DECLARE @medIDTable table (medID int);

    INSERT dbo.Medications
    (
        petID, medicationName, dosage, frequencyType, frequencyInterval,
        TimingDoesNotMatter, startDate, endDate, notes
    )
    OUTPUT INSERTED.medID INTO @medIDTable
    VALUES
    (
        @petID, @medicationName, @dosage, @frequencyType,
        ISNULL(@frequencyInterval, 1), @TimingDoesNotMatter,
        @startDate, @endDate, @notes
    );

    DECLARE @medID int = (SELECT TOP (1) medID FROM @medIDTable);
    EXEC dbo.EnsureMedicationScheduleGenerated @medID = @medID;
    SELECT @medID;
END;
GO

CREATE OR ALTER PROCEDURE dbo.UpdateMedication
    @medID int,
    @medicationName nvarchar(100),
    @dosage nvarchar(100),
    @frequencyType nvarchar(50),
    @frequencyInterval int = NULL,
    @startDate datetime2(0),
    @endDate datetime2(0) = NULL,
    @notes nvarchar(255),
    @TimingDoesNotMatter bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF UPPER(LTRIM(RTRIM(@frequencyType))) = N'HOURLY'
        SET @TimingDoesNotMatter = 0;

    IF @TimingDoesNotMatter = 1
    BEGIN
        SET @startDate = CONVERT(datetime2(0), CONVERT(date, @startDate));
        IF @endDate IS NOT NULL
            SET @endDate = CONVERT(datetime2(0), CONVERT(date, @endDate));
    END;

    DECLARE
        @oldStart datetime2(0),
        @oldEnd datetime2(0),
        @oldFrequency nvarchar(50),
        @oldInterval int,
        @oldTimingDoesNotMatter bit;

    SELECT
        @oldStart = startDate,
        @oldEnd = endDate,
        @oldFrequency = frequencyType,
        @oldInterval = frequencyInterval,
        @oldTimingDoesNotMatter = TimingDoesNotMatter
    FROM dbo.Medications
    WHERE medID = @medID;

    UPDATE dbo.Medications
    SET medicationName = @medicationName,
        dosage = @dosage,
        frequencyType = @frequencyType,
        frequencyInterval = ISNULL(@frequencyInterval, 1),
        TimingDoesNotMatter = @TimingDoesNotMatter,
        startDate = @startDate,
        endDate = @endDate,
        notes = @notes
    WHERE medID = @medID;

    IF ISNULL(@oldStart, CONVERT(datetime2(0), '19000101')) <> @startDate
       OR ISNULL(@oldEnd, CONVERT(datetime2(0), '99991231'))
            <> ISNULL(@endDate, CONVERT(datetime2(0), '99991231'))
       OR ISNULL(@oldFrequency, N'') <> ISNULL(@frequencyType, N'')
       OR ISNULL(@oldInterval, 1) <> ISNULL(@frequencyInterval, 1)
       OR ISNULL(@oldTimingDoesNotMatter, 0) <> @TimingDoesNotMatter
    BEGIN
        DELETE dbo.MedicationSchedule
        WHERE medID = @medID AND isConfirmed = 0;

        EXEC dbo.EnsureMedicationScheduleGenerated @medID = @medID;
    END;
END;
GO

CREATE OR ALTER PROCEDURE dbo.ConfirmMedicationSchedule
    @medID int,
    @logDate datetime2(0),
    @confirmedAt datetime2(0)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

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
                    (medID, scheduleDate, isConfirmed, confirmedAt)
                VALUES
                    (@medID, CONVERT(datetime2(0), CONVERT(date, @logDate)), 1, @confirmedAt);
            END
            ELSE
            BEGIN
                UPDATE dbo.MedicationSchedule
                SET isConfirmed = 1,
                    confirmedAt = @confirmedAt
                WHERE scheduleID = @dateOnlyScheduleID;
            END;

            COMMIT TRANSACTION;
            RETURN;
        END;

        DECLARE @shiftSeconds int = DATEDIFF(SECOND, @logDate, @confirmedAt);
        DECLARE @nextConfirmedDate datetime2(0);

        SELECT @nextConfirmedDate = MIN(scheduleDate)
        FROM dbo.MedicationSchedule WITH (UPDLOCK, HOLDLOCK)
        WHERE medID = @medID
          AND scheduleDate > @logDate
          AND isConfirmed = 1;

        IF EXISTS
        (
            SELECT 1
            FROM dbo.MedicationSchedule WITH (UPDLOCK, HOLDLOCK)
            WHERE medID = @medID AND scheduleDate = @logDate
        )
        BEGIN
            UPDATE dbo.MedicationSchedule
            SET isConfirmed = 1,
                confirmedAt = @confirmedAt
            WHERE medID = @medID AND scheduleDate = @logDate;
        END
        ELSE
        BEGIN
            INSERT dbo.MedicationSchedule
                (medID, scheduleDate, isConfirmed, confirmedAt)
            VALUES
                (@medID, @logDate, 1, @confirmedAt);
        END;

        IF @shiftSeconds <> 0
        BEGIN
            DECLARE @RowsToShift table
            (
                scheduleID int PRIMARY KEY,
                oldScheduleDate datetime2(0) NOT NULL,
                newScheduleDate datetime2(0) NOT NULL
            );

            INSERT @RowsToShift (scheduleID, oldScheduleDate, newScheduleDate)
            SELECT
                ms.scheduleID,
                ms.scheduleDate,
                DATEADD(SECOND, @shiftSeconds, ms.scheduleDate)
            FROM dbo.MedicationSchedule AS ms WITH (UPDLOCK, HOLDLOCK)
            WHERE ms.medID = @medID
              AND ms.scheduleDate > @logDate
              AND ms.isConfirmed = 0
              AND (@nextConfirmedDate IS NULL OR ms.scheduleDate < @nextConfirmedDate);

            IF EXISTS
            (
                SELECT 1
                FROM @RowsToShift AS r
                INNER JOIN dbo.MedicationSchedule AS ms WITH (UPDLOCK, HOLDLOCK)
                    ON ms.medID = @medID
                   AND ms.scheduleDate = r.newScheduleDate
                   AND ms.scheduleID <> r.scheduleID
                WHERE NOT EXISTS
                (
                    SELECT 1 FROM @RowsToShift AS r2
                    WHERE r2.scheduleID = ms.scheduleID
                )
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
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

CREATE OR ALTER PROCEDURE dbo.UnconfirmMedicationSchedule
    @medID int,
    @logDate datetime2(0)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @timingDoesNotMatter bit = 0;
    DECLARE @scheduleID int;

    SELECT @timingDoesNotMatter = CASE
        WHEN UPPER(LTRIM(RTRIM(ISNULL(frequencyType, N'')))) = N'HOURLY' THEN 0
        ELSE TimingDoesNotMatter
    END
    FROM dbo.Medications
    WHERE medID = @medID;

    SELECT TOP (1) @scheduleID = scheduleID
    FROM dbo.MedicationSchedule
    WHERE medID = @medID
      AND
      (
          (@timingDoesNotMatter = 1
           AND CONVERT(date, scheduleDate) = CONVERT(date, @logDate))
          OR
          (@timingDoesNotMatter = 0 AND scheduleDate = @logDate)
      )
    ORDER BY CASE WHEN isConfirmed = 1 THEN 0 ELSE 1 END, scheduleID;

    UPDATE dbo.MedicationSchedule
    SET isConfirmed = 0,
        confirmedAt = NULL
    WHERE scheduleID = @scheduleID;
END;
GO

CREATE OR ALTER PROCEDURE dbo.GetScheduledMedsByPetID_Next30Days
    @petID int
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Today date = CAST(GETDATE() AS date);

    SELECT
        m.medID,
        m.medicationName,
        m.frequencyType,
        m.TimingDoesNotMatter,
        ms.scheduleDate,
        ms.isConfirmed,
        ms.confirmedAt
    FROM dbo.MedicationSchedule AS ms
    INNER JOIN dbo.Medications AS m ON m.medID = ms.medID
    WHERE m.petID = @petID
      AND
      (
          (ms.scheduleDate >= @Today
           AND ms.scheduleDate < DATEADD(DAY, 30, @Today))
          OR ms.isConfirmed = 0
      )
    ORDER BY ms.scheduleDate ASC;
END;
GO

CREATE OR ALTER PROCEDURE dbo.GetScheduledMedsByPetID_AllTime
    @petID int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        m.medID,
        m.medicationName,
        m.frequencyType,
        m.TimingDoesNotMatter,
        ms.scheduleDate,
        ms.isConfirmed,
        ms.confirmedAt
    FROM dbo.MedicationSchedule AS ms
    INNER JOIN dbo.Medications AS m ON m.medID = ms.medID
    WHERE m.petID = @petID
    ORDER BY ms.scheduleDate ASC;
END;
GO

CREATE OR ALTER PROCEDURE dbo.GetScheduledMedsByPetID_Month
    @petID int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.GetScheduledMedsByPetID_Next30Days @petID = @petID;
END;
GO

CREATE OR ALTER PROCEDURE dbo.GetScheduledMedsByPetID
    @petID int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.GetScheduledMedsByPetID_AllTime @petID = @petID;
END;
GO
