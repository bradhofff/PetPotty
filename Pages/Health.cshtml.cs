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
    IUserTimeZoneService timeZone,
    IHouseholdContextService householdContext) : PageModel
{
    private const string SelectedPetSessionKey = "health-selected-pet";
    private const string EditEventSessionKey = "health-edit-event";
    private const string FromSessionKey = "health-filter-from";
    private const string ToSessionKey = "health-filter-to";
    private const string EventTypeSessionKey = "health-filter-type";
    private const string SortSessionKey = "health-filter-sort";
    private const string SearchSessionKey = "health-filter-search";
    private const string HistoryViewSessionKey = "health-filter-view";

    public int? PetID { get; set; }
    [BindProperty] public DateTime? From { get; set; }
    [BindProperty] public DateTime? To { get; set; }
    [BindProperty] public string? EventType { get; set; } = "All";
    [BindProperty] public string? Sort { get; set; } = "Newest";
    [BindProperty] public string? Search { get; set; }
    [BindProperty] public string? HistoryView { get; set; } = "Grouped";
    [BindProperty] public HealthEventInput NewEvent { get; set; } = new();
    [BindProperty] public HealthEventInput EditEvent { get; set; } = new();
    [BindProperty] public bool ReturnToDashboard { get; set; }

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
    public bool CanManageCarePlans { get; set; }

    public static readonly string[] SourceTypes =
        ["All", HealthEventKinds.Symptom, HealthEventKinds.Incident, "Medication", "VetVisit"];

    public static readonly string[] SymptomTypes =
        ["Vomiting", "Diarrhea", "Coughing", "Limping", "Low appetite", "Low energy", "Pain", "Itching", "Other"];

    public static readonly string[] IncidentTypes =
        ["Seizure", "Vomiting", "Diarrhea", "Fall", "Injury", "Pain episode", "Breathing problem", "Allergic reaction", "Accident", "Behavioral change", "Other"];

    public IActionResult OnGet()
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");

        var result = LoadPage(userID);
        if (result != null)
            return result;

        var editHealthEventID = HttpContext.Session.GetInt32(EditEventSessionKey);
        HttpContext.Session.Remove(EditEventSessionKey);
        if (editHealthEventID.HasValue)
        {
            var existing = healthService.GetHealthEventByID(userID, editHealthEventID.Value);
            if (existing != null)
            {
                EditEvent = ToInput(existing);
                OpenEditEventModal = true;
            }
        }

        NewEvent.PetID = PetID ?? 0;
        NewEvent.EventKind = HealthEventKinds.Symptom;
        NewEvent.OccurredAtLocal = timeZone.ToLocal(DateTime.UtcNow, UtcOffsetMinutes);
        NewEvent.UtcOffsetMinutes = UtcOffsetMinutes;
        return Page();
    }

    public IActionResult OnPostSelectPet(int? selectedPetID)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        if (selectedPetID.HasValue && petService.GetPetByID(userID, selectedPetID.Value) == null)
            return NotFound();

        if (selectedPetID.HasValue)
            HttpContext.Session.SetInt32(SelectedPetSessionKey, selectedPetID.Value);
        else
            HttpContext.Session.Remove(SelectedPetSessionKey);
        return RedirectToPage();
    }

    public IActionResult OnPostOpenEvent(int healthEventID)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        var existing = healthService.GetHealthEventByID(userID, healthEventID);
        if (existing == null)
            return NotFound();

        HttpContext.Session.SetInt32(SelectedPetSessionKey, existing.PetID);
        HttpContext.Session.SetInt32(EditEventSessionKey, healthEventID);
        return RedirectToPage();
    }

    public IActionResult OnPostApplyFilters()
    {
        if (!TryGetUserID(out _))
            return RedirectToPage("/Login");

        NormalizeFilters();
        StoreFilters();
        return Redirect("/Health#health-history");
    }

    public IActionResult OnPostResetFilters()
    {
        if (!TryGetUserID(out _))
            return RedirectToPage("/Login");

        foreach (var key in new[] { FromSessionKey, ToSessionKey, EventTypeSessionKey, SortSessionKey, SearchSessionKey, HistoryViewSessionKey })
            HttpContext.Session.Remove(key);
        return Redirect("/Health#health-history");
    }

    public IActionResult OnPostLogEvent()
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");

        PetID = HttpContext.Session.GetInt32(SelectedPetSessionKey);
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

        TempData["StatusMessage"] = $"{NewEvent.EventKind} recorded for {PetsName(userID, NewEvent.PetID)}.";
        if (ReturnToDashboard)
            return RedirectToPage("/Home");

        HttpContext.Session.SetInt32(SelectedPetSessionKey, NewEvent.PetID);
        return RedirectToPage();
    }

    public IActionResult OnPostEditEvent()
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");

        PetID = HttpContext.Session.GetInt32(SelectedPetSessionKey);
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

        TempData["StatusMessage"] = $"{EditEvent.EventKind} updated for {PetsName(userID, EditEvent.PetID)}.";
        HttpContext.Session.SetInt32(SelectedPetSessionKey, EditEvent.PetID);
        return RedirectToPage();
    }

    public IActionResult OnPostDeleteEvent(int healthEventID)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        if (!healthService.DeleteHealthEvent(userID, healthEventID))
            return NotFound();

        TempData["StatusMessage"] = "Health event removed.";
        return RedirectToPage();
    }

    private IActionResult? LoadPage(int userID)
    {
        UtcOffsetMinutes = timeZone.GetUtcOffsetMinutes(Request);
        var household = householdContext.GetActiveHousehold(userID);
        CanManageCarePlans = household != null
            && HouseholdAccessRules.HasPermission(household.Role, HouseholdPermission.ManageCarePlans);
        Pets = petService.GetPetsByUser(userID);
        PetID ??= HttpContext.Session.GetInt32(SelectedPetSessionKey);
        if (PetID.HasValue && Pets.All(pet => pet.PetID != PetID.Value))
        {
            PetID = null;
            HttpContext.Session.Remove(SelectedPetSessionKey);
        }

        var userToday = timeZone.ToLocal(DateTime.UtcNow, UtcOffsetMinutes).Date;
        RestoreFilters();
        NormalizeFilters();
        From ??= userToday.AddDays(-29);
        To ??= userToday;

        if (!PetID.HasValue)
            return null;

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

        if (!string.IsNullOrWhiteSpace(Search))
            Timeline = Timeline.Where(item => new[] { item.Title, item.Summary, item.PetName, item.Attribution, item.Status }
                .Any(value => value?.Contains(Search, StringComparison.OrdinalIgnoreCase) == true)).ToList();

        SymptomTimeline = Timeline.Where(item => item.SourceType == HealthEventKinds.Symptom).ToList();
        IncidentTimeline = Timeline.Where(item => item.SourceType == HealthEventKinds.Incident).ToList();
        MedicationTimeline = Timeline.Where(item => item.SourceType == "Medication").ToList();
        VetVisitTimeline = Timeline.Where(item => item.SourceType == "VetVisit").ToList();

        MedicationOptions = Pets
            .Where(pet => pet.PetID == PetID.Value)
            .SelectMany(pet => medicationService.GetMedicationsByPetID(userID, pet.PetID))
            .OrderBy(medication => medication.MedicationName)
            .ToList();
        LoadOverview(userID, userToday);
        return null;
    }

    private void RestoreFilters()
    {
        if (DateTime.TryParse(HttpContext.Session.GetString(FromSessionKey), out var from))
            From = from;
        if (DateTime.TryParse(HttpContext.Session.GetString(ToSessionKey), out var to))
            To = to;
        if (HttpContext.Session.GetString(EventTypeSessionKey) is { } eventType) EventType = eventType;
        if (HttpContext.Session.GetString(SortSessionKey) is { } sort) Sort = sort;
        if (HttpContext.Session.GetString(SearchSessionKey) is { } search) Search = search;
        if (HttpContext.Session.GetString(HistoryViewSessionKey) is { } view) HistoryView = view;
    }

    private void NormalizeFilters()
    {
        EventType = SourceTypes.FirstOrDefault(type =>
                type.Equals(EventType, StringComparison.OrdinalIgnoreCase)) ?? "All";
        Sort = string.Equals(Sort, "Oldest", StringComparison.OrdinalIgnoreCase) ? "Oldest" : "Newest";
        HistoryView = string.Equals(HistoryView, "Timeline", StringComparison.OrdinalIgnoreCase) ? "Timeline" : "Grouped";
        Search = Search?.Trim();
    }

    private void StoreFilters()
    {
        if (From.HasValue) HttpContext.Session.SetString(FromSessionKey, From.Value.ToString("O"));
        else HttpContext.Session.Remove(FromSessionKey);
        if (To.HasValue) HttpContext.Session.SetString(ToSessionKey, To.Value.ToString("O"));
        else HttpContext.Session.Remove(ToSessionKey);
        HttpContext.Session.SetString(EventTypeSessionKey, EventType ?? "All");
        HttpContext.Session.SetString(SortSessionKey, Sort ?? "Newest");
        HttpContext.Session.SetString(HistoryViewSessionKey, HistoryView ?? "Grouped");
        if (string.IsNullOrWhiteSpace(Search)) HttpContext.Session.Remove(SearchSessionKey);
        else HttpContext.Session.SetString(SearchSessionKey, Search);
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
            var schedules = medicationService.GetScheduleByPetID(userID, pet.PetID, true, UtcOffsetMinutes);
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
