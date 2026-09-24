using PetPotty.Models;

namespace PetPotty.Services;

public interface IPremiumService
{
    HouseholdPremiumStatus? GetStatus(int userID, int householdID);
    Task<PremiumCheckoutSession> CreateCheckoutSessionAsync(
        int userID,
        HouseholdContext household,
        string baseUrl,
        CancellationToken cancellationToken = default);
    Task ProcessWebhookAsync(string payload, string? signature, CancellationToken cancellationToken = default);
}
