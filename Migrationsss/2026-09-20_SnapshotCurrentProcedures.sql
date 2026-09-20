/* =============================================================================
   2026-09-20_SnapshotCurrentProcedures.sql

   PURPOSE
     Source-control snapshot of stored procedures that already exist in the test
     database (PetPottyDb_Test), that the application uses - or will use once its
     inline SQL is moved into procedures - and that 2026-09-20_AlignStoredProcedures.sql
     does NOT change. The Migrationsss folder either lacks these entirely or holds an
     older, pre-household version, so a new environment built only from the repo
     would not match what the app expects.

     The bodies below are the deployed definitions, copied verbatim from
     sys.sql_modules; the only differences are CREATE OR ALTER, the schema-qualified
     header and QUOTED_IDENTIFIER ON. Running it against the test database it was taken
     from therefore changes nothing functionally (it only refreshes modify_date and,
     for UpdatePetProfileImagePath, the QUOTED_IDENTIFIER flag).

   WHEN TO RUN
     Only when (re)building an environment from source control. Run it together with
     2026-09-20_AlignStoredProcedures.sql, in either order.
     Not needed for the current test database.

   HOW TO RUN
     SSMS : open, choose the database, Execute.
     sqlcmd: sqlcmd -S <server> -d <database> -E -b -i 2026-09-20_SnapshotCurrentProcedures.sql

   NOT INCLUDED (already correct in source control, identical to the deployed copy):
     DeleteVetVisit, EnsureMedicationScheduleGenerated.
   NOT INCLUDED (legacy / unused by the app - consider DROPping after review):
     GetPetByID, GetPetsByUserID (not household-aware), GetScheduledMedsByPetID (wrapper).
   ============================================================================= */

SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------- prerequisite guard: stop without changing anything if the schema is behind ---------- */
DECLARE @Missing nvarchar(max) = N'';
IF OBJECT_ID(N'dbo.Households',       N'U') IS NULL SET @Missing += N' dbo.Households';
IF OBJECT_ID(N'dbo.HouseholdMembers', N'U') IS NULL SET @Missing += N' dbo.HouseholdMembers';
IF OBJECT_ID(N'dbo.HealthEvents',     N'U') IS NULL SET @Missing += N' dbo.HealthEvents';
IF OBJECT_ID(N'dbo.MedicationSchedule', N'U') IS NULL SET @Missing += N' dbo.MedicationSchedule';
IF COL_LENGTH(N'dbo.Pets',               N'HouseholdID')      IS NULL SET @Missing += N' Pets.HouseholdID';
IF COL_LENGTH(N'dbo.Tasks',              N'RecordedByUserID') IS NULL SET @Missing += N' Tasks.RecordedByUserID';
IF COL_LENGTH(N'dbo.MedicationSchedule', N'DoseStatus')       IS NULL SET @Missing += N' MedicationSchedule.DoseStatus';
IF COL_LENGTH(N'dbo.Medications',        N'TimingDoesNotMatter') IS NULL SET @Missing += N' Medications.TimingDoesNotMatter';
IF LEN(@Missing) > 0
BEGIN
    EXEC sys.sp_set_session_context @key = N'SnapshotProcedures_Aborted', @value = 1;
    RAISERROR(N'Prerequisite schema objects are missing:%s. Apply the earlier Migrationsss scripts first. NOTHING WAS CHANGED.', 16, 1, @Missing);
    SET NOEXEC ON;
END;
GO

-- [SNAPSHOT] DeletePetByPetID  -  PetService.DeletePet (inline today)
-- Owner only, matching ManagePets. QUOTED_IDENTIFIER is ON, so the workaround in PetService.DeletePet ("legacy procedure was created with QUOTED_IDENTIFIER OFF") no longer applies. Returns Succeeded.
-- Deleting a pet cascades its medications/tasks/vet visits, so it requires
-- Owner (stricter than the Owner+Member tier used to add/edit a pet).
CREATE OR ALTER PROCEDURE dbo.DeletePetByPetID
    @UserID INT, @HouseholdID INT, @petID INT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS
        (
            SELECT 1 FROM dbo.Pets p WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN dbo.HouseholdMembers hm
                ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
               AND hm.Status = N'Active' AND hm.Role = N'Owner'
            WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID
        )
        BEGIN
            ROLLBACK;
            SELECT CAST(0 AS BIT) AS Succeeded;
            RETURN;
        END;

        DELETE FROM MedicationSchedule WHERE medID IN (SELECT medID FROM Medications WHERE petID = @petID);
        DELETE FROM Medications WHERE petID = @petID;
        DELETE FROM Tasks WHERE petID = @petID;
        DELETE FROM VetVisits WHERE petID = @petID;
        DELETE FROM Pets WHERE petID = @petID;
        COMMIT;
        SELECT CAST(1 AS BIT) AS Succeeded;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH
END;
GO

-- [SNAPSHOT] AddTaskByPetID  -  PetService.AddTask (inline today)
-- Any active member (RecordCare). Returns Succeeded.
-- ============================================================
-- TASK PROCEDURES (household-aware + RecordedByUserID attribution)
-- ============================================================

CREATE OR ALTER PROCEDURE dbo.AddTaskByPetID
    @UserID INT, @HouseholdID INT, @petID INT,
    @taskType VARCHAR(50), @notes VARCHAR(255), @createdAt DATETIME, @RecordedByUserID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Pets p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
           AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
        WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    INSERT INTO Tasks (petID, taskType, notes, createdAt, RecordedByUserID)
    VALUES (@petID, @taskType, @notes, @createdAt, @RecordedByUserID);

    COMMIT TRANSACTION;
    SELECT CAST(1 AS BIT) AS Succeeded;
END;
GO

-- [SNAPSHOT] UpdateTaskByID  -  PetService.UpdateTask
-- Any active member (RecordCare).
CREATE OR ALTER PROCEDURE dbo.UpdateTaskByID
    @UserID INT, @HouseholdID INT, @taskID INT,
    @taskType NVARCHAR(50), @notes NVARCHAR(255), @createdAt DATETIME
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    UPDATE t
    SET taskType = @taskType, notes = @notes, createdAt = @createdAt
    FROM Tasks t WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = t.petID
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
       AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
    WHERE t.taskID = @taskID AND p.HouseholdID = @HouseholdID;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [SNAPSHOT] DeleteTaskByTaskID  -  PetService.DeleteTask
-- Any active member (RecordCare).
CREATE OR ALTER PROCEDURE dbo.DeleteTaskByTaskID
    @UserID INT, @HouseholdID INT, @taskID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    DELETE t
    FROM Tasks t WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = t.petID
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
       AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
    WHERE t.taskID = @taskID AND p.HouseholdID = @HouseholdID;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [SNAPSHOT] UpdatePetProfileImagePath  -  PetService.UpdatePetProfileImagePath
-- No authorization inside: the caller wraps it in OwnedRecordCommand. The repo copy (2026-07-22) targets dbo.Pet, a table that no longer exists, and this deployed copy was built with QUOTED_IDENTIFIER OFF; this snapshot re-creates the same body with it ON.

CREATE OR ALTER PROCEDURE dbo.UpdatePetProfileImagePath
    @petID INT,
    @ProfileImagePath NVARCHAR(255) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.Pets
    SET ProfileImagePath = @ProfileImagePath
    WHERE petID = @petID;
END;
GO

-- [SNAPSHOT] AddMedication  -  MedicationService.AddMedication
-- Owner/Member (ManageCarePlans). The repo copies (2026-08-05) have the pre-household signature.
CREATE OR ALTER PROCEDURE dbo.AddMedication
    @UserID INT, @HouseholdID INT,
    @petID INT, @medicationName NVARCHAR(100), @dosage NVARCHAR(100),
    @frequencyType NVARCHAR(50), @frequencyInterval INT = NULL,
    @startDate DATETIME2(0), @endDate DATETIME2(0) = NULL,
    @notes NVARCHAR(255), @TimingDoesNotMatter BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Pets p
        INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
           AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
        WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID
    )
    BEGIN
        SELECT CAST(0 AS INT) AS medID;
        RETURN;
    END;

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
        (petID, medicationName, dosage, frequencyType, frequencyInterval, TimingDoesNotMatter, startDate, endDate, notes)
    OUTPUT INSERTED.medID INTO @medIDTable
    VALUES
        (@petID, @medicationName, @dosage, @frequencyType, ISNULL(@frequencyInterval, 1),
         @TimingDoesNotMatter, @startDate, @endDate, @notes);

    DECLARE @medID int = (SELECT TOP (1) medID FROM @medIDTable);
    EXEC dbo.EnsureMedicationScheduleGenerated @medID = @medID;
    SELECT @medID AS medID;
END;
GO

-- [SNAPSHOT] UpdateMedication  -  MedicationService.UpdateMedication
-- Owner/Member (ManageCarePlans). The repo copies have the pre-household signature.
CREATE OR ALTER PROCEDURE dbo.UpdateMedication
    @UserID INT, @HouseholdID INT,
    @medID INT, @medicationName NVARCHAR(100), @dosage NVARCHAR(100),
    @frequencyType NVARCHAR(50), @frequencyInterval INT = NULL,
    @startDate DATETIME2(0), @endDate DATETIME2(0) = NULL,
    @notes NVARCHAR(255), @TimingDoesNotMatter BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Medications m
        INNER JOIN dbo.Pets p ON p.petID = m.petID
        INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
           AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
        WHERE m.medID = @medID AND p.HouseholdID = @HouseholdID
    )
    BEGIN
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    IF UPPER(LTRIM(RTRIM(@frequencyType))) = N'HOURLY'
        SET @TimingDoesNotMatter = 0;

    IF @TimingDoesNotMatter = 1
    BEGIN
        SET @startDate = CONVERT(datetime2(0), CONVERT(date, @startDate));
        IF @endDate IS NOT NULL
            SET @endDate = CONVERT(datetime2(0), CONVERT(date, @endDate));
    END;

    DECLARE @oldStart datetime2(0), @oldEnd datetime2(0), @oldFrequency nvarchar(50),
            @oldInterval int, @oldTimingDoesNotMatter bit;

    SELECT @oldStart = startDate, @oldEnd = endDate, @oldFrequency = frequencyType,
           @oldInterval = frequencyInterval, @oldTimingDoesNotMatter = TimingDoesNotMatter
    FROM dbo.Medications
    WHERE medID = @medID;

    UPDATE dbo.Medications
    SET medicationName = @medicationName, dosage = @dosage, frequencyType = @frequencyType,
        frequencyInterval = ISNULL(@frequencyInterval, 1), TimingDoesNotMatter = @TimingDoesNotMatter,
        startDate = @startDate, endDate = @endDate, notes = @notes
    WHERE medID = @medID;

    IF ISNULL(@oldStart, CONVERT(datetime2(0), '19000101')) <> @startDate
       OR ISNULL(@oldEnd, CONVERT(datetime2(0), '99991231')) <> ISNULL(@endDate, CONVERT(datetime2(0), '99991231'))
       OR ISNULL(@oldFrequency, N'') <> ISNULL(@frequencyType, N'')
       OR ISNULL(@oldInterval, 1) <> ISNULL(@frequencyInterval, 1)
       OR ISNULL(@oldTimingDoesNotMatter, 0) <> @TimingDoesNotMatter
    BEGIN
        DELETE dbo.MedicationSchedule WHERE medID = @medID AND isConfirmed = 0;
        EXEC dbo.EnsureMedicationScheduleGenerated @medID = @medID;
    END;

    SELECT CAST(1 AS BIT) AS Succeeded;
END;
GO

-- [SNAPSHOT] DeleteMedicationByID  -  MedicationService.DeleteMedication
-- Owner/Member (ManageCarePlans). Not in the repo before this snapshot.
CREATE OR ALTER PROCEDURE dbo.DeleteMedicationByID
    @UserID INT, @HouseholdID INT, @medID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Medications m WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.Pets p ON p.petID = m.petID
        INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
           AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
        WHERE m.medID = @medID AND p.HouseholdID = @HouseholdID
    )
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    DELETE FROM MedicationSchedule WHERE medID = @medID;
    DELETE FROM Medications WHERE medID = @medID;

    COMMIT TRANSACTION;
    SELECT CAST(1 AS BIT) AS Succeeded;
END;
GO

-- [SNAPSHOT] ConfirmMedicationSchedule  -  MedicationService.RecordDose
-- Any active member (RecordCare). The repo copy has the pre-household signature.
-- Preserves the exact-time shift behavior and TimingDoesNotMatter date-only
-- behavior from the pre-household procedure unchanged; only the household
-- authorization check and the new adherence-column writes are added.
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
                     N'Taken', @AdministeredAtUtc, SYSUTCDATETIME(), @RecordedByUserID, @AdministrationNotes);
            END
            ELSE
            BEGIN
                UPDATE dbo.MedicationSchedule
                SET isConfirmed = 1, confirmedAt = @confirmedAt, DoseStatus = N'Taken',
                    AdministeredAtUtc = @AdministeredAtUtc, RecordedAtUtc = SYSUTCDATETIME(),
                    RecordedByUserID = @RecordedByUserID, AdministrationNotes = @AdministrationNotes
                WHERE scheduleID = @dateOnlyScheduleID;
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

        IF @shiftSeconds <> 0
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

-- [SNAPSHOT] GetScheduledMedsByPetID_AllTime  -  MedicationService.GetScheduleByPetID(showAllTime: true) (inline today)
-- Verified identical in result to the inline SQL. The repo copy (2026-09-10) has the pre-household signature.
CREATE OR ALTER PROCEDURE dbo.GetScheduledMedsByPetID_AllTime
    @UserID INT, @HouseholdID INT, @petID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Pets p INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
        WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID
    )
        RETURN;

    SELECT ms.scheduleID, m.medID, m.medicationName, m.dosage, m.frequencyType, m.TimingDoesNotMatter,
           ms.scheduleDate, ms.isConfirmed, ms.confirmedAt, ms.DoseStatus, ms.AdministeredAtUtc,
           ms.RecordedAtUtc, ms.RecordedByUserID, u.name AS RecordedByName, ms.StatusReason, ms.AdministrationNotes
    FROM dbo.MedicationSchedule ms
    INNER JOIN dbo.Medications m ON m.medID = ms.medID
    LEFT JOIN dbo.Users u ON u.userID = ms.RecordedByUserID
    WHERE m.petID = @petID
    ORDER BY ms.scheduleDate;
END;
GO

-- [SNAPSHOT] GetScheduledMedsByPetID_Next2Months  -  MedicationService.GetScheduleByPetID(showAllTime: false) (inline today)
-- Verified identical in result to the inline SQL. The repo copy has the pre-household signature.
CREATE OR ALTER PROCEDURE dbo.GetScheduledMedsByPetID_Next2Months
    @UserID INT, @HouseholdID INT, @petID INT, @Today date = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Pets p INNER JOIN dbo.HouseholdMembers hm
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
        WHERE p.petID = @petID AND p.HouseholdID = @HouseholdID
    )
        RETURN;

    SET @Today = COALESCE(@Today, CAST(SYSUTCDATETIME() AS date));

    SELECT ms.scheduleID, m.medID, m.medicationName, m.dosage, m.frequencyType, m.TimingDoesNotMatter,
           ms.scheduleDate, ms.isConfirmed, ms.confirmedAt, ms.DoseStatus, ms.AdministeredAtUtc,
           ms.RecordedAtUtc, ms.RecordedByUserID, u.name AS RecordedByName, ms.StatusReason, ms.AdministrationNotes
    FROM dbo.MedicationSchedule ms
    INNER JOIN dbo.Medications m ON m.medID = ms.medID
    LEFT JOIN dbo.Users u ON u.userID = ms.RecordedByUserID
    WHERE m.petID = @petID
      AND ms.scheduleDate < DATEADD(MONTH, 2, @Today)
      AND (ms.DoseStatus = N'Due' OR ms.scheduleDate >= @Today)
    ORDER BY ms.scheduleDate;
END;
GO

-- [SNAPSHOT] AddHealthEvent  -  HealthService.AddHealthEvent (inline today)
-- Any active member (RecordCare). Returns HealthEventID (0 = denied), like the inline INSERT.
-- ============================================================
-- HEALTH EVENT PROCEDURES
-- ============================================================

CREATE OR ALTER PROCEDURE dbo.AddHealthEvent
    @UserID INT, @HouseholdID INT, @PetID INT,
    @EventKind NVARCHAR(40), @EventType NVARCHAR(200),
    @OccurredAtUtc DATETIME2(6), @EndedAtUtc DATETIME2(6) = NULL,
    @Severity TINYINT = NULL, @Description NVARCHAR(4000) = N'',
    @PossibleTrigger NVARCHAR(1000) = N'', @AppetiteStatus NVARCHAR(60) = N'',
    @DrinkingStatus NVARCHAR(60) = N'', @RelatedMedicationID INT = NULL,
    @RecoveryStatus NVARCHAR(60) = N'', @RecoveredAtUtc DATETIME2(6) = NULL,
    @VeterinarianContacted BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @EventKind NOT IN (N'Symptom', N'Incident')
    BEGIN
        SELECT CAST(0 AS INT) AS HealthEventID;
        RETURN;
    END;

    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.Pets p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.HouseholdMembers hm WITH (UPDLOCK, HOLDLOCK)
            ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
           AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
        WHERE p.petID = @PetID AND p.HouseholdID = @HouseholdID
    )
       OR (@RelatedMedicationID IS NOT NULL AND NOT EXISTS
           (SELECT 1 FROM dbo.Medications WHERE medID = @RelatedMedicationID AND petID = @PetID))
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS INT) AS HealthEventID;
        RETURN;
    END;

    INSERT dbo.HealthEvents
        (PetID, EventKind, EventType, OccurredAtUtc, EndedAtUtc, Severity, Description,
         PossibleTrigger, AppetiteStatus, DrinkingStatus, RelatedMedicationID,
         RecoveryStatus, RecoveredAtUtc, VeterinarianContacted, CreatedByUserID)
    VALUES
        (@PetID, @EventKind, @EventType, @OccurredAtUtc, @EndedAtUtc, @Severity, @Description,
         @PossibleTrigger, @AppetiteStatus, @DrinkingStatus, @RelatedMedicationID,
         @RecoveryStatus, @RecoveredAtUtc, @VeterinarianContacted, @UserID);

    DECLARE @NewID INT = CONVERT(INT, SCOPE_IDENTITY());
    COMMIT TRANSACTION;
    SELECT @NewID AS HealthEventID;
END;
GO

-- [SNAPSHOT] UpdateHealthEvent  -  HealthService.UpdateHealthEvent (inline today)
-- Any active member (RecordCare). Returns Succeeded (the app currently checks ExecuteNonQuery() == 1).
CREATE OR ALTER PROCEDURE dbo.UpdateHealthEvent
    @UserID INT, @HouseholdID INT, @HealthEventID INT, @PetID INT,
    @EventKind NVARCHAR(40), @EventType NVARCHAR(200),
    @OccurredAtUtc DATETIME2(6), @EndedAtUtc DATETIME2(6) = NULL,
    @Severity TINYINT = NULL, @Description NVARCHAR(4000) = N'',
    @PossibleTrigger NVARCHAR(1000) = N'', @AppetiteStatus NVARCHAR(60) = N'',
    @DrinkingStatus NVARCHAR(60) = N'', @RelatedMedicationID INT = NULL,
    @RecoveryStatus NVARCHAR(60) = N'', @RecoveredAtUtc DATETIME2(6) = NULL,
    @VeterinarianContacted BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @EventKind NOT IN (N'Symptom', N'Incident')
    BEGIN
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    BEGIN TRANSACTION;

    -- The pet may be changing as part of the edit, so both the row's CURRENT
    -- pet and the NEW target pet are checked against the active household.
    UPDATE h
    SET PetID = @PetID, EventKind = @EventKind, EventType = @EventType,
        OccurredAtUtc = @OccurredAtUtc, EndedAtUtc = @EndedAtUtc, Severity = @Severity,
        Description = @Description, PossibleTrigger = @PossibleTrigger,
        AppetiteStatus = @AppetiteStatus, DrinkingStatus = @DrinkingStatus,
        RelatedMedicationID = @RelatedMedicationID, RecoveryStatus = @RecoveryStatus,
        RecoveredAtUtc = @RecoveredAtUtc, VeterinarianContacted = @VeterinarianContacted,
        UpdatedAtUtc = SYSUTCDATETIME()
    FROM dbo.HealthEvents h WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets currentPet ON currentPet.petID = h.PetID AND currentPet.HouseholdID = @HouseholdID
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = @HouseholdID AND hm.UserID = @UserID
       AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
    WHERE h.HealthEventID = @HealthEventID AND h.IsDeleted = 0
      AND EXISTS (SELECT 1 FROM dbo.Pets np WHERE np.petID = @PetID AND np.HouseholdID = @HouseholdID)
      AND (@RelatedMedicationID IS NULL OR EXISTS
          (SELECT 1 FROM dbo.Medications WHERE medID = @RelatedMedicationID AND petID = @PetID));

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [SNAPSHOT] DeleteHealthEvent  -  HealthService.DeleteHealthEvent (inline today)
-- Soft delete. Any active member (RecordCare). Returns Succeeded.
CREATE OR ALTER PROCEDURE dbo.DeleteHealthEvent
    @UserID INT, @HouseholdID INT, @HealthEventID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    UPDATE h
    SET IsDeleted = 1, DeletedAtUtc = SYSUTCDATETIME(), DeletedByUserID = @UserID, UpdatedAtUtc = SYSUTCDATETIME()
    FROM dbo.HealthEvents h WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Pets p ON p.petID = h.PetID
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
       AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
    WHERE h.HealthEventID = @HealthEventID AND p.HouseholdID = @HouseholdID AND h.IsDeleted = 0;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

-- [SNAPSHOT] GetHealthEventByID  -  HealthService.GetHealthEventByID (inline today)
-- Verified identical in result to the inline SQL.
CREATE OR ALTER PROCEDURE dbo.GetHealthEventByID
    @UserID INT, @HouseholdID INT, @HealthEventID INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT h.HealthEventID, h.PetID, p.name AS PetName, h.EventKind, h.EventType,
           h.OccurredAtUtc, h.EndedAtUtc, h.Severity, h.Description, h.PossibleTrigger,
           h.AppetiteStatus, h.DrinkingStatus, h.RelatedMedicationID,
           ISNULL(m.medicationName, N'') AS RelatedMedicationName,
           h.RecoveryStatus, h.RecoveredAtUtc, h.VeterinarianContacted,
           h.CreatedByUserID, u.name AS CreatedByName, h.CreatedAtUtc
    FROM dbo.HealthEvents h
    INNER JOIN dbo.Pets p ON p.petID = h.PetID
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
    INNER JOIN dbo.Users u ON u.userID = h.CreatedByUserID
    LEFT JOIN dbo.Medications m ON m.medID = h.RelatedMedicationID
    WHERE h.HealthEventID = @HealthEventID AND p.HouseholdID = @HouseholdID AND h.IsDeleted = 0;
END;
GO

-- [SNAPSHOT] GetHealthEvents  -  HealthService.GetHealthEvents (inline today)
-- Verified identical in result to the inline SQL (@EventKind filter is optional and unused by the app).
CREATE OR ALTER PROCEDURE dbo.GetHealthEvents
    @UserID INT, @HouseholdID INT, @PetID INT = NULL,
    @StartUtc DATETIME2(6), @EndUtc DATETIME2(6), @EventKind NVARCHAR(40) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.HouseholdMembers WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active')
        RETURN;

    SELECT h.HealthEventID, h.PetID, p.name AS PetName, h.EventKind, h.EventType,
           h.OccurredAtUtc, h.EndedAtUtc, h.Severity, h.Description, h.PossibleTrigger,
           h.AppetiteStatus, h.DrinkingStatus, h.RelatedMedicationID,
           ISNULL(m.medicationName, N'') AS RelatedMedicationName,
           h.RecoveryStatus, h.RecoveredAtUtc, h.VeterinarianContacted,
           h.CreatedByUserID, u.name AS CreatedByName, h.CreatedAtUtc
    FROM dbo.HealthEvents h
    INNER JOIN dbo.Pets p ON p.petID = h.PetID
    INNER JOIN dbo.Users u ON u.userID = h.CreatedByUserID
    LEFT JOIN dbo.Medications m ON m.medID = h.RelatedMedicationID
    WHERE p.HouseholdID = @HouseholdID AND h.IsDeleted = 0
      AND (@PetID IS NULL OR h.PetID = @PetID)
      AND (@EventKind IS NULL OR @EventKind = N'All' OR h.EventKind = @EventKind)
      AND h.OccurredAtUtc >= @StartUtc AND h.OccurredAtUtc < @EndUtc
    ORDER BY h.OccurredAtUtc DESC, h.HealthEventID DESC;
END;
GO

-- [SNAPSHOT] RenameHousehold  -  HouseholdService.RenameHousehold (inline today)
-- Owner only; validates the name. Returns Succeeded (the app currently checks ExecuteNonQuery() == 1).
CREATE OR ALTER PROCEDURE dbo.RenameHousehold
    @UserID INT,
    @HouseholdID INT,
    @NewName NVARCHAR(150)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF LEN(LTRIM(RTRIM(ISNULL(@NewName, N'')))) NOT BETWEEN 1 AND 150
    BEGIN
        SELECT CAST(0 AS BIT) AS Succeeded;
        RETURN;
    END;

    BEGIN TRANSACTION;

    UPDATE h
    SET Name = LTRIM(RTRIM(@NewName)), UpdatedAtUtc = SYSUTCDATETIME()
    FROM dbo.Households h WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.HouseholdMembers hm
        ON hm.HouseholdID = h.HouseholdID AND hm.UserID = @UserID
       AND hm.Status = N'Active' AND hm.Role = N'Owner'
    WHERE h.HouseholdID = @HouseholdID;

    DECLARE @Rows INT = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT CAST(CASE WHEN @Rows = 1 THEN 1 ELSE 0 END AS BIT) AS Succeeded;
END;
GO

/* ---------- post-check: every procedure above must exist and carry QUOTED_IDENTIFIER ON ---------- */
SET NOEXEC OFF;
GO

IF SESSION_CONTEXT(N'SnapshotProcedures_Aborted') IS NOT NULL
BEGIN
    EXEC sys.sp_set_session_context @key = N'SnapshotProcedures_Aborted', @value = NULL;
    PRINT N'Aborted: prerequisite schema objects are missing. Nothing was changed.';
    RETURN;
END;

DECLARE @Expected TABLE (Name sysname PRIMARY KEY);
INSERT @Expected (Name) VALUES
(N'DeletePetByPetID'),
(N'AddTaskByPetID'),
(N'UpdateTaskByID'),
(N'DeleteTaskByTaskID'),
(N'UpdatePetProfileImagePath'),
(N'AddMedication'),
(N'UpdateMedication'),
(N'DeleteMedicationByID'),
(N'ConfirmMedicationSchedule'),
(N'GetScheduledMedsByPetID_AllTime'),
(N'GetScheduledMedsByPetID_Next2Months'),
(N'AddHealthEvent'),
(N'UpdateHealthEvent'),
(N'DeleteHealthEvent'),
(N'GetHealthEventByID'),
(N'GetHealthEvents'),
(N'RenameHousehold');

DECLARE @Bad nvarchar(max) = N'';
SELECT @Bad = @Bad + N' ' + e.Name + CASE WHEN p.object_id IS NULL THEN N'(missing)' ELSE N'(QUOTED_IDENTIFIER OFF)' END
FROM @Expected e
LEFT JOIN sys.procedures p ON p.name = e.Name AND p.schema_id = SCHEMA_ID(N'dbo')
LEFT JOIN sys.sql_modules m ON m.object_id = p.object_id
WHERE p.object_id IS NULL OR m.uses_quoted_identifier = 0;

IF LEN(@Bad) > 0
    RAISERROR(N'2026-09-20_SnapshotCurrentProcedures did not complete cleanly:%s', 16, 1, @Bad);
ELSE
    SELECT COUNT(*) AS ProceduresVerified,
           N'All snapshot procedures present with QUOTED_IDENTIFIER ON' AS Result
    FROM @Expected;
GO
