using PetPotty.Models;

namespace PetPotty.Services;

public interface IPremiumService
{
    bool BillingPortalConfigured { get; }
    HouseholdPremiumStatus? GetStatus(int userID, int householdID);
    Task<HouseholdPremiumStatus?> RefreshStatusAsync(int userID, int householdID,
        CancellationToken cancellationToken = default);
    Task<string> CreateBillingPortalSessionAsync(int userID, int householdID, string baseUrl,
        CancellationToken cancellationToken = default);
    Task<PremiumCheckoutSession> CreateCheckoutSessionAsync(
        int userID,
        HouseholdContext household,
        string priceID,
        string planType,
        string mode,
        string baseUrl,
        CancellationToken cancellationToken = default);
    Task ProcessWebhookAsync(string payload, string? signature, CancellationToken cancellationToken = default);
}
