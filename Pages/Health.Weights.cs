using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages;

public sealed partial class HealthModel
{
    [BindProperty] public PetWeightInput WeightEntry { get; set; } = new();
    public List<PetWeightEntry> WeightEntries { get; set; } = [];
    public string? WeightLoadError { get; set; }
    public string? WeightSaveError { get; set; }
    public bool OpenWeightForm { get; set; }
    public bool CanRecordWeight { get; set; }

    private void LoadWeights(int userID)
    {
        if (!PetID.HasValue) return;
        var household = householdContext.GetActiveHousehold(userID);
        CanRecordWeight = household != null && HouseholdAccessRules.HasPermission(household.Role, HouseholdPermission.RecordCare);
        try { WeightEntries = petWeightService.GetWeights(userID, PetID.Value); }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Unable to load weights for pet {PetID}", PetID);
            WeightLoadError = "Weight history could not be loaded. Please try again.";
        }
        if (!OpenWeightForm)
            WeightEntry = new()
            {
                PetID = PetID.Value, Unit = WeightEntries.FirstOrDefault()?.Unit ?? "lb",
                MeasuredAtLocal = Local(DateTime.UtcNow), UtcOffsetMinutes = UtcOffsetMinutes
            };
    }

    public IActionResult OnPostSaveWeight()
    {
        if (!TryGetUserID(out var userID)) return RedirectToPage("/Login");
        if (petService.GetPetByID(userID, WeightEntry.PetID) == null) return NotFound();
        var household = householdContext.GetActiveHousehold(userID);
        if (household == null || !HouseholdAccessRules.HasPermission(household.Role, HouseholdPermission.RecordCare))
            return StatusCode(403);
        // Preserve binding errors (e.g. an invalid decimal) while removing unrelated forms' validation.
        foreach (var key in ModelState.Keys.Where(k => k != nameof(WeightEntry) && !k.StartsWith(nameof(WeightEntry) + ".", StringComparison.Ordinal)).ToArray())
            ModelState.Remove(key);
        OpenWeightForm = true;
        if (!SelectedPetPreference.TrySelect(HttpContext.Session, petService.GetPetsByUser(userID), WeightEntry.PetID)) return NotFound();
        if (ModelState.IsValid)
        {
            try
            {
                if (!petWeightService.SaveWeight(userID, WeightEntry)) return NotFound();
                TempData["StatusMessage"] = WeightEntry.WeightEntryID == 0 ? "Weight recorded." : "Weight updated.";
                return Redirect("/Health#pet-weight");
            }
            catch (SqlException ex)
            {
                logger.LogError(ex, "Unable to save weight for pet {PetID}", WeightEntry.PetID);
                WeightSaveError = "Weight could not be saved. Your entries are still here. Please try again.";
            }
        }
        var result = LoadPage(userID);
        return result ?? Page();
    }

    public IActionResult OnPostOpenWeight(int petID, int weightEntryID)
    {
        if (!TryGetUserID(out var userID)) return RedirectToPage("/Login");
        if (petID <= 0 || weightEntryID <= 0 || !SelectedPetPreference.TrySelect(HttpContext.Session, petService.GetPetsByUser(userID), petID)) return NotFound();
        var result = LoadPage(userID);
        if (result != null) return result;
        if (!CanRecordWeight) return StatusCode(403);
        if (WeightLoadError != null) return Page();
        var entry = WeightEntries.FirstOrDefault(w => w.WeightEntryID == weightEntryID);
        if (entry == null) return NotFound();
        ModelState.Clear();
        WeightEntry = new()
        {
            WeightEntryID = entry.WeightEntryID, PetID = petID, Weight = entry.Weight,
            Unit = entry.Unit, MeasuredAtLocal = Local(entry.MeasuredAtUtc), Notes = entry.Notes,
            UtcOffsetMinutes = UtcOffsetMinutes
        };
        OpenWeightForm = true;
        return Page();
    }

    public IActionResult OnPostDeleteWeight(int petID, int weightEntryID)
    {
        if (!TryGetUserID(out var userID)) return RedirectToPage("/Login");
        if (petID <= 0 || weightEntryID <= 0 || petService.GetPetByID(userID, petID) == null) return NotFound();
        var household = householdContext.GetActiveHousehold(userID);
        if (household == null || !HouseholdAccessRules.HasPermission(household.Role, HouseholdPermission.RecordCare)) return StatusCode(403);
        if (!SelectedPetPreference.TrySelect(HttpContext.Session, petService.GetPetsByUser(userID), petID)) return NotFound();
        try
        {
            if (!petWeightService.DeleteWeight(userID, petID, weightEntryID)) return NotFound();
            TempData["StatusMessage"] = "Weight entry deleted.";
            return Redirect("/Health#pet-weight");
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Unable to delete weight entry {WeightEntryID}", weightEntryID);
            WeightSaveError = "Weight entry could not be deleted. Please try again.";
            var result = LoadPage(userID);
            return result ?? Page();
        }
    }
}
