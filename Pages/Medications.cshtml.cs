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

        public MedicationsModel(IPetService petService, IMedicationService medService)
        {
            _petService = petService;
            _medService = medService;
        }

        // ── Page state ──────────────────────────────────────────────
        public int UserID { get; set; }
        public List<Pet> Pets { get; set; } = new();
        public List<Medication> Medications { get; set; } = new();
        public List<MedSchedule> Schedule { get; set; } = new();

        [BindProperty(SupportsGet = true)] public int SelectedPetID { get; set; } = 0;
        [BindProperty(SupportsGet = true)] public bool ShowAllTime { get; set; } = false;

        // ── Add Medication fields ────────────────────────────────────
        [BindProperty] public string NewMedName { get; set; } = string.Empty;
        [BindProperty] public string NewMedDosage { get; set; } = string.Empty;
        [BindProperty] public string NewMedFrequencyType { get; set; } = string.Empty;
        [BindProperty] public int? NewMedFrequencyInterval { get; set; }
        [BindProperty] public DateTime NewMedStartDate { get; set; } = DateTime.Today;
        [BindProperty] public DateTime? NewMedEndDate { get; set; }
        [BindProperty] public bool NewMedForever { get; set; } = true;
        [BindProperty] public string NewMedNotes { get; set; } = string.Empty;

        // ── Edit Medication fields ───────────────────────────────────
        [BindProperty] public int EditMedID { get; set; }
        [BindProperty] public string EditMedName { get; set; } = string.Empty;
        [BindProperty] public string EditMedDosage { get; set; } = string.Empty;
        [BindProperty] public string EditMedFrequencyType { get; set; } = string.Empty;
        [BindProperty] public int? EditMedFrequencyInterval { get; set; }
        [BindProperty] public DateTime EditMedStartDate { get; set; } = DateTime.Today;
        [BindProperty] public DateTime? EditMedEndDate { get; set; }
        [BindProperty] public bool EditMedForever { get; set; }
        [BindProperty] public string EditMedNotes { get; set; } = string.Empty;

        // ── GET ──────────────────────────────────────────────────────
        public IActionResult OnGet()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            LoadData();
            return Page();
        }

        // ── Add Medication ───────────────────────────────────────────
        public IActionResult OnPostAddMedication()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;

            if (!NewMedForever)
            {
                if (!NewMedEndDate.HasValue)
                {
                    TempData["StatusMessage"] = "Error: End date is required when Forever is unchecked.";
                    return RedirectToPage(new { selectedPetID = SelectedPetID, showAllTime = ShowAllTime });
                }
                if (NewMedEndDate.Value <= NewMedStartDate)
                {
                    TempData["StatusMessage"] = "Error: End date must be after start date.";
                    return RedirectToPage(new { selectedPetID = SelectedPetID, showAllTime = ShowAllTime });
                }
            }

            _medService.AddMedication(
                SelectedPetID, NewMedName, NewMedDosage,
                NewMedFrequencyType, NewMedFrequencyInterval,
                NewMedStartDate, NewMedForever ? null : NewMedEndDate,
                NewMedNotes);

            TempData["StatusMessage"] = $"{NewMedName} added successfully!";
            return RedirectToPage(new { selectedPetID = SelectedPetID, showAllTime = ShowAllTime });
        }

        // ── Edit Medication ──────────────────────────────────────────
        public IActionResult OnPostEditMedication()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;

            if (!EditMedForever)
            {
                if (!EditMedEndDate.HasValue)
                {
                    TempData["StatusMessage"] = "Error: End date is required when Forever is unchecked.";
                    return RedirectToPage(new { selectedPetID = SelectedPetID, showAllTime = ShowAllTime });
                }
                if (EditMedEndDate.Value <= EditMedStartDate)
                {
                    TempData["StatusMessage"] = "Error: End date must be after start date.";
                    return RedirectToPage(new { selectedPetID = SelectedPetID, showAllTime = ShowAllTime });
                }
            }

            _medService.UpdateMedication(
                EditMedID, EditMedName, EditMedDosage,
                EditMedFrequencyType, EditMedFrequencyInterval,
                EditMedStartDate, EditMedForever ? null : EditMedEndDate,
                EditMedNotes);

            TempData["StatusMessage"] = $"{EditMedName} updated successfully!";
            return RedirectToPage(new { selectedPetID = SelectedPetID, showAllTime = ShowAllTime });
        }

        // ── Delete Medication ────────────────────────────────────────
        public IActionResult OnPostDeleteMedication(int medID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            _medService.DeleteMedication(medID);

            TempData["StatusMessage"] = "Medication deleted.";
            return RedirectToPage(new { selectedPetID = SelectedPetID, showAllTime = ShowAllTime });
        }

        // ── Confirm Schedule ─────────────────────────────────────────
        public IActionResult OnPostConfirmSchedule(int medID, DateTime logDate)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            _medService.ConfirmSchedule(medID, logDate);

            TempData["StatusMessage"] = "Dose confirmed!";
            return RedirectToPage(new { selectedPetID = SelectedPetID, showAllTime = ShowAllTime });
        }

        // ── Unconfirm Schedule ───────────────────────────────────────
        public IActionResult OnPostUnconfirmSchedule(int medID, DateTime logDate)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            _medService.UnconfirmSchedule(medID, logDate);

            TempData["StatusMessage"] = "Dose unconfirmed.";
            return RedirectToPage(new { selectedPetID = SelectedPetID, showAllTime = ShowAllTime });
        }

        // ── Helpers ──────────────────────────────────────────────────
        private void LoadData()
        {
            Pets = _petService.GetPetsByUser(UserID);

            if (SelectedPetID > 0)
            {
                Medications = _medService.GetMedicationsByPetID(SelectedPetID);
                Schedule    = _medService.GetScheduleByPetID(SelectedPetID, ShowAllTime);
            }
        }
    }
}
