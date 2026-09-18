using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages
{
    public class AcceptInvitationModel : PageModel
    {
        private readonly IHouseholdInvitationService _invitationService;
        private readonly IHouseholdContextService _householdContext;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AcceptInvitationModel> _logger;

        public AcceptInvitationModel(
            IHouseholdInvitationService invitationService,
            IHouseholdContextService householdContext,
            IConfiguration configuration,
            ILogger<AcceptInvitationModel> logger)
        {
            _invitationService = invitationService;
            _householdContext = householdContext;
            _configuration = configuration;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)] public string? Token { get; set; }

        public InvitationDetails? Invitation { get; set; }
        public bool IsLoggedIn { get; set; }
        public bool LoggedInEmailMatches { get; set; }
        public string? LoggedInEmail { get; set; }
        public string? ErrorMessage { get; set; }
        public bool Accepted { get; set; }

        public IActionResult OnGet()
        {
            if (string.IsNullOrWhiteSpace(Token))
            {
                ErrorMessage = "This invitation link is missing its token.";
                return Page();
            }

            Invitation = _invitationService.GetByRawToken(Token);
            if (Invitation == null)
            {
                ErrorMessage = "This invitation link is invalid.";
                return Page();
            }

            LoadLoginState();
            return Page();
        }

        public IActionResult OnPostAcceptAsync()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out var userID))
                return RedirectToPage("/Login", new { invite = Token });

            if (string.IsNullOrWhiteSpace(Token))
            {
                ErrorMessage = "This invitation link is missing its token.";
                return Page();
            }

            var email = GetSessionUserEmail(userID);
            var normalizedEmail = HouseholdInvitationService.NormalizeEmail(email ?? string.Empty);

            var result = _invitationService.Accept(Token, userID, normalizedEmail, out var householdID);
            Invitation = _invitationService.GetByRawToken(Token);
            LoadLoginState();

            switch (result)
            {
                case AcceptInvitationResult.Accepted:
                case AcceptInvitationResult.AlreadyAccepted:
                    if (householdID.HasValue)
                        _householdContext.SwitchHousehold(HttpContext.Session, userID, householdID.Value);
                    Accepted = true;
                    TempData["StatusMessage"] = "You're in! Welcome to the household.";
                    return RedirectToPage("/Home");
                case AcceptInvitationResult.Expired:
                    ErrorMessage = "This invitation has expired. Ask the household owner to send a new one.";
                    break;
                case AcceptInvitationResult.Revoked:
                    ErrorMessage = "This invitation was revoked by the household owner.";
                    break;
                case AcceptInvitationResult.EmailMismatch:
                    ErrorMessage = $"This invitation was sent to a different email address than the one on your account ({email}). Log in with the invited account instead.";
                    break;
                default:
                    ErrorMessage = "This invitation could not be found.";
                    break;
            }

            return Page();
        }

        private void LoadLoginState()
        {
            IsLoggedIn = int.TryParse(HttpContext.Session.GetString("userID"), out var userID);
            if (!IsLoggedIn || Invitation == null)
                return;

            LoggedInEmail = GetSessionUserEmail(userID);
            LoggedInEmailMatches = string.Equals(
                HouseholdInvitationService.NormalizeEmail(LoggedInEmail ?? string.Empty),
                Invitation.NormalizedEmail,
                StringComparison.Ordinal);
        }

        private string? GetSessionUserEmail(int userID)
        {
            var connStr = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connStr))
                return null;

            using var conn = new SqlConnection(connStr);
            using var cmd = new SqlCommand("SELECT email FROM dbo.Users WHERE userID = @userID", conn);
            cmd.Parameters.AddWithValue("@userID", userID);
            conn.Open();
            var result = cmd.ExecuteScalar();
            return result?.ToString();
        }
    }
}
