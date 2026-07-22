using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetPotty.Pages
{
    public class VetVisitsModel : PageModel
    {
        public IActionResult OnGet()
        {
            if (HttpContext.Session.GetString("userID") == null)
                return RedirectToPage("/Login");

            return Page();
        }
    }
}
