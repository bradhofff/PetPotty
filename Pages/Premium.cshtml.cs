using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetPotty.Pages;

public class PremiumComingSoonModel : PageModel
{
    public IActionResult OnGet(string? checkout)
    {
        if (!int.TryParse(HttpContext.Session.GetString("userID"), out _))
            return RedirectToPage("/Login", new { returnUrl = "/Premium" });
        var checkoutQuery = checkout is "success" or "cancelled" ? $"?checkout={checkout}" : string.Empty;
        return Redirect($"/Profile{checkoutQuery}#premium-settings");
    }
}
