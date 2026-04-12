using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;

public class LoginModel : PageModel
{
    private readonly IConfiguration _configuration;

    public LoginModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [BindProperty]
    public string Username { get; set; }

    [BindProperty]
    public string Password { get; set; }

    public string ErrorMessage { get; set; }

    public IActionResult OnPost()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
{
    ErrorMessage = "Please enter your username and password.";
    return Page();
}
        string connectionString = 
            _configuration.GetConnectionString("DefaultConnection");

        string query = @"SELECT userID, name 
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
                    string userID = reader["userID"].ToString();
                    string name = reader["name"].ToString();

                    // Session
                    HttpContext.Session.SetString("userID", userID);
                    HttpContext.Session.SetString("name", name);

                    // Cookie
                    Response.Cookies.Append("userID", userID,
                        new CookieOptions { Expires = DateTimeOffset.Now.AddMinutes(60) });

                    Response.Cookies.Append("userName", name,
                        new CookieOptions { Expires = DateTimeOffset.Now.AddMinutes(60) });

                    return RedirectToPage("/Home");
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
}