using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages
{
    public class MedicationsModel : PageModel
    {
        private readonly IPetService _petService;
        private readonly IMedicationService _medService;
        private readonly IHouseholdContextService _householdContext;
        private readonly IUserTimeZoneService _timeZone;
        private readonly int _lateAfterMinutes;

        public MedicationsModel(
            IPetService petService,
            IMedicationService medService,
            IHouseholdContextService householdContext,
            IUserTimeZoneService timeZone,
            IConfiguration configuration)
        {
            _petService = petService;
            _medService = medService;
            _householdContext = householdContext;
            _timeZone = timeZone;
            _lateAfterMinutes = Math.Clamp(configuration.GetValue<int?>("MedicationAdherence:LateAfterMinutes") ?? 60, 1, 1440);
        }

        // ── Page state ──────────────────────────────────────────────
        public int UserID { get; set; }
        public HouseholdContext Household { get; set; } = new();
        public List<Pet> Pets { get; set; } = new();
        public List<Medication> Medications { get; set; } = new();
        public List<MedSchedule> Schedule { get; set; } = new();

        [BindProperty] public int SelectedPetID { get; set; } = 0;
        [BindProperty] public bool ShowAllTime { get; set; } = false;
        public string? MedicationError { get; set; }
        public string? ModalToOpen { get; set; }

        // ── Add Medication fields ────────────────────────────────────
        [BindProperty] public int NewMedPetID { get; set; }
        [BindProperty] public string NewMedName { get; set; } = string.Empty;
        [BindProperty] public string NewMedDosage { get; set; } = string.Empty;
        [BindProperty] public string NewMedFrequencyType { get; set; } = string.Empty;
        [BindProperty] public int? NewMedFrequencyInterval { get; set; }
        [BindProperty] public bool NewMedTimingDoesNotMatter { get; set; } = true;
        [BindProperty] public DateTime NewMedStartDate { get; set; } = DateTime.Today;
        [BindProperty] public TimeSpan? NewMedStartTime { get; set; }
        [BindProperty] public DateTime? NewMedEndDate { get; set; }
        [BindProperty] public bool NewMedForever { get; set; } = true;
        [BindProperty] public string NewMedNotes { get; set; } = string.Empty;

        // ── Edit Medication fields ───────────────────────────────────
        [BindProperty] public int EditMedID { get; set; }
        [BindProperty] public string EditMedName { get; set; } = string.Empty;
        [BindProperty] public string EditMedDosage { get; set; } = string.Empty;
        [BindProperty] public string EditMedFrequencyType { get; set; } = string.Empty;
        [BindProperty] public int? EditMedFrequencyInterval { get; set; }
        [BindProperty] public bool EditMedTimingDoesNotMatter { get; set; }
        [BindProperty] public DateTime EditMedStartDate { get; set; } = DateTime.Today;
        [BindProperty] public TimeSpan? EditMedStartTime { get; set; }
        [BindProperty] public DateTime? EditMedEndDate { get; set; }
        [BindProperty] public bool EditMedForever { get; set; }
        [BindProperty] public string EditMedNotes { get; set; } = string.Empty;

        // ── GET ──────────────────────────────────────────────────────
        public IActionResult OnGet(int? petID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            if (petID.HasValue)
                SelectedPetID = petID.Value;
            LoadData();
            if (petID.HasValue && SelectedPetID == petID.Value)
                SetSelectedPetID(petID.Value);
            if (NewMedPetID == 0 && SelectedPetID > 0)
                NewMedPetID = SelectedPetID;
            return Page();
        }

        public IActionResult OnPostSelectPet(int selectedPetID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            SetSelectedPetID(selectedPetID);
            return RedirectToPage();
        }

        public IActionResult OnPostSetScheduleView(bool showAllTime)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            HttpContext.Session.SetString("medicationsShowAllTime", showAllTime.ToString());
            return RedirectToPage();
        }

        // ── Add Medication ───────────────────────────────────────────
        public IActionResult OnPostAddMedication()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            RestoreStateFromSession();
            Pets = _petService.GetPetsByHousehold(UserID, Household.HouseholdID);

            if (NewMedPetID <= 0)
                return ShowMedicationModalError("addMedModal", "Choose a pet for this medication.");
            if (Pets.All(pet => pet.PetID != NewMedPetID))
                return Forbid();

            if (!TryNormalizeMedicationTiming(
                    NewMedFrequencyType,
                    NewMedTimingDoesNotMatter,
                    NewMedStartDate,
                    NewMedStartTime,
                    out var timingDoesNotMatter,
                    out var normalizedStartDate,
                    out var timingError))
            {
                return ShowMedicationModalError("addMedModal", timingError);
            }

            NewMedTimingDoesNotMatter = timingDoesNotMatter;
            NewMedStartDate = normalizedStartDate;

            if (!NewMedForever)
            {
                if (!NewMedEndDate.HasValue)
                {
                    return ShowMedicationModalError(
                        "addMedModal",
                        "End date is required when Forever is unchecked.");
                }

                NewMedEndDate = timingDoesNotMatter
                    ? NewMedEndDate.Value.Date
                    : NewMedEndDate.Value;
                var invalidEndDate = timingDoesNotMatter
                    ? NewMedEndDate.Value.Date < normalizedStartDate.Date
                    : NewMedEndDate.Value <= normalizedStartDate;
                if (invalidEndDate)
                {
                    return ShowMedicationModalError(
                        "addMedModal",
                        timingDoesNotMatter
                            ? "End date cannot be before the start date."
                            : "End date and time must be after the start date and time.");
                }
            }

            var newMedID = _medService.AddMedication(
                UserID, Household.HouseholdID,
                NewMedPetID, NewMedName, NewMedDosage,
                NewMedFrequencyType, NewMedFrequencyInterval, NewMedTimingDoesNotMatter,
                NewMedStartDate, NewMedForever ? null : NewMedEndDate,
                NewMedNotes);
            if (newMedID == 0)
                return ShowMedicationModalError("addMedModal", "That medication could not be added.");

            SetSelectedPetID(NewMedPetID);
            TempData["StatusMessage"] = $"{NewMedName} added successfully!";
            return RedirectToPage();
        }

        // ── Edit Medication ──────────────────────────────────────────
        public IActionResult OnPostEditMedication()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            RestoreStateFromSession();
            SetSelectedPetID(SelectedPetID);

            if (!TryNormalizeMedicationTiming(
                    EditMedFrequencyType,
                    EditMedTimingDoesNotMatter,
                    EditMedStartDate,
                    EditMedStartTime,
                    out var timingDoesNotMatter,
                    out var normalizedStartDate,
                    out var timingError))
            {
                return ShowMedicationModalError("editMedModal", timingError);
            }

            EditMedTimingDoesNotMatter = timingDoesNotMatter;
            EditMedStartDate = normalizedStartDate;

            if (!EditMedForever)
            {
                if (!EditMedEndDate.HasValue)
                {
                    return ShowMedicationModalError(
                        "editMedModal",
                        "End date is required when Forever is unchecked.");
                }

                EditMedEndDate = timingDoesNotMatter
                    ? EditMedEndDate.Value.Date
                    : EditMedEndDate.Value;
                var invalidEndDate = timingDoesNotMatter
                    ? EditMedEndDate.Value.Date < normalizedStartDate.Date
                    : EditMedEndDate.Value <= normalizedStartDate;
                if (invalidEndDate)
                {
                    return ShowMedicationModalError(
                        "editMedModal",
                        timingDoesNotMatter
                            ? "End date cannot be before the start date."
                            : "End date and time must be after the start date and time.");
                }
            }

            if (!_medService.UpdateMedication(
                UserID, Household.HouseholdID,
                EditMedID, EditMedName, EditMedDosage,
                EditMedFrequencyType, EditMedFrequencyInterval, EditMedTimingDoesNotMatter,
                EditMedStartDate, EditMedForever ? null : EditMedEndDate,
                EditMedNotes))
                return ShowMedicationModalError("editMedModal", "That medication could not be updated.");

            TempData["StatusMessage"] = $"{EditMedName} updated successfully!";
            return RedirectToPage();
        }

        // ── Delete Medication ────────────────────────────────────────
        public IActionResult OnPostDeleteMedication(int medID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            RestoreStateFromSession();
            TempData["StatusMessage"] = _medService.DeleteMedication(UserID, Household.HouseholdID, medID)
                ? "Medication deleted."
                : "That medication could not be deleted.";
            return RedirectToPage();
        }

        // ── Confirm Schedule ─────────────────────────────────────────
        public IActionResult OnPostConfirmSchedule(int medID, DateTime logDate, DateTime confirmedAt)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            RestoreStateFromSession();

            var timingDoesNotMatter = SelectedPetID > 0
                && _medService.GetMedicationsByPetID(UserID, Household.HouseholdID, SelectedPetID)
                    .FirstOrDefault(m => m.MedID == medID)?.TimingDoesNotMatter == true;
            var doseStatus = MedicationAdherence.ResolveConfirmStatus(logDate, confirmedAt, timingDoesNotMatter, _lateAfterMinutes);
            var administeredAtUtc = _timeZone.ToUtc(confirmedAt, _timeZone.GetRequestOffsetMinutes(HttpContext));

            _medService.ConfirmSchedule(UserID, Household.HouseholdID, medID, logDate, confirmedAt, UserID, administeredAtUtc, doseStatus);

            TempData["StatusMessage"] = "Dose confirmed!";
            return RedirectToPage();
        }

        // ── Unconfirm Schedule ───────────────────────────────────────
        public IActionResult OnPostUnconfirmSchedule(int medID, DateTime logDate)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            RestoreStateFromSession();
            _medService.UnconfirmSchedule(UserID, Household.HouseholdID, medID, logDate);

            TempData["StatusMessage"] = "Dose unconfirmed.";
            return RedirectToPage();
        }

        // ── Helpers ──────────────────────────────────────────────────
        private static bool TryNormalizeMedicationTiming(
            string frequencyType,
            bool requestedTimingDoesNotMatter,
            DateTime startDate,
            TimeSpan? startTime,
            out bool timingDoesNotMatter,
            out DateTime normalizedStartDate,
            out string error)
        {
            timingDoesNotMatter = false;
            normalizedStartDate = startDate;
            error = string.Empty;

            var normalizedFrequency = frequencyType?.Trim() ?? string.Empty;
            var validFrequencies = new[] { "Hourly", "Daily", "Weekly", "Monthly" };
            if (!validFrequencies.Contains(normalizedFrequency, StringComparer.OrdinalIgnoreCase))
            {
                error = "Choose a valid medication frequency.";
                return false;
            }

            if (startDate == default)
            {
                error = "Choose a valid start date.";
                return false;
            }

            var isHourly = normalizedFrequency.Equals("Hourly", StringComparison.OrdinalIgnoreCase);
            timingDoesNotMatter = !isHourly && requestedTimingDoesNotMatter;

            if (!timingDoesNotMatter && !startTime.HasValue)
            {
                error = isHourly
                    ? "Hourly medications require an exact start time."
                    : "Choose a start time or select ‘Timing doesn’t matter’.";
                return false;
            }

            normalizedStartDate = startDate.Date
                + (timingDoesNotMatter ? TimeSpan.Zero : startTime!.Value);
            return true;
        }

        private PageResult ShowMedicationModalError(string modalID, string message)
        {
            MedicationError = message;
            ModalToOpen = modalID;
            LoadData();
            return Page();
        }

        private void LoadData()
        {
            Pets = _petService.GetPetsByHousehold(UserID, Household.HouseholdID);
            RestoreStateFromSession();

            if (SelectedPetID > 0 && Pets.All(pet => pet.PetID != SelectedPetID))
            {
                SetSelectedPetID(0);
            }

            if (SelectedPetID > 0)
            {
                Medications = _medService.GetMedicationsByPetID(UserID, Household.HouseholdID, SelectedPetID);
                Schedule    = _medService.GetScheduleByPetID(UserID, Household.HouseholdID, SelectedPetID, ShowAllTime);
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

        private void RestoreStateFromSession()
        {
            if (SelectedPetID <= 0 && int.TryParse(HttpContext.Session.GetString("medicationsSelectedPetID"), out var selectedPetID))
                SelectedPetID = selectedPetID;

            ShowAllTime = bool.TryParse(HttpContext.Session.GetString("medicationsShowAllTime"), out var showAllTime) && showAllTime;
        }

        private void SetSelectedPetID(int selectedPetID)
        {
            SelectedPetID = selectedPetID;
            HttpContext.Session.SetString("medicationsSelectedPetID", selectedPetID.ToString());
        }
    }
}
