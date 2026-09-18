using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages
{
    // Internal report renderer reached only from Health — deliberately not a
    // primary navigation destination.
    public class ReportsModel : PageModel
    {
        private readonly IPetService _petService;
        private readonly IHealthService _healthService;
        private readonly IMedicationService _medicationService;
        private readonly IVetVisitService _vetVisitService;
        private readonly IHouseholdContextService _householdContext;
        private readonly IUserTimeZoneService _timeZone;
        private readonly int _missedAfterMinutes;

        public ReportsModel(
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
            _missedAfterMinutes = Math.Clamp(configuration.GetValue<int?>("MedicationAdherence:MissedAfterMinutes") ?? 720, 1, 10080);
        }

        public HealthReport? Report { get; set; }
        public string? ErrorMessage { get; set; }
        public int UtcOffsetMinutes { get; set; }

        public IActionResult OnGet(int petID, int? days, DateTime? start, DateTime? end)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            var household = _householdContext.GetActiveHousehold(HttpContext.Session, userID);
            if (household == null)
                return RedirectToPage("/Login");

            var pet = _petService.GetPetByID(userID, household.HouseholdID, petID);
            if (pet == null)
                return Forbid();

            UtcOffsetMinutes = _timeZone.GetRequestOffsetMinutes(HttpContext);

            DateTime startLocal, endLocal;
            string label;
            if (start.HasValue && end.HasValue)
            {
                if (end.Value.Date < start.Value.Date || (end.Value.Date - start.Value.Date).TotalDays > 366)
                {
                    ErrorMessage = "Choose a custom range of up to one year, with the end date on or after the start date.";
                    return Page();
                }
                startLocal = start.Value.Date;
                endLocal = end.Value.Date.AddDays(1);
                label = $"{startLocal:MMM d, yyyy} – {end.Value.Date:MMM d, yyyy}";
            }
            else
            {
                var rangeDays = Math.Clamp(days ?? 30, 1, 366);
                endLocal = DateTime.Today.AddDays(1);
                startLocal = endLocal.AddDays(-rangeDays);
                label = $"Last {rangeDays} days";
            }

            var startUtc = startLocal.ToUniversalTime();
            var endUtc = endLocal.ToUniversalTime();

            var events = _healthService.GetHealthEvents(userID, household.HouseholdID, petID, startUtc, endUtc, null);
            var medications = _medicationService.GetMedicationsByPetID(userID, household.HouseholdID, petID);
            var schedule = _medicationService.GetScheduleByPetID(userID, household.HouseholdID, petID, true)
                .Where(s => s.ScheduleDate >= startLocal && s.ScheduleDate < endLocal)
                .ToList();
            var visits = _vetVisitService.GetVisits(userID, household.HouseholdID, petID)
                .Where(v => v.VisitDate >= startLocal && v.VisitDate < endLocal)
                .ToList();

            var now = DateTime.Now;
            Report = new HealthReport
            {
                PetName = pet.Name,
                PetType = pet.Type,
                PetBreed = pet.Breed,
                Range = new HealthReportRange { StartUtc = startUtc, EndUtc = endUtc, StartLocal = startLocal, EndLocal = endLocal, Label = label },
                CurrentMedications = medications,
                Adherence = new DoseAdherenceSummary
                {
                    Total = schedule.Count,
                    Taken = schedule.Count(s => s.DoseStatus == "Taken"),
                    TakenLate = schedule.Count(s => s.DoseStatus == "Taken late"),
                    Skipped = schedule.Count(s => s.DoseStatus == "Skipped"),
                    Missed = schedule.Count(s => MedicationAdherence.EffectiveStatus(
                        s.DoseStatus, s.ScheduleDate,
                        medications.FirstOrDefault(m => m.MedID == s.MedID)?.TimingDoesNotMatter ?? false,
                        _missedAfterMinutes, now) == "Missed")
                },
                Symptoms = events.Where(e => e.EventKind == HealthEventKinds.Symptom).OrderBy(e => e.OccurredAtUtc).ToList(),
                Incidents = events.Where(e => e.EventKind == HealthEventKinds.Incident).OrderBy(e => e.OccurredAtUtc).ToList(),
                VetVisits = visits.OrderBy(v => v.VisitDate).ToList()
            };

            return Page();
        }
    }
}
