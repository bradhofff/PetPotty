namespace PetPotty.Models
{
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
        public int MedID { get; set; }
        public string MedicationName { get; set; } = string.Empty;
        public string FrequencyType { get; set; } = string.Empty;
        public bool TimingDoesNotMatter { get; set; }
        public DateTime ScheduleDate { get; set; }
        public bool IsConfirmed { get; set; }
        public DateTime? ConfirmedAt { get; set; }
    }
}
