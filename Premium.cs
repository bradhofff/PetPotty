namespace PetPotty.Models;

public sealed class HouseholdPremiumStatus
{
    public int HouseholdID { get; init; }
    public string HouseholdName { get; init; } = string.Empty;
    public string Status { get; init; } = "free";
    public string PlanType { get; init; } = "free";
    public string? StripeCustomerID { get; init; }
    public string? StripeSubscriptionID { get; init; }
    public DateTime? CurrentPeriodEndUtc { get; init; }
    public bool CancelAtPeriodEnd { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public int? BillingManagerUserID { get; init; }

    public bool HasBillingSubscription => PlanType != "lifetime"
        && !string.IsNullOrWhiteSpace(StripeCustomerID)
        && !string.IsNullOrWhiteSpace(StripeSubscriptionID)
        && Status is "active" or "trialing" or "past_due" or "unpaid" or "incomplete" or "paused";

    public bool CanManageBilling(int userID) => HasBillingSubscription && BillingManagerUserID == userID;

    public string PlanLabel => PlanType.ToLowerInvariant() switch
    {
        "monthly" => "Monthly",
        "yearly" => "Yearly",
        "lifetime" => "Lifetime",
        _ => "Free"
    };

    public bool IsPremium
    {
        get
        {
            if (CancelAtPeriodEnd && CurrentPeriodEndUtc is { } end && end <= DateTime.UtcNow)
                return false;
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
