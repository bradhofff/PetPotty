/*
  PackTracker medication schedule ranges.

  The default view includes today and the following 29 calendar days, plus
  overdue schedule rows that have not been confirmed. The all-time view has no
  future cutoff. Legacy procedure names remain as compatibility wrappers.
*/

CREATE OR ALTER PROCEDURE dbo.GetScheduledMedsByPetID_Next30Days
    @petID int
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Today date = CAST(GETDATE() AS date);

    SELECT
        m.medID,
        m.medicationName,
        ms.scheduleDate,
        ms.isConfirmed,
        ms.confirmedAt
    FROM dbo.MedicationSchedule AS ms
    INNER JOIN dbo.Medications AS m ON m.medID = ms.medID
    WHERE m.petID = @petID
      AND ms.scheduleDate < DATEADD(DAY, 30, @Today)
      AND (ms.scheduleDate >= @Today OR ms.isConfirmed = 0)
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
