using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetPotty.Pages;

public class PremiumComingSoonModel(IConfiguration configuration) : PageModel
{
    public IReadOnlyList<PremiumPreviewPrice> Prices { get; private set; } = [];

    public IActionResult OnGet()
    {
        if (!int.TryParse(HttpContext.Session.GetString("userID"), out _))
            return RedirectToPage("/Login", new { returnUrl = "/Premium" });

        // Use the same plan names and price labels as the hidden Premiumm page.
        // This page only displays them; it does not start checkout.
        Prices = configuration.GetSection("Stripe:Plans").GetChildren()
            .Select(section => new PremiumPreviewPrice(
                section.Key,
                section["Name"]?.Trim() is { Length: > 0 } name ? name : section.Key,
                section["PriceLabel"]?.Trim() ?? string.Empty,
                section.Key.ToLowerInvariant() switch
                {
                    "month" => "per month",
                    "year" => "per year",
                    "lifetime" => "one time",
                    _ => string.Empty
                }))
            .Where(plan => !string.IsNullOrWhiteSpace(plan.PriceLabel))
            .OrderBy(plan => plan.Key.ToLowerInvariant() switch
            {
                "month" => 0,
                "year" => 1,
                "lifetime" => 2,
                _ => 3
            })
            .ToArray();

        return Page();
    }
}

public sealed record PremiumPreviewPrice(string Key, string Name, string PriceLabel, string BillingLabel);
