using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using System.Data;
using PetPotty.Services;

namespace PetPotty.Pages
{
    public class SignupModel : PageModel
    {
        private readonly IConfiguration _configuration;
        private readonly IHouseholdInvitationService _invitations;

        public SignupModel(IConfiguration configuration, IHouseholdInvitationService invitations)
        {
            _configuration = configuration;
            _invitations = invitations;
        }

        [BindProperty] public string SignupName     { get; set; } = string.Empty;
        [BindProperty] public string SignupUserName { get; set; } = string.Empty;
        [BindProperty] public string SignupEmail    { get; set; } = string.Empty;
        [BindProperty] public string SignupPhone    { get; set; } = string.Empty;
        [BindProperty] public string SignupPass     { get; set; } = string.Empty;
        [BindProperty] public string SignupConfirmPass { get; set; } = string.Empty;
        [BindProperty] public string InvitationToken { get; set; } = string.Empty;

        public string ErrorMessage   { get; set; } = string.Empty;
        public string SuccessMessage { get; set; } = string.Empty;

        public IActionResult OnGet(string? invitationToken)
        {
            InvitationToken = invitationToken?.Trim() ?? string.Empty;
            if (IsAuthenticated())
                return string.IsNullOrWhiteSpace(InvitationToken)
                    ? RedirectToPage("/Home")
                    : RedirectToPage("/HouseholdInvitation", new { token = InvitationToken });

            var invitation = string.IsNullOrWhiteSpace(InvitationToken)
                ? null
                : _invitations.Validate(InvitationToken);
            if (invitation != null && !invitation.AcceptedAtUtc.HasValue
                && !invitation.RevokedAtUtc.HasValue && invitation.ExpiresAtUtc > DateTime.UtcNow)
                SignupEmail = invitation.Email;
            return Page();
        }

        public IActionResult OnPost()
        {
            if (IsAuthenticated())
            {
                return RedirectToPage("/Home");
            }

            var invitation = string.IsNullOrWhiteSpace(InvitationToken)
                ? null
                : _invitations.Validate(InvitationToken);
            if (invitation != null && !invitation.AcceptedAtUtc.HasValue
                && !invitation.RevokedAtUtc.HasValue && invitation.ExpiresAtUtc > DateTime.UtcNow)
                SignupEmail = invitation.Email;

            // Manual validation
            if (string.IsNullOrWhiteSpace(SignupName) ||
                string.IsNullOrWhiteSpace(SignupUserName) ||
                string.IsNullOrWhiteSpace(SignupEmail) ||
                string.IsNullOrWhiteSpace(SignupPass))
            {
                ErrorMessage = "Please fill in all required fields.";
                return Page();
            }

            if (SignupUserName.Contains(" "))
            {
                ErrorMessage = "Username cannot contain spaces.";
                return Page();
            }

            if (SignupPass != SignupConfirmPass)
            {
                ErrorMessage = "Passwords do not match.";
                return Page();
            }

            string connStr = _configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string not found.");

            using var conn = new SqlConnection(connStr);
            using var cmd  = new SqlCommand("AddUser", conn)
            {
                CommandType = CommandType.StoredProcedure
            };

            cmd.Parameters.AddWithValue("@userName",  SignupUserName.Trim());
            cmd.Parameters.AddWithValue("@name",      SignupName.Trim());
            cmd.Parameters.AddWithValue("@email",     SignupEmail.Trim());
            cmd.Parameters.AddWithValue("@pass",      SignupPass);
            cmd.Parameters.AddWithValue("@phone", string.IsNullOrEmpty(SignupPhone) ? string.Empty : SignupPhone.Trim());
            cmd.Parameters.AddWithValue("@createdAt", DateTime.Now);
            cmd.Parameters.AddWithValue("@isAdmin",   false);

            try
            {
                conn.Open();
                int rows = cmd.ExecuteNonQuery();

                if (rows > 0)
                {
                    TempData["SignupSuccess"] = $"Account created successfully. Welcome, {SignupName.Trim()}! Please log in.";
                    return RedirectToPage("/Login", new { invitationToken = InvitationToken });
                }
                else
                {
                    ErrorMessage = "Failed to create account. Please try again.";
                    return Page();
                }
            }
            catch (SqlException ex)
            {
                if (ex.Message.Contains("UQ_Users_UserName"))
                    ErrorMessage = "That username is already taken.";
                else if (ex.Message.Contains("UQ_Users_Email"))
                    ErrorMessage = "That email is already registered.";
                else
                    ErrorMessage = "An error occurred: " + ex.Message;

                return Page();
            }
        }

        private bool IsAuthenticated()
        {
            return !string.IsNullOrEmpty(HttpContext.Session.GetString("userID"));
        }
    }
}
