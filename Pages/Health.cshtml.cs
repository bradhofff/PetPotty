using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages;

public sealed class HealthModel(
    IPetService petService,
    IMedicationService medicationService,
    IVetVisitService vetVisitService,
    IHealthService healthService,
    IUserTimeZoneService timeZone) : PageModel
{
    // Not query-bound: kept out of the URL (like /Medications). Set once from an incoming
    // ?petID= deep link or an explicit pet switch, then remembered in session from there.
    public int? PetID { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? EventType { get; set; } = "All";
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; } = "Newest";
    [BindProperty] public HealthEventInput NewEvent { get; set; } = new();
    [BindProperty] public HealthEventInput EditEvent { get; set; } = new();
    [BindProperty] public bool ReturnToDashboard { get; set; }

    private const string SelectedPetSessionKey = "healthSelectedPetID";

    public List<Pet> Pets { get; set; } = [];
    public List<Medication> MedicationOptions { get; set; } = [];
    public List<HealthTimelineItem> Timeline { get; set; } = [];
    // Health history grouped by category so symptoms, incidents, medication activity,
    // and vet visits each get their own labeled, collapsible cluster instead of one
    // interleaved chronological feed. Each still follows the page's date-range/sort settings.
    public List<HealthTimelineItem> SymptomTimeline { get; set; } = [];
    public List<HealthTimelineItem> IncidentTimeline { get; set; } = [];
    public List<HealthTimelineItem> MedicationTimeline { get; set; } = [];
    public List<HealthTimelineItem> VetVisitTimeline { get; set; } = [];
    public List<HealthMedicationSummary> CurrentMedications { get; set; } = [];
    public List<HealthEvent> RecentHealthEvents { get; set; } = [];
    public int RecentHealthEventCount { get; set; }
    public VetVisit? NextVetVisit { get; set; }
    public VetVisit? RecentVetVisit { get; set; }
    public DoseAdherenceSummary RecentAdherence { get; set; } = new();
    public int UtcOffsetMinutes { get; set; }
    public string? HealthError { get; set; }
    public bool OpenEventModal { get; set; }
    public bool OpenEditEventModal { get; set; }

    public static readonly string[] SourceTypes =
        ["All", HealthEventKinds.Symptom, HealthEventKinds.Incident, "Medication", "VetVisit"];

    public static readonly string[] SymptomTypes =
        ["Vomiting", "Diarrhea", "Coughing", "Limping", "Low appetite", "Low energy", "Pain", "Itching", "Other"];

    public static readonly string[] IncidentTypes =
        ["Seizure", "Vomiting", "Diarrhea", "Fall", "Injury", "Pain episode", "Breathing problem", "Allergic reaction", "Accident", "Behavioral change", "Other"];

    public IActionResult OnGet(int? petID, int? editHealthEventID)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");

        if (petID.HasValue && petService.GetPetByID(userID, petID.Value) == null)
            return NotFound();
        PetID = petID ?? GetSelectedPetIDFromSession();
        if (petID.HasValue)
            SetSelectedPetID(petID.Value);

        var result = LoadPage(userID);
        if (result != null)
            return result;

        if (editHealthEventID.HasValue)
        {
            var existing = healthService.GetHealthEventByID(userID, editHealthEventID.Value);
            if (existing != null)
            {
                EditEvent = ToInput(existing);
                OpenEditEventModal = true;
            }
        }

        NewEvent.PetID = PetID ?? (Pets.Count == 1 ? Pets[0].PetID : 0);
        NewEvent.EventKind = HealthEventKinds.Symptom;
        NewEvent.OccurredAtLocal = timeZone.ToLocal(DateTime.UtcNow, UtcOffsetMinutes);
        NewEvent.UtcOffsetMinutes = UtcOffsetMinutes;
        return Page();
    }

    public IActionResult OnPostSelectPet(int selectedPetID)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        if (selectedPetID != 0 && petService.GetPetByID(userID, selectedPetID) == null)
            return NotFound();

        SetSelectedPetID(selectedPetID == 0 ? (int?)null : selectedPetID);
        return RedirectToPage();
    }

    public IActionResult OnPostLogEvent()
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");

        PetID = NewEvent.PetID;
        NewEvent.EventKind = NormalizeKind(NewEvent.EventKind);
        if (petService.GetPetByID(userID, NewEvent.PetID) == null)
            return NotFound();

        // NewEvent and EditEvent are both [BindProperty] HealthEventInput on this page.
        // EditEvent never receives any posted fields here, and ASP.NET Core's automatic
        // validation of that untouched, still-default nested object records its failures
        // under bare keys ("PetID", "EventType") rather than an "EditEvent." prefix — so
        // ClearValidationState(nameof(EditEvent)) can't reach them. Clear everything and
        // revalidate just NewEvent from scratch, which produces only "NewEvent."-prefixed keys.
        ModelState.Clear();
        TryValidateModel(NewEvent, nameof(NewEvent));

        if (!HealthEventKinds.All.Contains(NewEvent.EventKind, StringComparer.Ordinal))
            ModelState.AddModelError("NewEvent.EventKind", "Choose symptom or incident.");
        if (NewEvent.EndedAtLocal.HasValue && NewEvent.EndedAtLocal < NewEvent.OccurredAtLocal)
            ModelState.AddModelError("NewEvent.EndedAtLocal", "End time cannot be before the event started.");
        if (NewEvent.RecoveredAtLocal.HasValue && NewEvent.RecoveredAtLocal < NewEvent.OccurredAtLocal)
            ModelState.AddModelError("NewEvent.RecoveredAtLocal", "Recovery time cannot be before the event started.");

        if (!ModelState.IsValid)
        {
            var messages = ModelState.Values
                .SelectMany(value => value.Errors)
                .Select(error => error.ErrorMessage)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct()
                .ToList();
            HealthError = messages.Count == 0
                ? "Check the highlighted fields and try again."
                : string.Join(" ", messages);
            OpenEventModal = true;
            var invalidResult = LoadPage(userID);
            return invalidResult ?? Page();
        }

        var eventID = healthService.AddHealthEvent(userID, NewEvent);
        if (eventID == 0)
            return NotFound();

        SetSelectedPetID(NewEvent.PetID);
        TempData["StatusMessage"] = $"{NewEvent.EventKind} recorded for {PetsName(userID, NewEvent.PetID)}.";
        if (ReturnToDashboard)
            return RedirectToPage("/Home");

        return RedirectToPage();
    }

    public IActionResult OnPostEditEvent()
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");

        PetID = EditEvent.PetID;
        EditEvent.EventKind = NormalizeKind(EditEvent.EventKind);
        if (petService.GetPetByID(userID, EditEvent.PetID) == null)
            return NotFound();

        // See OnPostLogEvent for why this is a full Clear() rather than ClearValidationState.
        ModelState.Clear();
        TryValidateModel(EditEvent, nameof(EditEvent));

        if (!HealthEventKinds.All.Contains(EditEvent.EventKind, StringComparer.Ordinal))
            ModelState.AddModelError("EditEvent.EventKind", "Choose symptom or incident.");
        if (EditEvent.EndedAtLocal.HasValue && EditEvent.EndedAtLocal < EditEvent.OccurredAtLocal)
            ModelState.AddModelError("EditEvent.EndedAtLocal", "End time cannot be before the event started.");
        if (EditEvent.RecoveredAtLocal.HasValue && EditEvent.RecoveredAtLocal < EditEvent.OccurredAtLocal)
            ModelState.AddModelError("EditEvent.RecoveredAtLocal", "Recovery time cannot be before the event started.");

        if (!ModelState.IsValid)
        {
            var messages = ModelState.Values
                .SelectMany(value => value.Errors)
                .Select(error => error.ErrorMessage)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct()
                .ToList();
            HealthError = messages.Count == 0
                ? "Check the highlighted fields and try again."
                : string.Join(" ", messages);
            OpenEditEventModal = true;
            var invalidResult = LoadPage(userID);
            return invalidResult ?? Page();
        }

        if (!healthService.UpdateHealthEvent(userID, EditEvent))
            return NotFound();

        SetSelectedPetID(EditEvent.PetID);
        TempData["StatusMessage"] = $"{EditEvent.EventKind} updated for {PetsName(userID, EditEvent.PetID)}.";
        return RedirectToPage();
    }

    public IActionResult OnPostDeleteEvent(int healthEventID, int petID)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        if (petService.GetPetByID(userID, petID) == null)
            return NotFound();
        if (!healthService.DeleteHealthEvent(userID, healthEventID))
            return NotFound();

        SetSelectedPetID(petID);
        TempData["StatusMessage"] = "Health event removed.";
        return RedirectToPage();
    }

    private IActionResult? LoadPage(int userID)
    {
        UtcOffsetMinutes = timeZone.GetUtcOffsetMinutes(Request);
        EventType = SourceTypes.FirstOrDefault(type =>
                type.Equals(EventType, StringComparison.OrdinalIgnoreCase)) ?? "All";
        Sort = string.Equals(Sort, "Oldest", StringComparison.OrdinalIgnoreCase) ? "Oldest" : "Newest";
        Pets = petService.GetPetsByUser(userID);
        // A pet remembered from an earlier session can be gone by now (deleted, or this is a
        // different account) — fall back to "All pets" instead of 404ing the whole page.
        if (PetID.HasValue && Pets.All(pet => pet.PetID != PetID.Value))
            SetSelectedPetID(null);

        var userToday = timeZone.ToLocal(DateTime.UtcNow, UtcOffsetMinutes).Date;
        From ??= userToday.AddDays(-29);
        To ??= userToday;
        if (From.Value.Date > To.Value.Date)
        {
            HealthError = "The start date must be on or before the end date.";
            Timeline = [];
        }
        else
        {
            var startUtc = timeZone.ToUtc(From.Value.Date, UtcOffsetMinutes);
            var endUtc = timeZone.ToUtc(To.Value.Date.AddDays(1), UtcOffsetMinutes);
            Timeline = healthService.GetTimeline(
                userID,
                PetID,
                startUtc,
                endUtc,
                UtcOffsetMinutes,
                EventType,
                string.Equals(Sort, "Oldest", StringComparison.OrdinalIgnoreCase));
        }

        SymptomTimeline = Timeline.Where(item => item.SourceType == HealthEventKinds.Symptom).ToList();
        IncidentTimeline = Timeline.Where(item => item.SourceType == HealthEventKinds.Incident).ToList();
        MedicationTimeline = Timeline.Where(item => item.SourceType == "Medication").ToList();
        VetVisitTimeline = Timeline.Where(item => item.SourceType == "VetVisit").ToList();

        MedicationOptions = Pets
            .SelectMany(pet => medicationService.GetMedicationsByPetID(pet.PetID))
            .OrderBy(medication => medication.MedicationName)
            .ToList();
        LoadOverview(userID, userToday);
        return null;
    }

    private int? GetSelectedPetIDFromSession() =>
        int.TryParse(HttpContext.Session.GetString(SelectedPetSessionKey), out var id) ? id : null;

    private void SetSelectedPetID(int? petID)
    {
        PetID = petID;
        if (petID.HasValue)
            HttpContext.Session.SetString(SelectedPetSessionKey, petID.Value.ToString());
        else
            HttpContext.Session.Remove(SelectedPetSessionKey);
    }

    private HealthEventInput ToInput(HealthEvent existing) => new()
    {
        HealthEventID = existing.HealthEventID,
        PetID = existing.PetID,
        EventKind = existing.EventKind,
        EventType = existing.EventType,
        OccurredAtLocal = timeZone.ToLocal(existing.OccurredAtUtc, UtcOffsetMinutes),
        EndedAtLocal = existing.EndedAtUtc.HasValue ? timeZone.ToLocal(existing.EndedAtUtc.Value, UtcOffsetMinutes) : null,
        Severity = existing.Severity,
        Description = existing.Description,
        PossibleTrigger = existing.PossibleTrigger,
        AppetiteStatus = existing.AppetiteStatus,
        DrinkingStatus = existing.DrinkingStatus,
        RelatedMedicationID = existing.RelatedMedicationID,
        RecoveryStatus = existing.RecoveryStatus,
        RecoveredAtLocal = existing.RecoveredAtUtc.HasValue ? timeZone.ToLocal(existing.RecoveredAtUtc.Value, UtcOffsetMinutes) : null,
        VeterinarianContacted = existing.VeterinarianContacted,
        UtcOffsetMinutes = UtcOffsetMinutes
    };

    public DateTime Local(DateTime utc) => timeZone.ToLocal(utc, UtcOffsetMinutes);

    private void LoadOverview(int userID, DateTime userToday)
    {
        var nowLocal = timeZone.ToLocal(DateTime.UtcNow, UtcOffsetMinutes);
        var scopedPets = PetID.HasValue
            ? Pets.Where(pet => pet.PetID == PetID.Value).ToList()
            : Pets;
        var adherenceRows = new List<MedSchedule>();

        foreach (var pet in scopedPets)
        {
            var schedules = medicationService.GetScheduleByPetID(pet.PetID, true, UtcOffsetMinutes);
            adherenceRows.AddRange(schedules.Where(schedule =>
                schedule.ScheduleDate >= userToday.AddDays(-29)
                && schedule.ScheduleDate < userToday.AddDays(1)
                && schedule.EffectiveStatus != MedicationDoseStatuses.Due));

            var medications = MedicationOptions.Where(medication =>
                medication.PetID == pet.PetID
                && medication.StartDate.Date <= userToday
                && (!medication.EndDate.HasValue || medication.EndDate.Value.Date >= userToday));

            foreach (var medication in medications)
            {
                var medicationRows = schedules
                    .Where(schedule => schedule.MedID == medication.MedID)
                    .ToList();
                var lastActivity = medicationRows
                    .Where(schedule => schedule.EffectiveStatus != MedicationDoseStatuses.Due)
                    .OrderByDescending(DoseActivityAtLocal)
                    .FirstOrDefault();
                var nextActivity = medicationRows
                    .Where(schedule => schedule.EffectiveStatus == MedicationDoseStatuses.Due)
                    .OrderBy(schedule => schedule.ScheduleDate)
                    .FirstOrDefault();
                var nextIsLate = nextActivity != null && (nextActivity.TimingDoesNotMatter
                    ? nextActivity.ScheduleDate.Date < userToday
                    : nextActivity.ScheduleDate < nowLocal);

                CurrentMedications.Add(new HealthMedicationSummary
                {
                    MedID = medication.MedID,
                    PetID = pet.PetID,
                    PetName = pet.Name,
                    MedicationName = medication.MedicationName,
                    Dosage = medication.Dosage,
                    FrequencyDisplay = medication.FrequencyDisplay,
                    LastDoseWhen = lastActivity == null
                        ? string.Empty
                        : FormatHealthDate(DoseActivityAtLocal(lastActivity), nowLocal, !lastActivity.TimingDoesNotMatter),
                    LastDoseStatus = lastActivity?.EffectiveStatus ?? string.Empty,
                    NextDoseWhen = nextActivity == null
                        ? string.Empty
                        : nextActivity.ScheduleDate.Date == userToday
                            ? "Today"
                            : FormatHealthDate(nextActivity.ScheduleDate, nowLocal, !nextActivity.TimingDoesNotMatter),
                    NextDoseIsLate = nextIsLate
                });
            }
        }

        CurrentMedications = CurrentMedications
            .OrderBy(summary => summary.PetName)
            .ThenBy(summary => summary.MedicationName)
            .ToList();
        RecentAdherence = new DoseAdherenceSummary
        {
            Total = adherenceRows.Count,
            Taken = adherenceRows.Count(row => row.EffectiveStatus == MedicationDoseStatuses.Taken),
            TakenLate = adherenceRows.Count(row => row.EffectiveStatus == MedicationDoseStatuses.TakenLate),
            Skipped = adherenceRows.Count(row => row.EffectiveStatus == MedicationDoseStatuses.Skipped),
            Missed = adherenceRows.Count(row => row.EffectiveStatus == MedicationDoseStatuses.Missed)
        };

        var overviewStartUtc = timeZone.ToUtc(userToday.AddDays(-29), UtcOffsetMinutes);
        var overviewEndUtc = timeZone.ToUtc(userToday.AddDays(1), UtcOffsetMinutes);
        var recentEvents = healthService.GetHealthEvents(userID, PetID, overviewStartUtc, overviewEndUtc);
        RecentHealthEventCount = recentEvents.Count;
        RecentHealthEvents = recentEvents.Take(3).ToList();

        var visits = vetVisitService.GetVisits(userID, PetID);
        NextVetVisit = visits
            .Where(visit => visit.Status is "Scheduled" or "Confirmed" or "Rescheduled"
                && visit.AppointmentDateTime >= nowLocal)
            .OrderBy(visit => visit.AppointmentDateTime)
            .FirstOrDefault();
        RecentVetVisit = visits
            .Where(visit => visit.AppointmentDateTime < nowLocal)
            .OrderByDescending(visit => visit.AppointmentDateTime)
            .FirstOrDefault();
    }

    private DateTime DoseActivityAtLocal(MedSchedule schedule) => schedule.AdministeredAtUtc.HasValue
        ? timeZone.ToLocal(schedule.AdministeredAtUtc.Value, UtcOffsetMinutes)
        : schedule.ScheduleDate;

    private static string FormatHealthDate(DateTime value, DateTime nowLocal, bool includeTime)
    {
        var day = value.Date == nowLocal.Date
            ? "Today"
            : value.Date == nowLocal.Date.AddDays(-1)
                ? "Yesterday"
                : value.Date == nowLocal.Date.AddDays(1)
                    ? "Tomorrow"
                    : value.ToString("MMM d");
        return includeTime ? $"{day}, {value:h:mm tt}" : day;
    }

    private string PetsName(int userID, int petID) =>
        petService.GetPetByID(userID, petID)?.Name ?? "your pet";

    private bool TryGetUserID(out int userID) =>
        int.TryParse(HttpContext.Session.GetString("userID"), out userID);

    private static string NormalizeKind(string? kind) =>
        kind?.Equals(HealthEventKinds.Incident, StringComparison.OrdinalIgnoreCase) == true
            ? HealthEventKinds.Incident
            : kind?.Equals(HealthEventKinds.Symptom, StringComparison.OrdinalIgnoreCase) == true
                ? HealthEventKinds.Symptom
                : string.Empty;
}
