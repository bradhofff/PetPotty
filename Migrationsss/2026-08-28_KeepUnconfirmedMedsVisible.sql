SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

/*
  Bug: GetScheduledMedsByPetID_Next2Months required scheduleDate >= @Today,
  so an unconfirmed dose from a prior day silently dropped out of the result
  set once the day rolled over -- even though it was never confirmed. That
  made the "Care reminders" notif on the pet card (Home.cshtml.cs, which
  filters on !IsConfirmed) disappear on its own instead of staying until the
  user actually confirms it.

  Fix: keep unconfirmed rows regardless of how far in the past they are;
  only apply the forward-looking 2-month window to rows that don't need a
  reminder anymore (i.e. already confirmed).
*/
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
      AND ms.scheduleDate < DATEADD(MONTH, 2, @Today)
      AND (ms.isConfirmed = 0 OR ms.scheduleDate >= @Today)
    ORDER BY ms.scheduleDate ASC;
END;
GO
