namespace PetPotty.Models
{
    public static class MedicationDoseStatuses
    {
        public const string Due = "Due";
        public const string Taken = "Taken";
        public const string TakenLate = "Taken late";
        public const string Skipped = "Skipped";
        public const string Missed = "Missed";

        public static readonly string[] Recordable = [Taken, Skipped, Missed];
    }

    public class Medication
    {
        public int MedID { get; set; }
        public int PetID { get; set; }
        public string MedicationName { get; set; } = string.Empty;
        public string Dosage { get; set; } = string.Empty;
        public string FrequencyType { get; set; } = string.Empty;
        public int? FrequencyInterval { get; set; }
        public bool TimingDoesNotMatter { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string Notes { get; set; } = string.Empty;

        // Display helper — "Every day", "Every 2 weeks", "Every 8 hours", etc.
        public string FrequencyDisplay
        {
            get
            {
                if (!FrequencyInterval.HasValue)
                    return FrequencyType;

                var unit = FrequencyType.Trim().ToLowerInvariant() switch
                {
                    "hourly" => "hour",
                    "daily" => "day",
                    "weekly" => "week",
                    "monthly" => "month",
                    _ => FrequencyType.Trim().ToLowerInvariant()
                };

                return FrequencyInterval.Value == 1
                    ? $"Every {unit}"
                    : $"Every {FrequencyInterval.Value} {unit}s";
            }
        }
    }

    public class MedSchedule
    {
        public int ScheduleID { get; set; }
        public int MedID { get; set; }
        public string MedicationName { get; set; } = string.Empty;
        public string Dosage { get; set; } = string.Empty;
        public string FrequencyType { get; set; } = string.Empty;
        public bool TimingDoesNotMatter { get; set; }
        public DateTime ScheduleDate { get; set; }
        public bool IsConfirmed { get; set; }
        public DateTime? ConfirmedAt { get; set; }
        public string DoseStatus { get; set; } = MedicationDoseStatuses.Due;
        public string EffectiveStatus { get; set; } = MedicationDoseStatuses.Due;
        public DateTime? AdministeredAtUtc { get; set; }
        public DateTime? RecordedAtUtc { get; set; }
        public int? RecordedByUserID { get; set; }
        public string RecordedByName { get; set; } = string.Empty;
        public string StatusReason { get; set; } = string.Empty;
        public string AdministrationNotes { get; set; } = string.Empty;

        public bool IsResolved => EffectiveStatus != MedicationDoseStatuses.Due;

        // An unresolved dose is late at its scheduled time, or after its scheduled
        // calendar day when the time of day does not matter.
        public bool IsLate(DateTime localNow) =>
            DoseStatus == MedicationDoseStatuses.Due
            && (TimingDoesNotMatter ? ScheduleDate.Date < localNow.Date : ScheduleDate < localNow);

        public string DisplayStatus(DateTime localNow)
        {
            if (DoseStatus != MedicationDoseStatuses.Due)
                return DoseStatus;
            if (IsLate(localNow))
                return "Late";

            var daysUntilDue = (ScheduleDate.Date - localNow.Date).Days;
            return daysUntilDue switch
            {
                > 3 => "Upcoming",
                > 0 => $"Due in {daysUntilDue} {(daysUntilDue == 1 ? "day" : "days")}",
                _ => "Due today"
            };
        }
    }
}
