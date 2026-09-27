using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PetPotty.Models;
using PetPotty.Services;
using System.Data;

namespace PetPotty.Pages;

public sealed class ProfileModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly IHouseholdService _households;
    private readonly IHouseholdContextService _householdContext;
    private readonly IHouseholdInvitationService _invitations;
    private readonly IEmailService _email;
    private readonly IPremiumService _premium;

    public ProfileModel(
        IConfiguration configuration,
        IHouseholdService households,
        IHouseholdContextService householdContext,
        IHouseholdInvitationService invitations,
        IEmailService email,
        IPremiumService premium)
    {
        _configuration = configuration;
        _households = households;
        _householdContext = householdContext;
        _invitations = invitations;
        _email = email;
        _premium = premium;
    }

    [BindProperty] public string ProfileName { get; set; } = string.Empty;
    [BindProperty] public string ProfileUserName { get; set; } = string.Empty;
    [BindProperty] public string ProfileEmail { get; set; } = string.Empty;
    [BindProperty] public string ProfilePhone { get; set; } = string.Empty;
    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string NewPassword { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;
    [BindProperty] public string HouseholdName { get; set; } = string.Empty;
    [BindProperty] public string InviteEmail { get; set; } = string.Empty;
    [BindProperty] public string InviteRole { get; set; } = nameof(HouseholdRole.Member);

    public string? ErrorMessage { get; set; }
    public HouseholdContext? ActiveHousehold { get; private set; }
    public IReadOnlyList<HouseholdContext> UserHouseholds { get; private set; } = [];
    public IReadOnlyList<HouseholdMember> Members { get; private set; } = [];
    public IReadOnlyList<HouseholdInvitation> PendingInvitations { get; private set; } = [];
    public HouseholdPremiumStatus? PremiumStatus { get; private set; }
    public IReadOnlyList<PremiumPreviewPrice> PremiumPrices { get; private set; } = [];
    public string? PremiumNotice { get; private set; }
    public bool IsOwner => ActiveHousehold?.Role == HouseholdRole.Owner;
    public bool EmailConfigured => _email.IsConfigured;

    public IActionResult OnGet(string? checkout)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        LoadPage(userID);
        PremiumNotice = checkout switch
        {
            "success" when PremiumStatus?.IsPremium == true => "Premium is active for this household.",
            "success" => "Checkout completed. Premium will appear here after Stripe confirms access.",
            "cancelled" => "Checkout was cancelled. Your household plan has not changed.",
            _ => null
        };
        return Page();
    }

    public IActionResult OnPostUpdateProfile()
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        if (string.IsNullOrWhiteSpace(ProfileName)
            || string.IsNullOrWhiteSpace(ProfileUserName)
            || string.IsNullOrWhiteSpace(ProfileEmail))
        {
            ErrorMessage = "Name, username, and email are required.";
            LoadPage(userID, loadProfile: false);
            return Page();
        }

        var connectionString = ConnectionString();
        if (!string.IsNullOrWhiteSpace(NewPassword))
        {
            if (NewPassword != ConfirmPassword)
            {
                ErrorMessage = "New passwords do not match.";
                LoadPage(userID, loadProfile: false);
                return Page();
            }
            if (!VerifyCurrentPassword(connectionString, userID, CurrentPassword))
            {
                ErrorMessage = "Current password is incorrect.";
                LoadPage(userID, loadProfile: false);
                return Page();
            }

            using var passwordConnection = new SqlConnection(connectionString);
            using var passwordCommand = new SqlCommand(
                "UPDATE dbo.Users SET pass = @Password WHERE userID = @UserID",
                passwordConnection);
            passwordCommand.Parameters.Add("@Password", SqlDbType.VarChar, 100).Value = NewPassword;
            passwordCommand.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            passwordConnection.Open();
            passwordCommand.ExecuteNonQuery();
        }

        using var connection = new SqlConnection(connectionString);
        using var command = new SqlCommand("UpdateUser", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@userID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@name", SqlDbType.VarChar, 100).Value = ProfileName.Trim();
        command.Parameters.Add("@userName", SqlDbType.VarChar, 100).Value = ProfileUserName.Trim();
        command.Parameters.Add("@email", SqlDbType.VarChar, 100).Value = ProfileEmail.Trim();
        command.Parameters.Add("@phone", SqlDbType.VarChar, 15).Value = ProfilePhone?.Trim() ?? string.Empty;
        try
        {
            connection.Open();
            command.ExecuteNonQuery();
            HttpContext.Session.SetString("name", ProfileName.Trim());
            TempData["ProfileStatus"] = "Profile updated successfully.";
            return Redirect("/Profile#account-details");
        }
        catch (SqlException exception)
        {
            ErrorMessage = exception.Message.Contains("UQ_Users_UserName", StringComparison.OrdinalIgnoreCase)
                ? "That username is already taken."
                : exception.Message.Contains("UQ_Users_Email", StringComparison.OrdinalIgnoreCase)
                    ? "That email is already registered."
                    : "Your profile could not be updated.";
            LoadPage(userID, loadProfile: false);
            return Page();
        }
    }

    public IActionResult OnPostSwitchHousehold(Guid householdPublicID)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        if (!_householdContext.TrySetActiveHousehold(userID, householdPublicID))
            TempData["HouseholdError"] = "You do not have access to that household.";
        else
            TempData["ProfileStatus"] = "Active household changed.";
        return Redirect("/Profile#household-settings");
    }

    public IActionResult OnPostRenameHousehold()
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        var household = _householdContext.GetActiveHousehold(userID);
        if (household == null || !_households.RenameHousehold(userID, household.HouseholdID, HouseholdName))
            TempData["HouseholdError"] = "The household name could not be changed.";
        else
            TempData["ProfileStatus"] = "Household name updated.";
        return Redirect("/Profile#household-settings");
    }

    public async Task<IActionResult> OnPostInviteAsync()
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        var household = _householdContext.GetActiveHousehold(userID);
        if (household == null || !HouseholdAccessRules.TryParseRole(InviteRole, out var role))
        {
            TempData["HouseholdError"] = "Choose a valid invitation role.";
            return Redirect("/Profile#household-settings");
        }

        var result = await _invitations.CreateAsync(userID, household.HouseholdID, InviteEmail, role, HttpContext.RequestAborted);
        TempData[result.Succeeded ? "ProfileStatus" : "HouseholdError"] = result.Message;
        return Redirect("/Profile#household-settings");
    }

    public IActionResult OnPostChangeMemberRole(Guid memberPublicID, string role)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        var household = _householdContext.GetActiveHousehold(userID);
        var changed = household != null
            && HouseholdAccessRules.TryParseRole(role, out var parsedRole)
            && _households.ChangeMemberRole(userID, household.HouseholdID, memberPublicID, parsedRole);
        TempData[changed ? "ProfileStatus" : "HouseholdError"] = changed
            ? "Member role updated."
            : "That member's role could not be changed.";
        return Redirect("/Profile#household-settings");
    }

    public IActionResult OnPostRemoveMember(Guid memberPublicID)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        var household = _householdContext.GetActiveHousehold(userID);
        var removed = household != null
            && _households.RemoveMember(userID, household.HouseholdID, memberPublicID);
        TempData[removed ? "ProfileStatus" : "HouseholdError"] = removed
            ? "Household member removed."
            : "That member cannot be removed. Owners cannot be removed without an ownership-transfer flow.";
        return Redirect("/Profile#household-settings");
    }

    public IActionResult OnPostCancelInvitation(Guid invitationPublicID)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        var household = _householdContext.GetActiveHousehold(userID);
        var result = household == null
            ? new HouseholdInvitationResult(false, "No active household was found.")
            : _invitations.Revoke(userID, household.HouseholdID, invitationPublicID);
        TempData[result.Succeeded ? "ProfileStatus" : "HouseholdError"] = result.Message;
        return Redirect("/Profile#household-settings");
    }

    public async Task<IActionResult> OnPostResendInvitationAsync(Guid invitationPublicID)
    {
        if (!TryGetUserID(out var userID))
            return RedirectToPage("/Login");
        var household = _householdContext.GetActiveHousehold(userID);
        var result = household == null
            ? new HouseholdInvitationResult(false, "No active household was found.")
            : await _invitations.ResendAsync(
                userID,
                household.HouseholdID,
                invitationPublicID,
                HttpContext.RequestAborted);
        TempData[result.Succeeded ? "ProfileStatus" : "HouseholdError"] = result.Message;
        return Redirect("/Profile#household-settings");
    }

    private void LoadPage(int userID, bool loadProfile = true)
    {
        if (loadProfile)
            LoadProfile(userID);
        ActiveHousehold = _households.EnsurePersonalHousehold(userID);
        UserHouseholds = _households.GetHouseholds(userID);
        Members = _households.GetMembers(userID, ActiveHousehold.HouseholdID);
        PendingInvitations = IsOwner
            ? _households.GetPendingInvitations(userID, ActiveHousehold.HouseholdID)
            : [];
        HouseholdName = ActiveHousehold.Name;
        PremiumStatus = _premium.GetStatus(userID, ActiveHousehold.HouseholdID);
        PremiumPrices = _configuration.GetSection("Stripe:Plans").GetChildren()
            .Select(section => new PremiumPreviewPrice(
                section.Key,
                section["Name"]?.Trim() is { Length: > 0 } name ? name : section.Key,
                section["PriceLabel"]?.Trim() ?? string.Empty,
                section.Key.ToLowerInvariant() switch
                {
                    "month" or "monthly" => "per month",
                    "year" or "yearly" => "per year",
                    "lifetime" => "one time",
                    _ => string.Empty
                }))
            .Where(plan => !string.IsNullOrWhiteSpace(plan.PriceLabel))
            .OrderBy(plan => plan.Key.ToLowerInvariant() switch
            {
                "month" or "monthly" => 0,
                "year" or "yearly" => 1,
                "lifetime" => 2,
                _ => 3
            })
            .ToArray();
    }

    private void LoadProfile(int userID)
    {
        using var connection = new SqlConnection(ConnectionString());
        using var command = new SqlCommand(
            "SELECT name, userName, email, phone FROM dbo.Users WHERE userID = @UserID",
            connection);
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        connection.Open();
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return;
        ProfileName = reader["name"].ToString() ?? string.Empty;
        ProfileUserName = reader["userName"].ToString() ?? string.Empty;
        ProfileEmail = reader["email"].ToString() ?? string.Empty;
        ProfilePhone = reader["phone"].ToString() ?? string.Empty;
    }

    private bool VerifyCurrentPassword(string connectionString, int userID, string password)
    {
        using var connection = new SqlConnection(connectionString);
        using var command = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.Users WHERE userID = @UserID AND pass = @Password",
            connection);
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@Password", SqlDbType.VarChar, 100).Value = password ?? string.Empty;
        connection.Open();
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private string ConnectionString() =>
        _configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string not found.");

    private bool TryGetUserID(out int userID) =>
        int.TryParse(HttpContext.Session.GetString("userID"), out userID);
}
