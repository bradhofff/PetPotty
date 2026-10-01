using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetPotty.Pages;

public class PremiumRedirectModel : PageModel
{
    public IActionResult OnGet(string? checkout, bool locked = false)
    {
        return RedirectToPage("/Purchase", new
        {
            checkout = checkout is "success" or "cancelled" ? checkout : null,
            locked = locked ? true : (bool?)null
        });
    }
}
