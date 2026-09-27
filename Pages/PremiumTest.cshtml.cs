using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Services;

namespace PetPotty.Pages;

public class PremiumTestModel : PageModel
{
    private readonly IHouseholdContextService _householdContext;
    private readonly IPremiumService _premium;

    public PremiumTestModel(IHouseholdContextService householdContext, IPremiumService premium)
    {
        _householdContext = householdContext;
        _premium = premium;
    }

    public string HouseholdName { get; private set; } = string.Empty;

    public IActionResult OnGet()
    {
        if (!int.TryParse(HttpContext.Session.GetString("userID"), out var userID))
            return RedirectToPage("/Login", new { returnUrl = "/PremiumTest" });

        var household = _householdContext.GetActiveHousehold(userID);
        var status = household == null ? null : _premium.GetStatus(userID, household.HouseholdID);
        if (household == null || status?.IsPremium != true)
            return StatusCode(StatusCodes.Status403Forbidden);

        HouseholdName = household.Name;
        return Page();
    }
}
