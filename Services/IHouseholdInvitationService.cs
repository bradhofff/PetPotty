using PetPotty.Models;

namespace PetPotty.Services;

public interface IHouseholdInvitationService
{
    HouseholdInvitationDetails? Validate(string token);
    Task<HouseholdInvitationResult> CreateAsync(
        int userID,
        int householdID,
        string email,
        HouseholdRole role,
        CancellationToken cancellationToken = default);
    Task<HouseholdInvitationResult> ResendAsync(
        int userID,
        int householdID,
        Guid invitationPublicID,
        CancellationToken cancellationToken = default);
    HouseholdInvitationResult Revoke(int userID, int householdID, Guid invitationPublicID);
    HouseholdInvitationResult Accept(int userID, string token);
}

