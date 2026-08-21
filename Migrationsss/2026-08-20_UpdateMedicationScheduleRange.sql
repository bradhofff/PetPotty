SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [dbo].[GetScheduledMedsByPetID_AllTime]
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

CREATE OR ALTER PROCEDURE [dbo].[GetScheduledMedsByPetID_Next2Months]
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
      AND ms.scheduleDate >= @Today
      AND ms.scheduleDate < DATEADD(MONTH, 2, @Today)
    ORDER BY ms.scheduleDate ASC;
END;
GO
