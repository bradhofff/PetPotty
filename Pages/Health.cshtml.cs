using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages;

public sealed partial class HealthModel(
    IPetService petService,
    IMedicationService medicationService,
    IVetVisitService vetVisitService,
    IHealthService healthService,
    IUserTimeZoneService timeZone,
    IHouseholdContextService householdContext,
    IPetCareService petCareService, ILogger<HealthModel> logger, IPetImageStorage petImageStorage,
    IPetWeightService petWeightService) : PageModel
{
    public string? CarePetImage(Pet pet) => string.IsNullOrWhiteSpace(pet.ProfileImagePath) ? null : $"/pet-image/{petImageStorage.GetAccessToken(pet.ProfileImagePath)}";
    public PetCareProfile CareProfile { get; set; } = new();
    public string? CareError { get; set; }
    public bool IsCareSheet { get; set; }
    [BindProperty] public Dictionary<string, string?> CareValues { get; set; } = new();

    private void LoadCare(int userID, int petID)
    {
        try { CareProfile = petCareService.Get(userID, petID) ?? new(); }
        catch (Microsoft.Data.SqlClient.SqlException ex) {
            logger.LogError(ex, "Unable to load care profile for pet {PetID}", petID);
            CareError = ex.Number == 208 ? "Care information is unavailable until the care-profile migration is installed." : "Care information could not be loaded. Please try again.";
        }
    }

    public IActionResult OnPostSaveCare(int petID, string section)
    {
        if (!TryGetUserID(out var userID)) return Unauthorized();
        var pet = petService.GetPetByID(userID, petID);
        if (pet == null) return NotFound();
        var household = householdContext.GetActiveHousehold(userID);
        if (household == null || !HouseholdAccessRules.HasPermission(household.Role, HouseholdPermission.ManageCarePlans)) return StatusCode(403);
        var fields = PetCareProfile.Fields.Where(f => f.Section == section).ToArray();
        if (fields.Length == 0) return BadRequest(new { message = "Choose a valid section." });
        var errors = fields.Where(f => CareValues.GetValueOrDefault(f.Name)?.Length > f.Limit)
            .ToDictionary(f => f.Name, f => $"Use at most {f.Limit} characters.");
        if (errors.Count > 0) return BadRequest(new { message = "Check the highlighted fields.", errors });
        try {
            if (!petCareService.SaveSection(userID, petID, section, CareValues)) return StatusCode(403);
        } catch (Microsoft.Data.SqlClient.SqlException ex) {
            logger.LogError(ex, "Unable to save care profile for pet {PetID}", petID);
            return StatusCode(503, new { message = "Care information could not be saved. Your entries are still here. Please try again." });
        }
        return new JsonResult(new { message = $"{PetCareProfile.SectionTitle(section)} saved for {pet.Name}." });
    }

    public IActionResult OnGetCareSheet(int petID)
    {
        if (!TryGetUserID(out var userID)) return RedirectToPage("/Login");
        var pet = petService.GetPetByID(userID, petID);
        if (pet == null) return NotFound();
        Pets = [pet]; PetID = petID; IsCareSheet = true;
        UtcOffsetMinutes = timeZone.GetUtcOffsetMinutes(Request);
        LoadCare(userID, petID);
        var now = Local(DateTime.UtcNow);
        MedicationOptions = medicationService.GetMedicationsByPetID(userID, petID)
            .Where(m => m.StartDate.Date <= now.Date && (!m.EndDate.HasValue || m.EndDate.Value.Date >= now.Date)).ToList();
        NextVetVisit = vetVisitService.GetVisits(userID, petID)
            .Where(v => IsUpcomingVisit(v, now))
            .OrderBy(v => v.AppointmentDateTime).FirstOrDefault();
        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    private const string EditEventSessionKey = "health-edit-event";
    private const string HistoryRangeSessionKey = "health-filter-range";
    private const string FromSessionKey = "health-filter-from";
    private const string ToSessionKey = "health-filter-to";
    private const string EventTypeSessionKey = "health-filter-type";
    private const string SortSessionKey = "health-filter-sort";
    private const string SearchSessionKey = "health-filter-search";
    private const string HistoryViewSessionKey = "health-filter-view";

    public int? PetID { get; set; }
    [BindProperty] public string HistoryRange { get; set; } = "Recent";
    [BindProperty(SupportsGet = true)] public int HistoryPage { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public int UpcomingPage { get; set; } = 1;
    public const int HistoryPageSize = 50;
    public const int UpcomingPageSize = 5;
    public bool HasMoreHistory { get; set; }
    public int UpcomingCount { get; set; }
    public List<HealthUpcomingItem> UpcomingCare { get; set; } = [];
    public string HistoryRangeLabel => HistoryRange == "All" ? "All time through today" : $"{From:MMM d, yyyy} – {To:MMM d, yyyy}";
    public string HistoryPageLabel => Timeline.Count == 0 ? "No matching records on this page" : $"Records {(HistoryPage - 1) * HistoryPageSize + 1}–{(HistoryPage - 1) * HistoryPageSize + Timeline.Count}{(HasMoreHistory ? " · more available" : "")}";

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

        var editHealthEventID = HttpContext.Session.GetInt32(EditEventSessionKey);
        HttpContext.Session.Remove(EditEventSessionKey);
        HealthEvent? existing = null;
        if (editHealthEventID.HasValue)
        {
            existing = healthService.GetHealthEventByID(userID, editHealthEventID.Value);
            if (existing == null || !SelectedPetPreference.TrySelect(HttpContext.Session, petService.GetPetsByUser(userID), existing.PetID))
                return NotFound();
        }
        var result = LoadPage(userID);
        if (result != null) return result;
        if (existing != null)
        {
            EditEvent = ToInput(existing);
            OpenEditEventModal = true;
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
        if (ModelState.TryGetValue("selectedPetID", out var selectionState) && selectionState.Errors.Count > 0)
            return NotFound();
        if (!SelectedPetPreference.TrySelect(HttpContext.Session, petService.GetPetsByUser(userID), selectedPetID))
            return NotFound();

        return RedirectToPage();
    }

    public IActionResult OnPostOpenEvent(int healthEventID)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        var existing = healthService.GetHealthEventByID(userID, healthEventID);
        if (existing == null)
            return NotFound();

        if (!SelectedPetPreference.TrySelect(HttpContext.Session, petService.GetPetsByUser(userID), existing.PetID))
            return NotFound();
        HttpContext.Session.SetInt32(EditEventSessionKey, healthEventID);
        return RedirectToPage();
    }

    public IActionResult OnPostSetHistoryRange(string range)
    {
        if (!TryGetUserID(out _)) return RedirectToPage("/Login");
        if (range is not ("Recent" or "All")) return BadRequest();
        HttpContext.Session.SetString(HistoryRangeSessionKey, range);
        HttpContext.Session.Remove(FromSessionKey);
        HttpContext.Session.Remove(ToSessionKey);
        return Redirect("/Health#health-history");
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

        foreach (var key in new[] { HistoryRangeSessionKey, FromSessionKey, ToSessionKey, EventTypeSessionKey, SortSessionKey, SearchSessionKey, HistoryViewSessionKey })
            HttpContext.Session.Remove(key);
        return Redirect("/Health#health-history");
    }

    public IActionResult OnPostLogEvent()
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");

        NewEvent.EventKind = NormalizeKind(NewEvent.EventKind);
        if (petService.GetPetByID(userID, NewEvent.PetID) == null)
            return NotFound();
        if (!SelectedPetPreference.TrySelect(HttpContext.Session, petService.GetPetsByUser(userID), NewEvent.PetID))
            return NotFound();
        PetID = NewEvent.PetID;

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

        return RedirectToPage();
    }

    public IActionResult OnPostEditEvent()
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");

        EditEvent.EventKind = NormalizeKind(EditEvent.EventKind);
        if (petService.GetPetByID(userID, EditEvent.PetID) == null)
            return NotFound();
        if (!SelectedPetPreference.TrySelect(HttpContext.Session, petService.GetPetsByUser(userID), EditEvent.PetID))
            return NotFound();
        PetID = EditEvent.PetID;

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
        PetID = SelectedPetPreference.Get(HttpContext.Session, Pets);

        var userToday = timeZone.ToLocal(DateTime.UtcNow, UtcOffsetMinutes).Date;
        RestoreFilters();
        NormalizeFilters();
        HistoryPage = Math.Clamp(HistoryPage, 1, 1000000);
        UpcomingPage = Math.Clamp(UpcomingPage, 1, 1000000);
        if (HistoryRange == "Recent") { From = userToday.AddMonths(-2); To = userToday; }
        else if (HistoryRange == "All") { From = new DateTime(1, 1, 2); To = userToday; }
        else { From ??= userToday.AddMonths(-2); To ??= userToday; }
        if (To > userToday) To = userToday;

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
            if (endUtc > DateTime.UtcNow) endUtc = DateTime.UtcNow;
            Timeline = healthService.GetTimeline(
                userID,
                PetID,
                startUtc,
                endUtc,
                UtcOffsetMinutes,
                EventType,
                string.Equals(Sort, "Oldest", StringComparison.OrdinalIgnoreCase),
                Search, (HistoryPage - 1) * HistoryPageSize, HistoryPageSize);
            HasMoreHistory = Timeline.Count > HistoryPageSize;
            Timeline = Timeline.Take(HistoryPageSize).ToList();
        }

        SymptomTimeline = Timeline.Where(item => item.SourceType == HealthEventKinds.Symptom).ToList();
        IncidentTimeline = Timeline.Where(item => item.SourceType == HealthEventKinds.Incident).ToList();
        MedicationTimeline = Timeline.Where(item => item.SourceType == "Medication").ToList();
        VetVisitTimeline = Timeline.Where(item => item.SourceType == "VetVisit").ToList();

        MedicationOptions = Pets
            .Where(pet => pet.PetID == PetID.Value)
            .SelectMany(pet => medicationService.GetMedicationsByPetID(userID, pet.PetID))
            .OrderBy(medication => medication.MedicationName)
            .ToList();
        LoadCare(userID, PetID.Value);
        LoadWeights(userID);
        LoadOverview(userID, userToday);
        return null;
    }

    private void RestoreFilters()
    {
        HistoryRange = HttpContext.Session.GetString(HistoryRangeSessionKey) ?? "Recent";
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
        HistoryRange = HistoryRange is "All" or "Custom" ? HistoryRange : "Recent";
        EventType = SourceTypes.FirstOrDefault(type =>
                type.Equals(EventType, StringComparison.OrdinalIgnoreCase)) ?? "All";
        Sort = string.Equals(Sort, "Oldest", StringComparison.OrdinalIgnoreCase) ? "Oldest" : "Newest";
        HistoryView = string.Equals(HistoryView, "Timeline", StringComparison.OrdinalIgnoreCase) ? "Timeline" : "Grouped";
        Search = Search?.Trim();
        if (Search?.Length > 200) Search = Search[..200];
    }

    private void StoreFilters()
    {
        HttpContext.Session.SetString(HistoryRangeSessionKey, HistoryRange);
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
        var upcoming = new List<HealthUpcomingItem>();

        foreach (var pet in scopedPets)
        {
            var schedules = medicationService.GetScheduleByPetID(userID, pet.PetID, true, UtcOffsetMinutes);
            upcoming.AddRange(schedules.Where(dose => dose.DoseStatus == MedicationDoseStatuses.Due
                && (dose.TimingDoesNotMatter ? dose.ScheduleDate.Date >= nowLocal.Date : dose.ScheduleDate >= nowLocal))
                .Select(dose => new HealthUpcomingItem("Medication", dose.ScheduleID, pet.PetID,
                    dose.MedicationName, dose.Dosage, dose.ScheduleDate,
                    dose.TimingDoesNotMatter ? "Any time" : dose.ScheduleDate.ToString("h:mm tt"))));
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
        var futureVisits = visits.Where(visit => IsUpcomingVisit(visit, nowLocal))
            .OrderBy(visit => visit.AppointmentDateTime).ToList();
        NextVetVisit = futureVisits.FirstOrDefault();
        upcoming.AddRange(futureVisits.Select(visit => new HealthUpcomingItem("VetVisit", visit.VetVisitID, visit.PetID,
            string.IsNullOrWhiteSpace(visit.VisitReason) ? "Vet appointment" : visit.VisitReason,
            visit.ClinicName, visit.AppointmentDateTime, VisitTimeLabel(visit))));
        // Include the full local calendar day two months from today.
        var upcomingThrough = nowLocal.Date.AddMonths(2);
        upcoming = upcoming.Where(item => item.At.Date <= upcomingThrough).ToList();
        UpcomingCount = upcoming.Count;
        UpcomingCare = upcoming.OrderBy(item => item.At).ThenBy(item => item.Kind).ThenBy(item => item.RecordID)
            .Skip((UpcomingPage - 1) * UpcomingPageSize).Take(UpcomingPageSize).ToList();
    }

    public static bool IsUpcomingVisit(VetVisit visit, DateTime localNow) =>
        (visit.Status is "Scheduled" or "Confirmed" or "Rescheduled")
        && (visit.IsAllDay || !visit.VisitTime.HasValue ? visit.VisitDate.Date >= localNow.Date : visit.AppointmentDateTime >= localNow);

    public static string VisitTimeLabel(VetVisit visit) => visit.IsAllDay ? "All day"
        : !visit.VisitTime.HasValue ? "Time not specified" : visit.AppointmentDateTime.ToString("h:mm tt");

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
