using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages;

public sealed class HouseholdInvitationModel(IHouseholdInvitationService invitations) : PageModel
{
    [BindProperty]
    public string Token { get; set; } = string.Empty;

    public HouseholdInvitationDetails? Invitation { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public bool IsAuthenticated => int.TryParse(HttpContext.Session.GetString("userID"), out _);
    public bool CanAccept => Invitation != null
        && !Invitation.AcceptedAtUtc.HasValue
        && !Invitation.RevokedAtUtc.HasValue
        && Invitation.ExpiresAtUtc > DateTime.UtcNow;

    public IActionResult OnGet(string? token)
    {
        Token = token?.Trim() ?? string.Empty;
        LoadInvitation();
        return Page();
    }

    public IActionResult OnPostAccept()
    {
        if (!int.TryParse(HttpContext.Session.GetString("userID"), out var userID))
            return RedirectToPage("/Login", new { invitationToken = Token });

        var result = invitations.Accept(userID, Token);
        if (result.Succeeded)
        {
            TempData["ProfileStatus"] = result.Message;
            return RedirectToPage("/Profile");
        }

        Message = result.Message;
        LoadInvitation(preserveMessage: true);
        return Page();
    }

    private void LoadInvitation(bool preserveMessage = false)
    {
        Invitation = invitations.Validate(Token);
        if (preserveMessage && !string.IsNullOrWhiteSpace(Message))
            return;
        Message = Invitation switch
        {
            null => "This invitation link is invalid.",
            { RevokedAtUtc: not null } => "This invitation has been cancelled.",
            { AcceptedAtUtc: not null } => "This invitation has already been used.",
            { ExpiresAtUtc: var expires } when expires <= DateTime.UtcNow =>
                "This invitation has expired. Ask the household owner to send a new one.",
            _ => string.Empty
        };
    }
}

