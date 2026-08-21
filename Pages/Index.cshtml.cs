using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetPotty.Pages;

public class IndexModel : PageModel
{
    public bool IsLoggedIn => HttpContext.Session.GetString("userID") != null;

    public void OnGet()
    {

    }
}
