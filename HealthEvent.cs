using System.ComponentModel.DataAnnotations;

namespace PetPotty.Models;

public static class HealthEventKinds
{
    public const string Symptom = "Symptom";
    public const string Incident = "Incident";

    public static readonly string[] All = [Symptom, Incident];
}

public sealed class HealthEventInput
{
    // 0 for a new event; set to the existing HealthEventID when editing one in place.
    public int HealthEventID { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Choose a pet.")]
    public int PetID { get; set; }

    [Required]
    public string EventKind { get; set; } = HealthEventKinds.Symptom;

    [Required(ErrorMessage = "Choose or enter what happened.")]
    [StringLength(100)]
    public string EventType { get; set; } = string.Empty;

    [Required(ErrorMessage = "Choose when this happened.")]
    [DataType(DataType.DateTime)]
    public DateTime OccurredAtLocal { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? EndedAtLocal { get; set; }

    [Range(1, 5, ErrorMessage = "Severity must be between 1 and 5.")]
    public int? Severity { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    [StringLength(500)]
    public string? PossibleTrigger { get; set; }

    [StringLength(30)]
    public string? AppetiteStatus { get; set; }

    [StringLength(30)]
    public string? DrinkingStatus { get; set; }

    public int? RelatedMedicationID { get; set; }

    [StringLength(30)]
    public string? RecoveryStatus { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? RecoveredAtLocal { get; set; }

    public bool VeterinarianContacted { get; set; }

    [Range(-840, 840)]
    public int UtcOffsetMinutes { get; set; }
}

public sealed class HealthEvent
{
    public int HealthEventID { get; set; }
    public int PetID { get; set; }
    public string PetName { get; set; } = string.Empty;
    public string EventKind { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public int? Severity { get; set; }
    public string Description { get; set; } = string.Empty;
    public string PossibleTrigger { get; set; } = string.Empty;
    public string AppetiteStatus { get; set; } = string.Empty;
    public string DrinkingStatus { get; set; } = string.Empty;
    public int? RelatedMedicationID { get; set; }
    public string RelatedMedicationName { get; set; } = string.Empty;
    public string RecoveryStatus { get; set; } = string.Empty;
    public DateTime? RecoveredAtUtc { get; set; }
    public bool VeterinarianContacted { get; set; }
    public int CreatedByUserID { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class HealthTimelineItem
{
    public string SourceType { get; set; } = string.Empty;
    public int SourceID { get; set; }
    public int PetID { get; set; }
    public string PetName { get; set; } = string.Empty;
    public DateTime EventAtUtc { get; set; }
    public DateTime EventAtLocal { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Attribution { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int? Severity { get; set; }
    public string Icon => SourceType switch
    {
        HealthEventKinds.Symptom => "🩺",
        HealthEventKinds.Incident => "⚠️",
        "Medication" => "💊",
        "VetVisit" => "🏥",
        _ => "•"
    };
}

public sealed class HealthMedicationSummary
{
    public int MedID { get; set; }
    public int PetID { get; set; }
    public string PetName { get; set; } = string.Empty;
    public string MedicationName { get; set; } = string.Empty;
    public string Dosage { get; set; } = string.Empty;
    public string FrequencyDisplay { get; set; } = string.Empty;

    // Last resolved dose (Taken / Taken late / Skipped / Missed), for a status badge + timestamp.
    public string LastDoseWhen { get; set; } = string.Empty;
    public string LastDoseStatus { get; set; } = string.Empty;

    // Next scheduled dose, still Due. NextDoseIsLate flags a Due dose whose time has passed.
    public string NextDoseWhen { get; set; } = string.Empty;
    public bool NextDoseIsLate { get; set; }
}

public sealed class DoseAdherenceSummary
{
    public int Total { get; set; }
    public int Taken { get; set; }
    public int TakenLate { get; set; }
    public int Skipped { get; set; }
    public int Missed { get; set; }
    public decimal AdherencePercent => Total == 0
        ? 0
        : Math.Round((Taken + TakenLate) * 100m / Total, 1);
}
