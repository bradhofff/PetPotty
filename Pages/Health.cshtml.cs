using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages
{
    public class HealthModel : PageModel
    {
        private const string SelectedPetSessionKey = "healthSelectedPetID";

        public static readonly string[] SymptomTypes =
        [
            "Vomiting", "Diarrhea", "Limping", "Lethargy", "Coughing", "Sneezing",
            "Itching / Scratching", "Loss of appetite", "Excessive thirst", "Ear discomfort",
            "Eye discharge", "Skin irritation", "Behavior change", "Other"
        ];

        public static readonly string[] IncidentTypes =
        [
            "Fall / Injury", "Bite or fight", "Ate something toxic/foreign", "Allergic reaction",
            "Seizure", "Accident (car, etc.)", "Choking", "Other"
        ];

        private readonly IPetService _petService;
        private readonly IHealthService _healthService;
        private readonly IMedicationService _medicationService;
        private readonly IVetVisitService _vetVisitService;
        private readonly IHouseholdContextService _householdContext;
        private readonly IUserTimeZoneService _timeZone;
        private readonly int _lateAfterMinutes;
        private readonly int _missedAfterMinutes;

        public HealthModel(
            IPetService petService,
            IHealthService healthService,
            IMedicationService medicationService,
            IVetVisitService vetVisitService,
            IHouseholdContextService householdContext,
            IUserTimeZoneService timeZone,
            IConfiguration configuration)
        {
            _petService = petService;
            _healthService = healthService;
            _medicationService = medicationService;
            _vetVisitService = vetVisitService;
            _householdContext = householdContext;
            _timeZone = timeZone;
            _lateAfterMinutes = Math.Clamp(configuration.GetValue<int?>("MedicationAdherence:LateAfterMinutes") ?? 60, 1, 1440);
            _missedAfterMinutes = Math.Clamp(configuration.GetValue<int?>("MedicationAdherence:MissedAfterMinutes") ?? 720, 1, 10080);
        }

        public int UserID { get; set; }
        public HouseholdContext Household { get; set; } = new();
        public List<Pet> Pets { get; set; } = [];
        public int SelectedPetID { get; set; }
        public Pet? SelectedPet => Pets.FirstOrDefault(p => p.PetID == SelectedPetID);

        // Overview
        public List<HealthMedicationSummary> MedicationSummaries { get; set; } = [];
        public DoseAdherenceSummary Adherence { get; set; } = new();
        public VetVisit? NextVetVisit { get; set; }
        public List<VetVisit> RecentVetVisits { get; set; } = [];

        // Symptoms / Incidents (grouped, most recent first within the selected history range)
        public List<HealthEvent> Symptoms { get; set; } = [];
        public List<HealthEvent> Incidents { get; set; } = [];

        // Chronological history + filters
        public List<HealthTimelineItem> Timeline { get; set; } = [];
        [BindProperty(SupportsGet = true)] public int HistoryDays { get; set; } = 30;
        [BindProperty(SupportsGet = true)] public string HistoryType { get; set; } = "All";
        [BindProperty(SupportsGet = true)] public bool OldestFirst { get; set; }

        public string? ModalToOpen { get; set; }
        public string? HealthError { get; set; }
        public int UtcOffsetMinutes { get; set; }

        [BindProperty] public HealthEventInput EventInput { get; set; } = new();

        // A deep link (Dashboard's "+ Log Health Event") arrives as
        // /Health?petID=X&open=symptom|incident — handled here, then
        // immediately redirected to a bare /Health so the address bar never
        // shows petID or any other query string. The selected pet lives in
        // session and which modal to open (if any) is carried one-shot
        // through TempData across that redirect; see ApplyPendingModal.
        public IActionResult OnGet(int? petID, string? open)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");

            if (petID.HasValue || open != null)
            {
                if (petID.HasValue)
                    SetSelectedPetID(petID.Value);
                if (open is "symptom" or "incident")
                    TempData["HealthOpenModal"] = open;
                return RedirectToPage();
            }

            LoadData();
            ApplyPendingModal();
            return Page();
        }

        public IActionResult OnPostSelectPet(int selectedPetID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            SetSelectedPetID(selectedPetID);
            return RedirectToPage();
        }

        // Shared by both the Health page's own Log Symptom/Incident buttons and
        // the Dashboard's "+ Log Health Event" entry point (which lands here via
        // ?open=symptom|incident) — one handler, one validation path, one
        // authorization path, regardless of where the user started.
        public IActionResult OnPostSaveHealthEvent()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");

            SetSelectedPetID(EventInput.PetID);
            Pets = _petService.GetPetsByHousehold(UserID, Household.HouseholdID);
            ModelState.Clear();
            TryValidateModel(EventInput, nameof(EventInput));
            ValidateEventInput();
            if (!ModelState.IsValid)
                return ShowEventModalError(null);

            var isNew = EventInput.HealthEventID == 0;
            var succeeded = isNew
                ? _healthService.AddHealthEvent(UserID, Household.HouseholdID, EventInput) != 0
                : _healthService.UpdateHealthEvent(UserID, Household.HouseholdID, EventInput);

            if (!succeeded)
                return ShowEventModalError(isNew
                    ? "That event could not be saved. Make sure the pet and any related medication belong to this household."
                    : "That event could not be updated.");

            TempData["StatusMessage"] = $"{EventInput.EventKind} {(isNew ? "logged" : "updated")} successfully!";
            return RedirectToPage();
        }

        // Reached via a plain link (no petID in the URL — just a handler +
        // the event's own id). Authorizes, then hands off to the same
        // redirect-then-TempData flow as OnGet so the address bar still ends
        // up on a bare /Health once the modal is showing.
        public IActionResult OnGetEditHealthEvent(int healthEventID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");

            var existing = _healthService.GetHealthEventByID(UserID, Household.HouseholdID, healthEventID);
            if (existing == null)
                return NotFound();

            SetSelectedPetID(existing.PetID);
            TempData["HealthOpenModal"] = "edit";
            TempData["HealthOpenEventID"] = existing.HealthEventID;
            return RedirectToPage();
        }

        public IActionResult OnPostDeleteHealthEvent(int healthEventID, int petID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");

            SetSelectedPetID(petID);
            TempData["StatusMessage"] = _healthService.DeleteHealthEvent(UserID, Household.HouseholdID, healthEventID)
                ? "Health record deleted."
                : "That record could not be found.";
            return RedirectToPage();
        }

        private void ValidateEventInput()
        {
            const string prefix = nameof(EventInput);
            if (!HealthEventKinds.All.Contains(EventInput.EventKind))
                ModelState.AddModelError($"{prefix}.{nameof(EventInput.EventKind)}", "Choose Symptom or Incident.");
            if (Pets.All(p => p.PetID != EventInput.PetID))
                ModelState.AddModelError($"{prefix}.{nameof(EventInput.PetID)}", "Choose one of your household's pets.");
            if (EventInput.EndedAtLocal.HasValue && EventInput.EndedAtLocal.Value < EventInput.OccurredAtLocal)
                ModelState.AddModelError($"{prefix}.{nameof(EventInput.EndedAtLocal)}", "End time cannot be before the start time.");
            if (EventInput.RecoveredAtLocal.HasValue && EventInput.RecoveredAtLocal.Value < EventInput.OccurredAtLocal)
                ModelState.AddModelError($"{prefix}.{nameof(EventInput.RecoveredAtLocal)}", "Recovery time cannot be before the start time.");
        }

        private PageResult ShowEventModalError(string? message)
        {
            if (message != null)
                ModelState.AddModelError(string.Empty, message);
            HealthError = message ?? string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            ModalToOpen = "logHealthEventModal";
            LoadData();
            return Page();
        }

        // One-shot: consumes TempData left by OnGet's deep-link redirect,
        // OnGetEditHealthEvent, or a failed save/edit, and opens the shared
        // modal accordingly. Reading TempData clears it, so a page refresh
        // afterward is just a plain Health page — the modal doesn't reopen.
        private void ApplyPendingModal()
        {
            if (TempData["HealthOpenModal"] is not string openModal)
                return;

            if (openModal is "symptom" or "incident" && SelectedPetID > 0)
            {
                EventInput = new HealthEventInput
                {
                    PetID = SelectedPetID,
                    EventKind = openModal == "symptom" ? HealthEventKinds.Symptom : HealthEventKinds.Incident,
                    OccurredAtLocal = DateTime.Now
                };
                ModalToOpen = "logHealthEventModal";
            }
            else if (openModal == "edit" && TempData["HealthOpenEventID"] is int healthEventID)
            {
                var existing = _healthService.GetHealthEventByID(UserID, Household.HouseholdID, healthEventID);
                if (existing == null)
                    return;

                EventInput = new HealthEventInput
                {
                    HealthEventID = existing.HealthEventID,
                    PetID = existing.PetID,
                    EventKind = existing.EventKind,
                    EventType = existing.EventType,
                    OccurredAtLocal = _timeZone.ToLocal(existing.OccurredAtUtc, UtcOffsetMinutes),
                    EndedAtLocal = existing.EndedAtUtc.HasValue ? _timeZone.ToLocal(existing.EndedAtUtc.Value, UtcOffsetMinutes) : null,
                    Severity = existing.Severity,
                    Description = existing.Description,
                    PossibleTrigger = existing.PossibleTrigger,
                    AppetiteStatus = existing.AppetiteStatus,
                    DrinkingStatus = existing.DrinkingStatus,
                    RelatedMedicationID = existing.RelatedMedicationID,
                    RecoveryStatus = existing.RecoveryStatus,
                    RecoveredAtLocal = existing.RecoveredAtUtc.HasValue ? _timeZone.ToLocal(existing.RecoveredAtUtc.Value, UtcOffsetMinutes) : null,
                    VeterinarianContacted = existing.VeterinarianContacted,
                    UtcOffsetMinutes = UtcOffsetMinutes
                };
                ModalToOpen = "logHealthEventModal";
            }
        }

        private bool TryResolveHousehold()
        {
            var context = _householdContext.GetActiveHousehold(HttpContext.Session, UserID);
            if (context == null)
                return false;

            Household = context;
            return true;
        }

        private void SetSelectedPetID(int petID)
        {
            SelectedPetID = petID;
            HttpContext.Session.SetInt32(SelectedPetSessionKey, petID);
        }

        private void LoadData()
        {
            Pets = _petService.GetPetsByHousehold(UserID, Household.HouseholdID);

            if (SelectedPetID == 0)
                SelectedPetID = HttpContext.Session.GetInt32(SelectedPetSessionKey) ?? 0;
            if (Pets.All(p => p.PetID != SelectedPetID))
                SelectedPetID = Pets.FirstOrDefault()?.PetID ?? 0;

            if (SelectedPetID == 0)
                return;

            UtcOffsetMinutes = _timeZone.GetRequestOffsetMinutes(HttpContext);
            var nowLocal = DateTime.Now;

            // Overview: current medications with adherence-aware status
            var meds = _medicationService.GetMedicationsByPetID(UserID, Household.HouseholdID, SelectedPetID);
            var schedule = _medicationService.GetScheduleByPetID(UserID, Household.HouseholdID, SelectedPetID, true);
            MedicationSummaries = meds.Select(med =>
            {
                var doses = schedule.Where(s => s.MedID == med.MedID).OrderBy(s => s.ScheduleDate).ToList();
                var lastTaken = doses.Where(s => s.DoseStatus is "Taken" or "Taken late" or "Skipped").OrderByDescending(s => s.ScheduleDate).FirstOrDefault();
                var nextDue = doses.FirstOrDefault(s => MedicationAdherence.EffectiveStatus(s.DoseStatus, s.ScheduleDate, med.TimingDoesNotMatter, _missedAfterMinutes, nowLocal) is "Due" or "Missed");
                return new HealthMedicationSummary
                {
                    MedID = med.MedID,
                    PetID = SelectedPetID,
                    MedicationName = med.MedicationName,
                    Dosage = med.Dosage,
                    FrequencyDisplay = med.FrequencyDisplay,
                    LastDoseWhen = lastTaken?.ScheduleDate.ToString("MMM d, h:mm tt") ?? "—",
                    LastDoseStatus = lastTaken?.DoseStatus ?? "—",
                    NextDoseWhen = nextDue?.ScheduleDate.ToString("MMM d, h:mm tt") ?? "—",
                    NextDoseIsLate = nextDue != null && MedicationAdherence.EffectiveStatus(nextDue.DoseStatus, nextDue.ScheduleDate, med.TimingDoesNotMatter, _missedAfterMinutes, nowLocal) == "Missed"
                };
            }).ToList();

            var recentDoses = schedule.Where(s => s.ScheduleDate >= nowLocal.AddDays(-30) && s.ScheduleDate <= nowLocal).ToList();
            Adherence = new DoseAdherenceSummary
            {
                Total = recentDoses.Count,
                Taken = recentDoses.Count(s => s.DoseStatus == "Taken"),
                TakenLate = recentDoses.Count(s => s.DoseStatus == "Taken late"),
                Skipped = recentDoses.Count(s => s.DoseStatus == "Skipped"),
                Missed = recentDoses.Count(s =>
                    MedicationAdherence.EffectiveStatus(s.DoseStatus, s.ScheduleDate, meds.FirstOrDefault(m => m.MedID == s.MedID)?.TimingDoesNotMatter ?? false, _missedAfterMinutes, nowLocal) == "Missed")
            };

            // Overview: vet visits
            var visits = _vetVisitService.GetVisits(UserID, Household.HouseholdID, SelectedPetID);
            NextVetVisit = visits
                .Where(v => v.IsUpcoming)
                .OrderBy(v => v.AppointmentDateTime)
                .FirstOrDefault();
            RecentVetVisits = visits
                .Where(v => v.Status == "Completed")
                .OrderByDescending(v => v.VisitDate)
                .Take(5)
                .ToList();

            // Symptoms / Incidents — grouped, recent window
            var startUtc = DateTime.UtcNow.AddDays(-Math.Max(HistoryDays, 30));
            var allEvents = _healthService.GetHealthEvents(UserID, Household.HouseholdID, SelectedPetID, startUtc, DateTime.UtcNow.AddDays(1), null);
            Symptoms = allEvents.Where(e => e.EventKind == HealthEventKinds.Symptom).ToList();
            Incidents = allEvents.Where(e => e.EventKind == HealthEventKinds.Incident).ToList();

            // Chronological history (filtered/sorted)
            var historyStartUtc = DateTime.UtcNow.AddDays(-Math.Clamp(HistoryDays, 1, 3650));
            Timeline = _healthService.GetTimeline(
                UserID, Household.HouseholdID, SelectedPetID, historyStartUtc, DateTime.UtcNow.AddMinutes(5),
                UtcOffsetMinutes, HistoryType, OldestFirst);
        }
    }
}
