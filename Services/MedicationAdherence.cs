namespace PetPotty.Services;

// The one place medication timing thresholds are evaluated. Both the
// Medications page and the Health timeline call through here so a dose's
// displayed status never disagrees between the two (previously each
// re-implemented this independently).
public static class MedicationAdherence
{
    // A still-"Due" row is displayed as Missed once this many minutes have
    // passed its scheduled time (or, for TimingDoesNotMatter medication,
    // this many minutes past the END of its scheduled calendar day — never
    // based on an exact clock time for those).
    public static string EffectiveStatus(
        string storedDoseStatus,
        DateTime scheduleDate,
        bool timingDoesNotMatter,
        int missedAfterMinutes,
        DateTime nowLocal)
    {
        if (!string.Equals(storedDoseStatus, "Due", StringComparison.OrdinalIgnoreCase))
            return storedDoseStatus;

        var threshold = timingDoesNotMatter
            ? scheduleDate.Date.AddDays(1).AddMinutes(missedAfterMinutes)
            : scheduleDate.AddMinutes(missedAfterMinutes);

        return nowLocal >= threshold ? "Missed" : "Due";
    }

    // Whether confirming a dose right now counts as on-time ("Taken") or
    // late ("Taken late"). TimingDoesNotMatter medication is never late —
    // it only cares which calendar day the dose happened on.
    public static string ResolveConfirmStatus(
        DateTime scheduleDate,
        DateTime confirmedAt,
        bool timingDoesNotMatter,
        int lateAfterMinutes)
    {
        if (timingDoesNotMatter)
            return "Taken";

        return confirmedAt <= scheduleDate.AddMinutes(lateAfterMinutes) ? "Taken" : "Taken late";
    }

    public static bool IsOverdue(
        string storedDoseStatus,
        DateTime scheduleDate,
        bool timingDoesNotMatter,
        DateTime nowLocal)
    {
        if (!string.Equals(storedDoseStatus, "Due", StringComparison.OrdinalIgnoreCase))
            return false;

        return timingDoesNotMatter
            ? scheduleDate.Date < nowLocal.Date
            : scheduleDate < nowLocal;
    }
}
