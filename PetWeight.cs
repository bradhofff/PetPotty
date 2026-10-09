using System.ComponentModel.DataAnnotations;

namespace PetPotty.Models;

public sealed class PetWeightInput : IValidatableObject
{
    [Range(0, int.MaxValue)] public int WeightEntryID { get; set; }
    [Range(1, int.MaxValue)] public int PetID { get; set; }
    [Required(ErrorMessage = "Enter a weight.")]
    [Range(typeof(decimal), "0.001", "999999.999", ErrorMessage = "Enter a weight between 0.001 and 999,999.999.")]
    public decimal? Weight { get; set; }
    [Required, RegularExpression("^(lb|kg)$", ErrorMessage = "Choose lb or kg.")]
    public string Unit { get; set; } = "lb";
    [Required(ErrorMessage = "Choose when your pet was weighed.")]
    public DateTime? MeasuredAtLocal { get; set; }
    [StringLength(1000)] public string? Notes { get; set; }
    [Range(-840, 840)] public int UtcOffsetMinutes { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Weight.HasValue && decimal.Round(Weight.Value, 3) != Weight.Value)
            yield return new("Use no more than three decimal places.", [nameof(Weight)]);
        if (MeasuredAtLocal.HasValue && UtcOffsetMinutes is >= -840 and <= 840)
        {
            // Browser offsets are UTC minus local time. Compare ticks to avoid date overflow.
            var utcTicks = MeasuredAtLocal.Value.Ticks + TimeSpan.FromMinutes(UtcOffsetMinutes).Ticks;
            if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.UtcNow.Ticks)
                yield return new("Choose a measurement time that is not in the future.", [nameof(MeasuredAtLocal)]);
        }
    }
}

public sealed class PetWeightEntry
{
    public const decimal PoundsPerKilogram = 2.2046226218487757m;
    public int WeightEntryID { get; set; }
    public int PetID { get; set; }
    public decimal Weight { get; set; }
    public string Unit { get; set; } = "lb";
    public DateTime MeasuredAtUtc { get; set; }
    public string? Notes { get; set; }
    public string RecordedBy { get; set; } = string.Empty;

    public decimal InUnit(string unit) => unit == Unit ? Weight
        : unit == "kg" ? Weight / PoundsPerKilogram : Weight * PoundsPerKilogram;
}
