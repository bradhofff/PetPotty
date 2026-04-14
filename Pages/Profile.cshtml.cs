using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using System.Data;

namespace PetPotty.Pages
{
    public class ProfileModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public ProfileModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        // ── Read-only display state ──────────────────────────────────
        public string ActiveTab { get; set; } = "Profile";

        // ── Profile fields ───────────────────────────────────────────
        [BindProperty] public string ProfileName     { get; set; } = string.Empty;
        [BindProperty] public string ProfileUserName { get; set; } = string.Empty;
        [BindProperty] public string ProfileEmail    { get; set; } = string.Empty;
        [BindProperty] public string ProfilePhone    { get; set; } = string.Empty;

        // ── Password change (optional — only submitted if non-empty) ─
        [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
        [BindProperty] public string NewPassword     { get; set; } = string.Empty;
        [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;

        public string? StatusMessage { get; set; }
        public string? ErrorMessage  { get; set; }

        // ── GET ──────────────────────────────────────────────────────
        public IActionResult OnGet()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            LoadProfile(userID);
            return Page();
        }

        // ── POST: Save profile fields ────────────────────────────────
        public IActionResult OnPostAsync()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            string connStr = _configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string not found.");

            // ── Handle password change if new password was provided ──
            if (!string.IsNullOrWhiteSpace(NewPassword))
            {
                if (NewPassword != ConfirmPassword)
                {
                    ErrorMessage = "New passwords do not match.";
                    LoadProfile(userID);
                    return Page();
                }

                // Verify current password
                if (!VerifyCurrentPassword(connStr, userID, CurrentPassword))
                {
                    ErrorMessage = "Current password is incorrect.";
                    LoadProfile(userID);
                    return Page();
                }

                // Update password
                using var connPw = new SqlConnection(connStr);
                using var cmdPw  = new SqlCommand("UPDATE Users SET pass = @pass WHERE userID = @userID", connPw);
                cmdPw.Parameters.AddWithValue("@pass",   NewPassword);
                cmdPw.Parameters.AddWithValue("@userID", userID);
                connPw.Open();
                cmdPw.ExecuteNonQuery();
            }

            // ── Update profile fields ────────────────────────────────
            using var conn = new SqlConnection(connStr);
            using var cmd  = new SqlCommand("UpdateUser", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@userID",   userID);
            cmd.Parameters.AddWithValue("@name",     ProfileName.Trim());
            cmd.Parameters.AddWithValue("@userName", ProfileUserName.Trim());
            cmd.Parameters.AddWithValue("@email",    ProfileEmail.Trim());
            cmd.Parameters.AddWithValue("@phone",    string.IsNullOrWhiteSpace(ProfilePhone) ? string.Empty : ProfilePhone.Trim());

            try
            {
                conn.Open();
                cmd.ExecuteNonQuery();

                // Keep session name in sync if it changed
                HttpContext.Session.SetString("name", ProfileName.Trim());

                TempData["ProfileStatus"] = "Profile updated successfully!";
                return RedirectToPage();
            }
            catch (SqlException ex)
            {
                if (ex.Message.Contains("UQ_Users_UserName"))
                    ErrorMessage = "That username is already taken.";
                else if (ex.Message.Contains("UQ_Users_Email"))
                    ErrorMessage = "That email is already registered.";
                else
                    ErrorMessage = "An error occurred: " + ex.Message;

                LoadProfile(userID);
                return Page();
            }
        }

        // ── Helpers ──────────────────────────────────────────────────
        private void LoadProfile(int userID)
        {
            string connStr = _configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string not found.");

            using var conn = new SqlConnection(connStr);
            using var cmd  = new SqlCommand(
                "SELECT name, userName, email, phone FROM Users WHERE userID = @userID", conn);
            cmd.Parameters.AddWithValue("@userID", userID);
            conn.Open();
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                ProfileName     = reader["name"].ToString()     ?? string.Empty;
                ProfileUserName = reader["userName"].ToString() ?? string.Empty;
                ProfileEmail    = reader["email"].ToString()    ?? string.Empty;
                ProfilePhone    = reader["phone"].ToString()    ?? string.Empty;
            }
        }

        private bool VerifyCurrentPassword(string connStr, int userID, string password)
        {
            using var conn = new SqlConnection(connStr);
            using var cmd  = new SqlCommand(
                "SELECT COUNT(1) FROM Users WHERE userID = @userID AND pass = @pass", conn);
            cmd.Parameters.AddWithValue("@userID", userID);
            cmd.Parameters.AddWithValue("@pass",   password);
            conn.Open();
            return (int)cmd.ExecuteScalar() > 0;
        }
    }
}
