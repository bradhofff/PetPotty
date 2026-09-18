using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PetPotty.Services;

public class LoginModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly IHouseholdService _households;

    public LoginModel(IConfiguration configuration, IHouseholdService households)
    {
        _configuration = configuration;
        _households = households;
    }

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string ErrorMessage { get; set; } = string.Empty;

    [BindProperty]
    public string InvitationToken { get; set; } = string.Empty;

    public IActionResult OnGet(string? invitationToken)
    {
        InvitationToken = invitationToken?.Trim() ?? string.Empty;
        if (!IsAuthenticated())
            return Page();
        return string.IsNullOrWhiteSpace(InvitationToken)
            ? RedirectToPage("/Home")
            : RedirectToPage("/HouseholdInvitation", new { token = InvitationToken });
    }

    public IActionResult OnPost()
    {
        if (IsAuthenticated())
        {
            return string.IsNullOrWhiteSpace(InvitationToken)
                ? RedirectToPage("/Home")
                : RedirectToPage("/HouseholdInvitation", new { token = InvitationToken });
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
{
    ErrorMessage = "Please enter your username and password.";
    return Page();
}
        string connectionString =
            _configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string not found.");

        string query = @"SELECT userID, name, DarkMode
                         FROM Users 
                         WHERE userName = @userName 
                         AND pass = @pass";

        using (SqlConnection conn = new SqlConnection(connectionString))
        using (SqlCommand cmd = new SqlCommand(query, conn))
        {
            cmd.Parameters.AddWithValue("@userName", Username.Trim());
            cmd.Parameters.AddWithValue("@pass", Password.Trim());

            try
            {
                conn.Open();
                SqlDataReader reader = cmd.ExecuteReader();

                if (reader.Read())
                {
                    string userID = reader.GetInt32(reader.GetOrdinal("userID")).ToString();
                    string name = reader["name"].ToString() ?? string.Empty;

                    // Session
                    HttpContext.Session.SetString("userID", userID);
                    HttpContext.Session.SetString("name", name);
                    HttpContext.Session.SetString(
                        "darkMode",
                        Convert.ToBoolean(reader["DarkMode"]) ? "true" : "false");

                    // Cookie — persistent for 30 days so mobile browsers don't log out on reopen
                    Response.Cookies.Append("userID", userID,
                        new CookieOptions
                        {
                            Expires  = DateTimeOffset.Now.AddDays(30),
                            HttpOnly = true,
                            SameSite = SameSiteMode.Lax
                        });

                    Response.Cookies.Append("userName", name,
                        new CookieOptions
                        {
                            Expires  = DateTimeOffset.Now.AddDays(30),
                            SameSite = SameSiteMode.Lax
                        });

                    _households.EnsurePersonalHousehold(Convert.ToInt32(userID));

                    return string.IsNullOrWhiteSpace(InvitationToken)
                        ? RedirectToPage("/Home")
                        : RedirectToPage("/HouseholdInvitation", new { token = InvitationToken });
                }
                else
                {
                    ErrorMessage = "Invalid username or password.";
                    return Page();
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = "Error: " + ex.Message;
                return Page();
            }
        }
    }

    private bool IsAuthenticated()
    {
        return !string.IsNullOrEmpty(HttpContext.Session.GetString("userID"));
    }
}
