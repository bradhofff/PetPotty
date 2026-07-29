using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using System.Data;

namespace PetPotty.Pages
{
    public class ThemeModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public ThemeModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public IActionResult OnGet() => RedirectToPage("/Home");

        public IActionResult OnPost(string? returnUrl)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out var userID))
                return RedirectToPage("/Login");

            var darkMode = HttpContext.Session.GetString("darkMode") != "true";
            using var connection = new SqlConnection(
                _configuration.GetConnectionString("DefaultConnection"));
            using var command = new SqlCommand(
                "UPDATE dbo.Users SET DarkMode = @DarkMode WHERE userID = @UserID",
                connection);
            command.Parameters.Add("@DarkMode", SqlDbType.Bit).Value = darkMode;
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            connection.Open();
            if (command.ExecuteNonQuery() != 1)
                return Forbid();

            HttpContext.Session.SetString("darkMode", darkMode ? "true" : "false");
            return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
                ? LocalRedirect(returnUrl)
                : RedirectToPage("/Home");
        }
    }
}
