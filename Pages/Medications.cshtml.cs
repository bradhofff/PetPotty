using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages
{
    public class MedicationsModel : PageModel
    {
        private const string FocusMedicationSessionKey = "medications-focus-medication";
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
        public IActionResult OnGet()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            UtcOffsetMinutes = _timeZone.GetUtcOffsetMinutes(Request);
            var editMedID = HttpContext.Session.GetInt32(FocusMedicationSessionKey);
            HttpContext.Session.Remove(FocusMedicationSessionKey);
            Medication? medication = null;
            if (editMedID.HasValue)
            {
                medication = FindAccessibleMedication(editMedID.Value);
                if (medication == null) return NotFound();
                SetSelectedPetID(medication.PetID);
            }
            LoadData();
            if (NewMedPetID == 0 && SelectedPetID > 0)
                NewMedPetID = SelectedPetID;
            if (medication != null)
            {
                PopulateEditMedFields(medication);
                ModalToOpen = "editMedModal";
            }
            return Page();
        }

        public IActionResult OnPostReviewDose(int petID, int scheduleID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out var userID)) return RedirectToPage("/Login");
            if (_petService.GetPetByID(userID, petID) == null) return NotFound();
            var dose = _medService.GetScheduleByPetID(userID, petID, true, _timeZone.GetUtcOffsetMinutes(Request))
                .FirstOrDefault(s => s.ScheduleID == scheduleID);
            if (dose == null) return NotFound();
            UserID = userID; SetSelectedPetID(petID);
            HttpContext.Session.SetString("medicationsShowAllTime", "True");
            TempData["StatusMessage"] = $"Review {dose.MedicationName} on {dose.ScheduleDate:MMM d, yyyy}."
                + (dose.DoseStatus is MedicationDoseStatuses.Skipped or MedicationDoseStatuses.Missed
                    ? " To correct a skipped or missed record, use Undo for that dose, then record Taken with the actual administration time." : "");
            return Redirect($"/Medications#dose-{dose.ScheduleID}");
        }

        public IActionResult OnPostOpenMedication(int petID, int? medicationID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out var userID))
                return RedirectToPage("/Login");
            if (_petService.GetPetByID(userID, petID) == null)
                return NotFound();
            if (medicationID.HasValue)
            {
                var medication = _medService.GetMedicationsByPetID(userID, petID)
                    .FirstOrDefault(item => item.MedID == medicationID.Value);
                if (medication == null)
                    return NotFound();
            }

            UserID = userID;
            SetSelectedPetID(petID);
            if (medicationID.HasValue)
                HttpContext.Session.SetInt32(FocusMedicationSessionKey, medicationID.Value);
            return RedirectToPage();
        }

        public IActionResult OnPostSelectPet(int selectedPetID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            if (ModelState.TryGetValue("selectedPetID", out var selectionState) && selectionState.Errors.Count > 0)
                return NotFound();
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
            SetSelectedPetID(NewMedPetID);

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
            var medication = FindAccessibleMedication(EditMedID);
            if (medication == null) return NotFound();
            SetSelectedPetID(medication.PetID);

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
            var medication = FindAccessibleMedication(medID);
            if (medication == null) return NotFound();
            SetSelectedPetID(medication.PetID);
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
            string? notes, bool dashboardRequest = false)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return dashboardRequest ? StatusCode(401, new { message = "Your session expired. Reload and sign in." }) : RedirectToPage("/Login");

            UserID = userID;
            if (new[] { "medID", "logDate", "confirmedAt", "administeredAt", "utcOffsetMinutes" }.Any(key => ModelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0) || logDate == default || utcOffsetMinutes is < -840 or > 840)
                return BadRequest(new { message = "Check the dose date, time and outcome." });
            var medication = FindAccessibleMedication(medID);
            if (medication == null) return NotFound();
            SetSelectedPetID(medication.PetID);
            RestoreStateFromSession();
            if (confirmedAt == default)
                return dashboardRequest ? BadRequest(new { message = "Choose the administration date and time." }) : ShowMedicationModalError("confirmDoseModal", "Choose the administration date and time.");
            try
            {
            if (!_medService.ConfirmSchedule(
                    UserID,
                    medID,
                    logDate,
                    confirmedAt,
                    utcOffsetMinutes,
                    notes ?? string.Empty))
                return NotFound(new { message = "Unable to save this dose. Refresh the schedule and check your access." });
            }
            catch (Exception) when (dashboardRequest)
            {
                return StatusCode(500, new { message = "Could not confirm the save. Check the schedule before trying again." });
            }

            TempData["StatusMessage"] = "Dose confirmed!";
            return dashboardRequest ? new JsonResult(new { saved = true }) : RedirectToPage();
        }

        public IActionResult OnPostRecordDose(
            int medID,
            DateTime logDate,
            string status,
            DateTime? administeredAt,
            int utcOffsetMinutes,
            string? reason,
            string? notes, bool dashboardRequest = false)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return dashboardRequest ? StatusCode(401, new { message = "Your session expired. Reload and sign in." }) : RedirectToPage("/Login");

            UserID = userID;
            if (new[] { "medID", "logDate", "confirmedAt", "administeredAt", "utcOffsetMinutes" }.Any(key => ModelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0) || logDate == default || utcOffsetMinutes is < -840 or > 840)
                return BadRequest(new { message = "Check the dose date, time and outcome." });
            var medication = FindAccessibleMedication(medID);
            if (medication == null) return NotFound();
            SetSelectedPetID(medication.PetID);
            RestoreStateFromSession();
            if (!MedicationDoseStatuses.Recordable.Contains(status, StringComparer.OrdinalIgnoreCase))
                return BadRequest();
            if ((status.Equals(MedicationDoseStatuses.Skipped, StringComparison.OrdinalIgnoreCase)
                    || status.Equals(MedicationDoseStatuses.Missed, StringComparison.OrdinalIgnoreCase))
                && string.IsNullOrWhiteSpace(reason))
            {
                if (dashboardRequest) return BadRequest(new { message = "Add a reason for a skipped or missed dose." });
                MedicationError = "Add a short reason for a skipped or manually missed dose.";
                LoadData();
                return Page();
            }
            try
            {
            if (!_medService.RecordDose(
                    UserID,
                    medID,
                    logDate,
                    status,
                    administeredAt,
                    utcOffsetMinutes,
                    reason ?? string.Empty,
                    notes ?? string.Empty))
                return NotFound(new { message = "Unable to save this dose. Refresh the schedule and check your access." });
            }
            catch (Exception) when (dashboardRequest)
            {
                return StatusCode(500, new { message = "Could not confirm the save. Check the schedule before trying again." });
            }

            TempData["StatusMessage"] = $"Dose marked {status.ToLowerInvariant()}.";
            return dashboardRequest ? new JsonResult(new { saved = true }) : RedirectToPage();
        }

        // ── Unconfirm Schedule ───────────────────────────────────────
        public IActionResult OnPostUnconfirmSchedule(int medID, DateTime logDate)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            var medication = FindAccessibleMedication(medID);
            if (medication == null) return NotFound();
            SetSelectedPetID(medication.PetID);
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

            if (SelectedPetID > 0)
            {
                Medications = _medService.GetMedicationsByPetID(UserID, SelectedPetID);
                Schedule    = _medService.GetScheduleByPetID(UserID, SelectedPetID, ShowAllTime, UtcOffsetMinutes);
            }
        }

        private void RestoreStateFromSession()
        {
            SelectedPetID = SelectedPetPreference.Get(HttpContext.Session, _petService.GetPetsByUser(UserID)) ?? 0;

            ShowAllTime = bool.TryParse(HttpContext.Session.GetString("medicationsShowAllTime"), out var showAllTime) && showAllTime;
        }

        private void SetSelectedPetID(int selectedPetID)
        {
            if (SelectedPetPreference.TrySelect(HttpContext.Session, _petService.GetPetsByUser(UserID), selectedPetID))
                SelectedPetID = selectedPetID;
        }

        private Medication? FindAccessibleMedication(int medID) =>
            _petService.GetPetsByUser(UserID)
                .SelectMany(pet => _medService.GetMedicationsByPetID(UserID, pet.PetID))
                .FirstOrDefault(med => med.MedID == medID);

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
