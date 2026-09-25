using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PetPotty.Billing;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages;

public sealed class BillingModel(IHouseholdContextService context, HouseholdBillingService billing,
    IOptions<StripeOptions> options, TimeProvider clock, ILogger<BillingModel> logger) : PageModel
{
    public HouseholdContext? Household { get; private set; }
    public HouseholdBilling Account { get; private set; } = new();
    public BillingView Current => Account.Resolve(clock.GetUtcNow());
    public bool IsOwner => Household?.Role == HouseholdRole.Owner;
    public bool Configured => options.Value.IsConfigured;
    public string PriceLabel(BillingPlan plan) => options.Value.Price(plan).PriceLabel;
    [TempData] public string? BillingMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(string? checkout, CancellationToken ct)
    {
        if (!TryContext(out var userId)) return RedirectToPage("/Login");
        if (Household == null) return RedirectToPage("/Profile");
        Account = await billing.GetAsync(userId, Household.HouseholdID, ct);
        if (checkout == "returned") BillingMessage = "Checkout has returned. Your plan updates after payment confirmation. Refresh to see the latest status.";
        if (checkout == "canceled") BillingMessage = "Checkout was closed. You have not activated a plan by returning here.";
        return Page();
    }

    public Task<IActionResult> OnPostCheckoutAsync(Guid householdPublicId, BillingPlan plan, CancellationToken ct) =>
        ActAsync(householdPublicId, (userId, household) => billing.CheckoutAsync(userId, household, plan, ct));

    public Task<IActionResult> OnPostPortalAsync(Guid householdPublicId, CancellationToken ct) =>
        ActAsync(householdPublicId, (userId, household) => billing.PortalAsync(userId, household.HouseholdID, ct));

    private async Task<IActionResult> ActAsync(Guid expectedHousehold, Func<int, HouseholdContext, Task<string>> action)
    {
        if (!TryContext(out var userId)) return RedirectToPage("/Login");
        if (Household == null || Household.PublicID != expectedHousehold || !IsOwner) return StatusCode(403);
        if (!ModelState.IsValid) return BadRequest();
        try { return Redirect(await action(userId, Household)); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
        catch (BillingException exception) { BillingMessage = exception.Message; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Billing action failed for household {HouseholdID}", Household.HouseholdID);
            BillingMessage = "Billing could not be reached. Please try again shortly. An existing checkout will be reused.";
        }
        return RedirectToPage();
    }

    private bool TryContext(out int userId)
    {
        if (!int.TryParse(HttpContext.Session.GetString("userID"), out userId) || userId <= 0) return false;
        Household = context.GetActiveHousehold(userId);
        return true;
    }
}
