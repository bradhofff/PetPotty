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
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string Notes { get; set; } = string.Empty;

        // Display helper — "Every 1 daily", "Every 8 hourly" etc
        public string FrequencyDisplay =>
            FrequencyInterval.HasValue
                ? $"Every {FrequencyInterval} {FrequencyType.ToLower()}"
                : FrequencyType;
    }

    public class MedSchedule
    {
        public int MedID { get; set; }
        public string MedicationName { get; set; } = string.Empty;
        public DateTime ScheduleDate { get; set; }
        public bool IsConfirmed { get; set; }
        public DateTime? ConfirmedAt { get; set; }
    }
}
