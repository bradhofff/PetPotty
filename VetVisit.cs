using System.ComponentModel.DataAnnotations;

namespace PetPotty.Models
{
    public class VetVisit
    {
        public int VetVisitID { get; set; }
        public int PetID { get; set; }
        public string PetName { get; set; } = string.Empty;
        public DateTime VisitDate { get; set; }
        public TimeSpan? VisitTime { get; set; }
        public bool IsAllDay { get; set; }
        public string ClinicName { get; set; } = string.Empty;
        public string VeterinarianName { get; set; } = string.Empty;
        public string VisitReason { get; set; } = string.Empty;
        public string VisitType { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string Status { get; set; } = "Scheduled";
        public string Notes { get; set; } = string.Empty;
        public DateTime? FollowUpDate { get; set; }
        public decimal? Cost { get; set; }
        public bool IsEmergency { get; set; }
        public string PreparationInstructions { get; set; } = string.Empty;
        public string VisitSummary { get; set; } = string.Empty;
        public string Diagnosis { get; set; } = string.Empty;
        public string TreatmentProvided { get; set; } = string.Empty;
        public string VaccinationsReceived { get; set; } = string.Empty;
        public string Prescriptions { get; set; } = string.Empty;
        public string FollowUpInstructions { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int? ReminderID { get; set; }
        public DateTime? ReminderAt { get; set; }
        public string? ReminderStatus { get; set; }

        public DateTime AppointmentDateTime => VisitDate.Date + (VisitTime ?? TimeSpan.Zero);
        public bool IsUpcoming => Status is "Scheduled" or "Confirmed" or "Rescheduled"
            && VisitDate.Date >= DateTime.Today;
    }

    public class VetVisitInput
    {
        public int VetVisitID { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Choose a pet.")]
        public int PetID { get; set; }

        [DataType(DataType.Date)]
        public DateTime VisitDate { get; set; } = DateTime.Today;

        [DataType(DataType.Time)]
        public TimeSpan? VisitTime { get; set; }
        public bool IsAllDay { get; set; }

        [Required(ErrorMessage = "Clinic name is required.")]
        [StringLength(200)]
        public string ClinicName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Veterinarian is required.")]
        [StringLength(150)]
        public string VeterinarianName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Visit reason is required.")]
        [StringLength(500)]
        public string VisitReason { get; set; } = string.Empty;

        [Required]
        [StringLength(50)]
        public string VisitType { get; set; } = "Wellness exam";

        [Required(ErrorMessage = "Clinic address or location is required.")]
        [StringLength(400)]
        public string Location { get; set; } = string.Empty;

        [Required(ErrorMessage = "Clinic phone number is required.")]
        [Phone]
        [StringLength(50)]
        public string PhoneNumber { get; set; } = string.Empty;

        [Required]
        [StringLength(25)]
        public string Status { get; set; } = "Scheduled";

        [StringLength(4000)]
        public string? Notes { get; set; }

        [DataType(DataType.Date)]
        public DateTime? FollowUpDate { get; set; }

        [Required(ErrorMessage = "Cost is required.")]
        [Range(typeof(decimal), "0", "99999999.99", ErrorMessage = "Cost cannot be negative.")]
        public decimal? Cost { get; set; }
        public bool IsEmergency { get; set; }

        [StringLength(2000)]
        public string? PreparationInstructions { get; set; }
        public string ReminderChoice { get; set; } = "OneDay";

        [DataType(DataType.DateTime)]
        public DateTime? CustomReminderAt { get; set; }
    }

    public class VetClinicOption
    {
        public string ClinicName { get; set; } = string.Empty;
        public string VeterinarianName { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
    }

    public class CompleteVetVisitInput
    {
        public int VetVisitID { get; set; }

        [Required(ErrorMessage = "Add a short visit summary.")]
        [StringLength(4000)]
        public string VisitSummary { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? Diagnosis { get; set; }

        [StringLength(4000)]
        public string? TreatmentProvided { get; set; }

        [StringLength(2000)]
        public string? VaccinationsReceived { get; set; }

        [StringLength(2000)]
        public string? Prescriptions { get; set; }

        [StringLength(4000)]
        public string? FollowUpInstructions { get; set; }

        [DataType(DataType.Date)]
        public DateTime? FollowUpDate { get; set; }

        [Range(typeof(decimal), "0", "99999999.99", ErrorMessage = "Final cost cannot be negative.")]
        public decimal? FinalCost { get; set; }

        [StringLength(4000)]
        public string? AdditionalNotes { get; set; }
    }

    public class VetVisitDocument
    {
        public int VetVisitDocumentID { get; set; }
        public int VetVisitID { get; set; }
        public string DocumentType { get; set; } = "Other";
        public string DisplayName { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string StoredPath { get; set; } = string.Empty;
        public string ContentType { get; set; } = "application/octet-stream";
        public long FileSizeBytes { get; set; }
        public string Description { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    public class VetVisitHistory
    {
        public int VetVisitHistoryID { get; set; }
        public int VetVisitID { get; set; }
        public string ChangeType { get; set; } = string.Empty;
        public string? OldStatus { get; set; }
        public string? NewStatus { get; set; }
        public string Details { get; set; } = string.Empty;
        public DateTime ChangedAt { get; set; }
    }

    public class DashboardCareItem
    {
        public int PetID { get; set; }
        public DateTime DueAt { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public bool IsOverdue { get; set; }
        public bool TimingDoesNotMatter { get; set; }
    }
}
