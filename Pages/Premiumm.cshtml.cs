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
    public IReadOnlyList<PremiumPlan> Plans { get; private set; } = [];
    [BindProperty]
    public string? SelectedPlanKey { get; set; }

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

        LoadPlans();
        var selectedPlan = Plans.FirstOrDefault(plan =>
            string.Equals(plan.Key, SelectedPlanKey, StringComparison.OrdinalIgnoreCase));
        if (selectedPlan == null)
        {
            Load(userID);
            ErrorMessage = "Choose one of the configured Premium plans.";
            return Page();
        }

        var baseUrl = _configuration["App:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

        try
        {
            var planType = selectedPlan.Key.ToLowerInvariant() switch
            {
                "month" or "monthly" => "monthly",
                "year" or "yearly" => "yearly",
                "lifetime" => "lifetime",
                _ => throw new InvalidOperationException("The selected Premium plan has an invalid plan type.")
            };
            var session = await _premium.CreateCheckoutSessionAsync(userID, household,
                selectedPlan.PriceId, planType, selectedPlan.Mode, baseUrl, HttpContext.RequestAborted);
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
        LoadPlans();
        Household = _householdContext.GetActiveHousehold(userID);
        Status = Household == null ? null : _premium.GetStatus(userID, Household.HouseholdID);
    }

    private void LoadPlans()
    {
        Plans = _configuration.GetSection("Stripe:Plans").GetChildren()
            .Select(section => new PremiumPlan(
                section.Key,
                section["Name"]?.Trim() is { Length: > 0 } name ? name : section.Key,
                section["PriceLabel"]?.Trim() ?? string.Empty,
                section["Description"]?.Trim() ?? string.Empty,
                section["PriceId"]?.Trim() ?? string.Empty,
                ResolveCheckoutMode(section)))
            .Where(plan => !string.IsNullOrWhiteSpace(plan.PriceId))
            .ToArray();
    }

    private static string ResolveCheckoutMode(IConfigurationSection section)
    {
        var configuredMode = section["Mode"]?.Trim();
        if (string.Equals(configuredMode, "payment", StringComparison.OrdinalIgnoreCase))
            return "payment";
        if (string.Equals(configuredMode, "subscription", StringComparison.OrdinalIgnoreCase))
            return "subscription";
        if (!string.IsNullOrWhiteSpace(configuredMode))
            return "invalid";

        var planDescription = $"{section.Key} {section["Name"]} {section["Description"]}";
        return planDescription.Contains("lifetime", StringComparison.OrdinalIgnoreCase)
            ? "payment" : "subscription";
    }

    private bool TryGetUserID(out int userID) =>
        int.TryParse(HttpContext.Session.GetString("userID"), out userID);
}
