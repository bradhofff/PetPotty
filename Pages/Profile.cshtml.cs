using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PetPotty.Models;
using PetPotty.Services;
using System.Data;

namespace PetPotty.Pages
{
    public class ProfileModel : PageModel
    {
        private readonly IConfiguration _configuration;
        private readonly IHouseholdService _householdService;
        private readonly IHouseholdContextService _householdContext;
        private readonly IHouseholdInvitationService _invitationService;

        public ProfileModel(
            IConfiguration configuration,
            IHouseholdService householdService,
            IHouseholdContextService householdContext,
            IHouseholdInvitationService invitationService)
        {
            _configuration = configuration;
            _householdService = householdService;
            _householdContext = householdContext;
            _invitationService = invitationService;
        }

        // ── Read-only display state ──────────────────────────────────
        public int UserID { get; set; }
        [BindProperty(SupportsGet = true)] public string ActiveTab { get; set; } = "Profile";

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

        // ── Household tab state ────────────────────────────────────────
        public HouseholdContext Household { get; set; } = new();
        public List<HouseholdSummary> Households { get; set; } = [];
        public List<HouseholdMember> Members { get; set; } = [];
        public List<HouseholdInvitation> PendingInvitations { get; set; } = [];
        [BindProperty] public string HouseholdName { get; set; } = string.Empty;
        [BindProperty] public InvitationInput Invite { get; set; } = new();
        public string? HouseholdErrorMessage { get; set; }

        // ── GET ──────────────────────────────────────────────────────
        public IActionResult OnGet()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            LoadProfile(userID);
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            LoadHouseholdData();
            return Page();
        }

        // ── POST: Save profile fields ────────────────────────────────
        public IActionResult OnPostAsync()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
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
                    LoadHouseholdData();
                    return Page();
                }

                // Verify current password
                if (!VerifyCurrentPassword(connStr, userID, CurrentPassword))
                {
                    ErrorMessage = "Current password is incorrect.";
                    LoadProfile(userID);
                    LoadHouseholdData();
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
                LoadHouseholdData();
                return Page();
            }
        }

        // ── Household: switch active household ───────────────────────
        public IActionResult OnPostSwitchHousehold(int householdID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!_householdContext.SwitchHousehold(HttpContext.Session, userID, householdID))
                return Forbid();

            return RedirectToPage(new { ActiveTab = "Household" });
        }

        // ── Household: rename ─────────────────────────────────────────
        public IActionResult OnPostRenameHousehold()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            if (!Household.CanManageHousehold)
                return Forbid();

            if (string.IsNullOrWhiteSpace(HouseholdName) || HouseholdName.Trim().Length > 150)
            {
                HouseholdErrorMessage = "Enter a household name up to 150 characters.";
                LoadHouseholdData();
                return Page();
            }

            TempData["HouseholdStatus"] = _householdService.RenameHousehold(userID, Household.HouseholdID, HouseholdName.Trim())
                ? "Household renamed."
                : "The household could not be renamed.";
            return RedirectToPage(new { ActiveTab = "Household" });
        }

        // ── Household: change a member's role ─────────────────────────
        public IActionResult OnPostChangeRole(int targetUserID, string newRole)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            if (!Household.CanManageHousehold)
                return Forbid();

            TempData["HouseholdStatus"] = _householdService.UpdateMemberRole(userID, Household.HouseholdID, targetUserID, newRole)
                ? "Member role updated."
                : "That member's role could not be updated.";
            return RedirectToPage(new { ActiveTab = "Household" });
        }

        // ── Household: remove a member ────────────────────────────────
        public IActionResult OnPostRemoveMember(int targetUserID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            if (!Household.CanManageHousehold)
                return Forbid();

            TempData["HouseholdStatus"] = _householdService.RemoveMember(userID, Household.HouseholdID, targetUserID)
                ? "Member removed from the household."
                : "That member could not be removed.";
            return RedirectToPage(new { ActiveTab = "Household" });
        }

        // ── Household: send an invitation ─────────────────────────────
        public async Task<IActionResult> OnPostInviteAsync()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            LoadProfile(userID);
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            if (!Household.CanManageHousehold)
                return Forbid();

            ModelState.Clear();
            if (!TryValidateModel(Invite, nameof(Invite)) || !HouseholdRoles.Assignable.Contains(Invite.Role))
            {
                HouseholdErrorMessage = "Enter a valid email address and choose Member or Caregiver.";
                LoadHouseholdData();
                return Page();
            }

            var baseUrl = _configuration["App:BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = $"{Request.Scheme}://{Request.Host}";

            var inviterName = HttpContext.Session.GetString("name") ?? "A household owner";
            var (result, emailSent) = await _invitationService.CreateAsync(
                userID, Household.HouseholdID, Household.Name, inviterName, Invite.Email, Invite.Role, baseUrl,
                HttpContext.RequestAborted);

            switch (result)
            {
                case CreateInvitationResult.Created:
                    TempData["HouseholdStatus"] = emailSent
                        ? $"Invitation sent to {Invite.Email}."
                        : $"Invitation created for {Invite.Email}, but the email could not be sent. Share the accept link with them directly, or check the SMTP delivery settings.";
                    break;
                case CreateInvitationResult.AlreadyPending:
                    HouseholdErrorMessage = $"{Invite.Email} already has a pending invitation — use Resend instead.";
                    break;
                case CreateInvitationResult.AlreadyMember:
                    HouseholdErrorMessage = $"{Invite.Email} is already a member of this household.";
                    break;
                case CreateInvitationResult.InvalidRole:
                    HouseholdErrorMessage = "Choose Member or Caregiver — Owner cannot be assigned by invitation.";
                    break;
                default:
                    HouseholdErrorMessage = "Only the household Owner can send invitations.";
                    break;
            }

            if (HouseholdErrorMessage != null)
            {
                LoadHouseholdData();
                return Page();
            }

            return RedirectToPage(new { ActiveTab = "Household" });
        }

        // ── Household: resend a pending invitation ───────────────────
        public async Task<IActionResult> OnPostResendInvitationAsync(int invitationID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            if (!Household.CanManageHousehold)
                return Forbid();

            var inviterName = HttpContext.Session.GetString("name") ?? "A household owner";
            var baseUrl = _configuration["App:BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = $"{Request.Scheme}://{Request.Host}";

            var (succeeded, emailSent, resultCode) = await _invitationService.ResendAsync(
                userID, Household.HouseholdID, invitationID, Household.Name, inviterName, baseUrl, HttpContext.RequestAborted);

            TempData["HouseholdStatus"] = succeeded
                ? (emailSent ? "Invitation resent." : "Invitation renewed, but the email could not be sent. Share the accept link with them directly, or check the SMTP delivery settings.")
                : resultCode switch
                {
                    "TooSoon" => "Please wait a little before resending this invitation again.",
                    "NotPending" => "That invitation is no longer pending.",
                    _ => "That invitation could not be resent."
                };

            return RedirectToPage(new { ActiveTab = "Household" });
        }

        // ── Household: revoke a pending invitation ───────────────────
        public IActionResult OnPostRevokeInvitation(int invitationID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            if (!TryResolveHousehold())
                return RedirectToPage("/Login");
            if (!Household.CanManageHousehold)
                return Forbid();

            TempData["HouseholdStatus"] = _invitationService.Revoke(userID, Household.HouseholdID, invitationID)
                ? "Invitation revoked."
                : "That invitation could not be revoked.";
            return RedirectToPage(new { ActiveTab = "Household" });
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

        private bool TryResolveHousehold()
        {
            var context = _householdContext.GetActiveHousehold(HttpContext.Session, UserID);
            if (context == null)
                return false;

            Household = context;
            return true;
        }

        private void LoadHouseholdData()
        {
            Households = _householdContext.GetHouseholds(UserID);
            Members = _householdService.GetMembers(UserID, Household.HouseholdID);
            PendingInvitations = _invitationService.GetPending(UserID, Household.HouseholdID);
            if (string.IsNullOrEmpty(HouseholdName))
                HouseholdName = Household.Name;
        }
    }
}
