using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages;

public class PremiumModel : PageModel
{
    private readonly IHouseholdContextService _householdContext;
    private readonly IPremiumService _premium;
    private readonly IConfiguration _configuration;

    public PremiumModel(
        IHouseholdContextService householdContext,
        IPremiumService premium,
        IConfiguration configuration)
    {
        _householdContext = householdContext;
        _premium = premium;
        _configuration = configuration;
    }

    public HouseholdContext? Household { get; private set; }
    public HouseholdPremiumStatus? Status { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? StatusMessage { get; private set; }

    public IActionResult OnGet(string? checkout, bool locked = false)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login", new { returnUrl = "/Premium" });

        Load(userID);
        if (Household == null || Status == null)
            return RedirectToPage("/Home");

        if (locked)
            StatusMessage = "Premium is required for that feature. Upgrade this household to unlock it.";
        else if (checkout == "success")
            StatusMessage = Status.IsPremium
                ? "Premium is active for this household."
                : "Payment received. Premium will unlock as soon as Stripe confirms the subscription.";
        else if (checkout == "cancelled")
            StatusMessage = "Checkout was cancelled. No changes were made.";

        return Page();
    }

    public async Task<IActionResult> OnPostStartCheckoutAsync()
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login", new { returnUrl = "/Premium" });

        var household = _householdContext.GetActiveHousehold(userID);
        if (household == null)
            return RedirectToPage("/Home");

        var baseUrl = _configuration["App:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

        try
        {
            var session = await _premium.CreateCheckoutSessionAsync(userID, household, baseUrl, HttpContext.RequestAborted);
            return Redirect(session.Url);
        }
        catch (InvalidOperationException ex)
        {
            Load(userID);
            ErrorMessage = ex.Message;
            return Page();
        }
    }

    private void Load(int userID)
    {
        Household = _householdContext.GetActiveHousehold(userID);
        Status = Household == null ? null : _premium.GetStatus(userID, Household.HouseholdID);
    }

    private bool TryGetUserID(out int userID) =>
        int.TryParse(HttpContext.Session.GetString("userID"), out userID);
}
