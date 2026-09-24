namespace PetPotty.Models;

public sealed class HouseholdPremiumStatus
{
    public int HouseholdID { get; init; }
    public string HouseholdName { get; init; } = string.Empty;
    public string Status { get; init; } = "free";
    public string? StripeCustomerID { get; init; }
    public string? StripeSubscriptionID { get; init; }
    public DateTime? CurrentPeriodEndUtc { get; init; }
    public bool CancelAtPeriodEnd { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }

    public bool IsPremium
    {
        get
        {
            if (Status is "active" or "trialing")
                return true;

            // Give a household access through the paid period while Stripe
            // recovers a past-due payment. The webhook still records the
            // degraded status for billing/support visibility.
            return Status == "past_due"
                && CurrentPeriodEndUtc.GetValueOrDefault() > DateTime.UtcNow;
        }
    }
}

public sealed record PremiumCheckoutSession(string Url);

public sealed record PremiumPlan(string Key, string Name, string PriceLabel, string Description, string PriceId, string Mode);
