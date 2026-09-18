using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages;

public sealed class ReportsModel(
    IPetService petService,
    IMedicationService medicationService,
    IVetVisitService vetVisitService,
    IHealthService healthService,
    IUserTimeZoneService timeZone) : PageModel
{
    [BindProperty] public int SelectedPetID { get; set; }
    [BindProperty] public string? Period { get; set; } = "30";
    [BindProperty] public DateTime? CustomFrom { get; set; }
    [BindProperty] public DateTime? CustomTo { get; set; }
    [BindProperty] public string QuestionsForVet { get; set; } = string.Empty;

    public List<Pet> Pets { get; set; } = [];
    public Pet? SelectedPet { get; set; }
    public List<Medication> CurrentMedications { get; set; } = [];
    public List<MedSchedule> DoseRecords { get; set; } = [];
    public List<HealthEvent> HealthEvents { get; set; } = [];
    public List<VetVisit> VetVisits { get; set; } = [];
    public DoseAdherenceSummary Adherence { get; set; } = new();
    public DateTime RangeStartLocal { get; set; }
    public DateTime RangeEndLocal { get; set; }
    public int UtcOffsetMinutes { get; set; }
    public string? ReportError { get; set; }

    public string RangeLabel => $"{RangeStartLocal:MMMM d, yyyy} – {RangeEndLocal:MMMM d, yyyy}";

    public IActionResult OnGet(int? petID)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");

        Pets = petService.GetPetsByUser(userID);
        SelectedPetID = petID ?? Pets.FirstOrDefault()?.PetID ?? 0;
        if (petID.HasValue && petService.GetPetByID(userID, petID.Value) == null)
            return NotFound();
        return LoadReport(userID);
    }

    public IActionResult OnPostGenerate()
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");

        Pets = petService.GetPetsByUser(userID);
        if (petService.GetPetByID(userID, SelectedPetID) == null)
            return NotFound();
        return LoadReport(userID);
    }

    public DateTime Local(DateTime utc) => timeZone.ToLocal(utc, UtcOffsetMinutes);

    private IActionResult LoadReport(int userID)
    {
        if (Pets.Count == 0)
            Pets = petService.GetPetsByUser(userID);
        if (SelectedPetID == 0)
            return Page();

        SelectedPet = petService.GetPetByID(userID, SelectedPetID);
        if (SelectedPet == null)
            return NotFound();

        UtcOffsetMinutes = timeZone.GetUtcOffsetMinutes(Request);
        Period = string.IsNullOrWhiteSpace(Period) ? "30" : Period;
        var today = timeZone.ToLocal(DateTime.UtcNow, UtcOffsetMinutes).Date;
        if (Period == "90")
        {
            RangeStartLocal = today.AddDays(-89);
            RangeEndLocal = today;
        }
        else if (string.Equals(Period, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            if (!CustomFrom.HasValue || !CustomTo.HasValue || CustomFrom.Value.Date > CustomTo.Value.Date)
            {
                ReportError = "Choose a valid custom start and end date.";
                RangeStartLocal = CustomFrom?.Date ?? today.AddDays(-29);
                RangeEndLocal = CustomTo?.Date ?? today;
                return Page();
            }
            RangeStartLocal = CustomFrom.Value.Date;
            RangeEndLocal = CustomTo.Value.Date;
        }
        else
        {
            Period = "30";
            RangeStartLocal = today.AddDays(-29);
            RangeEndLocal = today;
        }

        CustomFrom ??= RangeStartLocal;
        CustomTo ??= RangeEndLocal;
        var rangeEndExclusive = RangeEndLocal.AddDays(1);
        var startUtc = timeZone.ToUtc(RangeStartLocal, UtcOffsetMinutes);
        var endUtc = timeZone.ToUtc(rangeEndExclusive, UtcOffsetMinutes);

        CurrentMedications = medicationService.GetMedicationsByPetID(userID, SelectedPetID)
            .Where(medication => !medication.EndDate.HasValue || medication.EndDate.Value.Date >= today)
            .OrderBy(medication => medication.MedicationName)
            .ToList();
        DoseRecords = medicationService.GetScheduleByPetID(userID, SelectedPetID, true, UtcOffsetMinutes)
            .Where(dose => dose.ScheduleDate >= RangeStartLocal
                && dose.ScheduleDate < rangeEndExclusive
                && dose.EffectiveStatus != MedicationDoseStatuses.Due)
            .OrderByDescending(dose => dose.ScheduleDate)
            .ToList();
        Adherence = new DoseAdherenceSummary
        {
            Total = DoseRecords.Count,
            Taken = DoseRecords.Count(dose => dose.EffectiveStatus == MedicationDoseStatuses.Taken),
            TakenLate = DoseRecords.Count(dose => dose.EffectiveStatus == MedicationDoseStatuses.TakenLate),
            Skipped = DoseRecords.Count(dose => dose.EffectiveStatus == MedicationDoseStatuses.Skipped),
            Missed = DoseRecords.Count(dose => dose.EffectiveStatus == MedicationDoseStatuses.Missed)
        };
        HealthEvents = healthService.GetHealthEvents(userID, SelectedPetID, startUtc, endUtc);
        VetVisits = vetVisitService.GetVisits(userID, SelectedPetID)
            .Where(visit => visit.VisitDate.Date >= RangeStartLocal
                && visit.VisitDate.Date < rangeEndExclusive)
            .OrderByDescending(visit => visit.VisitDate)
            .ToList();
        return Page();
    }

    private bool TryGetUserID(out int userID) =>
        int.TryParse(HttpContext.Session.GetString("userID"), out userID);
}
