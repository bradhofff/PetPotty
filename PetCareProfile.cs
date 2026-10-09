namespace PetPotty.Models;

public sealed record CareField(string Section, string Name, string Label, int Limit);

public sealed class PetCareProfile
{
    public Dictionary<string, string?> Values { get; set; } = new();
    public DateTime? UpdatedAtUtc { get; set; }
    public string? this[string key] => Values.GetValueOrDefault(key);
    public static readonly CareField[] Fields =
    [
        new("Important", "Allergies", "Allergies", 1000),
        new("Important", "Conditions", "Ongoing conditions", 1000),
        new("Important", "CriticalInstructions", "Critical care instructions", 2000),
        new("Routine", "Food", "Food / diet", 1000),
        new("Routine", "Portion", "Portion / amount", 300),
        new("Routine", "FeedingTimes", "Usual feeding times", 500),
        new("Routine", "Restrictions", "Treats and food restrictions", 1000),
        new("Routine", "Exercise", "Walking / exercise routine", 1000),
        new("Routine", "Handling", "Behavior, handling and comfort", 2000),
        new("Routine", "AdditionalInstructions", "Additional care instructions", 2000),
        new("Contacts", "VetName", "Primary vet / clinic", 200),
        new("Contacts", "VetPhone", "Vet phone", 80),
        new("Contacts", "VetAddress", "Vet address", 500),
        new("Contacts", "EmergencyName", "Emergency contact", 200),
        new("Contacts", "EmergencyPhone", "Emergency phone", 80),
        new("Contacts", "Microchip", "Microchip number", 100),
    ];
    public static string SectionTitle(string section) => section switch {
        "Important" => "Important health information", "Routine" => "Care instructions",
        "Contacts" => "Contacts and identification", _ => throw new ArgumentException("Unknown care section.") };
    public string Preview(string section) {
        var text = string.Join(" · ", Fields.Where(f => f.Section == section && !string.IsNullOrWhiteSpace(this[f.Name]))
            .Select(f => $"{f.Label}: {this[f.Name]}"));
        return text.Length == 0 ? "Not recorded" : text.Length > 140 ? text[..140] + "…" : text;
    }
}
