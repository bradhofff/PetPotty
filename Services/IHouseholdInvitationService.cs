using PetPotty.Models;

namespace PetPotty.Services;

public interface IHouseholdInvitationService
{
    List<HouseholdInvitation> GetPending(int userID, int householdID);

    // Creates (or renews an expired) invitation and attempts to email it.
    // The invitation row is saved regardless of whether the email send
    // succeeds — EmailSent reports what happened so the caller can tell the
    // Owner plainly rather than leaving them guessing.
    Task<(CreateInvitationResult Result, bool EmailSent)> CreateAsync(
        int userID, int householdID, string householdName, string inviterName,
        string email, string role, string baseUrl, CancellationToken cancellationToken = default);

    Task<(bool Succeeded, bool EmailSent, string ResultCode)> ResendAsync(
        int userID, int householdID, int invitationID, string householdName, string inviterName,
        string baseUrl, CancellationToken cancellationToken = default);

    bool Revoke(int userID, int householdID, int invitationID);

    // Anonymous-safe: looks up an invitation by the raw token from the link
    // without requiring the caller to already be logged in.
    InvitationDetails? GetByRawToken(string rawToken);

    AcceptInvitationResult Accept(string rawToken, int acceptingUserID, string acceptingEmailNormalized, out int? householdID);
}
