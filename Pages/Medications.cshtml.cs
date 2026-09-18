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
        private readonly IUserTimeZoneService _timeZone;
        private readonly IHouseholdContextService _householdContext;

        public MedicationsModel(
            IPetService petService,
            IMedicationService medService,
            IUserTimeZoneService timeZone,
            IHouseholdContextService householdContext)
        {
            _petService = petService;
            _medService = medService;
            _timeZone = timeZone;
            _householdContext = householdContext;
        }

        // ── Page state ──────────────────────────────────────────────
        public int UserID { get; set; }
        public List<Pet> Pets { get; set; } = new();
        public List<Medication> Medications { get; set; } = new();
        public List<MedSchedule> Schedule { get; set; } = new();
        public int UtcOffsetMinutes { get; set; }
        public DateTime UserNowLocal => _timeZone.ToLocal(DateTime.UtcNow, UtcOffsetMinutes);
        public bool CanManageCarePlans { get; set; }

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
        public IActionResult OnGet(int? petID, int? editMedID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            UtcOffsetMinutes = _timeZone.GetUtcOffsetMinutes(Request);
            if (petID.HasValue && _petService.GetPetByID(UserID, petID.Value) == null)
                return NotFound();
            if (petID.HasValue)
                SelectedPetID = petID.Value;
            LoadData();
            if (petID.HasValue && SelectedPetID == petID.Value)
                SetSelectedPetID(petID.Value);
            if (NewMedPetID == 0 && SelectedPetID > 0)
                NewMedPetID = SelectedPetID;

            // Deep link from /Health's "Edit medication" — opens straight into the real
            // edit form instead of duplicating it there.
            if (editMedID.HasValue)
            {
                var medication = Medications.FirstOrDefault(med => med.MedID == editMedID.Value);
                if (medication != null)
                {
                    PopulateEditMedFields(medication);
                    ModalToOpen = "editMedModal";
                }
            }
            return Page();
        }

        public IActionResult OnPostSelectPet(int selectedPetID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (selectedPetID != 0 && _petService.GetPetByID(UserID, selectedPetID) == null)
                return NotFound();
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
            RestoreStateFromSession();
            Pets = _petService.GetPetsByUser(UserID);

            if (NewMedPetID <= 0)
                return ShowMedicationModalError("addMedModal", "Choose a pet for this medication.");
            if (Pets.All(pet => pet.PetID != NewMedPetID))
                return NotFound();

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

            if (!_medService.AddMedication(UserID,
                NewMedPetID, NewMedName, NewMedDosage,
                NewMedFrequencyType, NewMedFrequencyInterval, NewMedTimingDoesNotMatter,
                NewMedStartDate, NewMedForever ? null : NewMedEndDate,
                NewMedNotes))
                return NotFound();

            SetSelectedPetID(NewMedPetID);
            TempData["StatusMessage"] = $"{NewMedName} added successfully!";
            return RedirectToPage();
        }

        // ── Edit Medication ──────────────────────────────────────────
        public IActionResult OnPostEditMedication()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            if (!_medService.OwnsMedication(userID, EditMedID))
                return NotFound();

            UserID = userID;
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

            if (!_medService.UpdateMedication(UserID,
                EditMedID, EditMedName, EditMedDosage,
                EditMedFrequencyType, EditMedFrequencyInterval, EditMedTimingDoesNotMatter,
                EditMedStartDate, EditMedForever ? null : EditMedEndDate,
                EditMedNotes))
                return NotFound();

            TempData["StatusMessage"] = $"{EditMedName} updated successfully!";
            return RedirectToPage();
        }

        // ── Delete Medication ────────────────────────────────────────
        public IActionResult OnPostDeleteMedication(int medID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            RestoreStateFromSession();
            if (!_medService.DeleteMedication(UserID, medID))
                return NotFound();

            TempData["StatusMessage"] = "Medication deleted.";
            return RedirectToPage();
        }

        // ── Confirm Schedule ─────────────────────────────────────────
        public IActionResult OnPostConfirmSchedule(
            int medID,
            DateTime logDate,
            DateTime confirmedAt,
            int utcOffsetMinutes,
            string? notes)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            RestoreStateFromSession();
            if (confirmedAt == default)
                return ShowMedicationModalError("confirmDoseModal", "Choose the administration date and time.");
            if (!_medService.ConfirmSchedule(
                    UserID,
                    medID,
                    logDate,
                    confirmedAt,
                    utcOffsetMinutes,
                    notes ?? string.Empty))
                return NotFound();

            TempData["StatusMessage"] = "Dose confirmed!";
            return RedirectToPage();
        }

        public IActionResult OnPostRecordDose(
            int medID,
            DateTime logDate,
            string status,
            DateTime? administeredAt,
            int utcOffsetMinutes,
            string? reason,
            string? notes)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            RestoreStateFromSession();
            if (!MedicationDoseStatuses.Recordable.Contains(status, StringComparer.OrdinalIgnoreCase))
                return BadRequest();
            if ((status.Equals(MedicationDoseStatuses.Skipped, StringComparison.OrdinalIgnoreCase)
                    || status.Equals(MedicationDoseStatuses.Missed, StringComparison.OrdinalIgnoreCase))
                && string.IsNullOrWhiteSpace(reason))
            {
                MedicationError = "Add a short reason for a skipped or manually missed dose.";
                LoadData();
                return Page();
            }
            if (!_medService.RecordDose(
                    UserID,
                    medID,
                    logDate,
                    status,
                    administeredAt,
                    utcOffsetMinutes,
                    reason ?? string.Empty,
                    notes ?? string.Empty))
                return NotFound();

            TempData["StatusMessage"] = $"Dose marked {status.ToLowerInvariant()}.";
            return RedirectToPage();
        }

        // ── Unconfirm Schedule ───────────────────────────────────────
        public IActionResult OnPostUnconfirmSchedule(int medID, DateTime logDate)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            RestoreStateFromSession();
            if (!_medService.UnconfirmSchedule(UserID, medID, logDate))
                return NotFound();

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
            UtcOffsetMinutes = _timeZone.GetUtcOffsetMinutes(Request);
            var household = _householdContext.GetActiveHousehold(UserID);
            CanManageCarePlans = household != null
                && HouseholdAccessRules.HasPermission(household.Role, HouseholdPermission.ManageCarePlans);
            Pets = _petService.GetPetsByUser(UserID);
            RestoreStateFromSession();

            if (SelectedPetID > 0 && Pets.All(pet => pet.PetID != SelectedPetID))
            {
                SetSelectedPetID(0);
            }

            if (SelectedPetID > 0)
            {
                Medications = _medService.GetMedicationsByPetID(UserID, SelectedPetID);
                Schedule    = _medService.GetScheduleByPetID(UserID, SelectedPetID, ShowAllTime, UtcOffsetMinutes);
            }
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

        private void PopulateEditMedFields(Medication medication)
        {
            EditMedID = medication.MedID;
            EditMedName = medication.MedicationName;
            EditMedDosage = medication.Dosage;
            EditMedFrequencyType = medication.FrequencyType;
            EditMedFrequencyInterval = medication.FrequencyInterval;
            EditMedTimingDoesNotMatter = medication.TimingDoesNotMatter;
            EditMedStartDate = medication.StartDate.Date;
            EditMedStartTime = medication.StartDate.TimeOfDay;
            EditMedEndDate = medication.EndDate;
            EditMedForever = !medication.EndDate.HasValue;
            EditMedNotes = medication.Notes;
        }
    }
}
