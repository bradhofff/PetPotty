namespace PetPotty.Billing;

public sealed class StripeOptions
{
    public const string ApiVersion = "2025-06-30.basil";
    public string SecretKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool LiveMode { get; set; }
    public Dictionary<string, StripePlanOptions> Plans { get; set; } = [];

    public StripePlanOptions Price(BillingPlan plan) => Plans.GetValueOrDefault(plan switch
    {
        BillingPlan.Monthly => "Month", BillingPlan.Yearly => "Year", BillingPlan.Lifetime => "Lifetime",
        _ => throw new BillingException("Choose Monthly, Yearly, or Lifetime.")
    }) ?? new();

    public bool IsConfigured => SecretKey.StartsWith(LiveMode ? "sk_live_" : "sk_test_", StringComparison.Ordinal)
        && WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal)
        && Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || (!LiveMode && uri.Scheme == "http" && uri.IsLoopback))
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && new[] { BillingPlan.Monthly, BillingPlan.Yearly, BillingPlan.Lifetime }
            .Select(p => Price(p).PriceId).Distinct().Count() == 3
        && new[] { BillingPlan.Monthly, BillingPlan.Yearly, BillingPlan.Lifetime }
            .All(p => Price(p).PriceId.StartsWith("price_", StringComparison.Ordinal));

    public void RequireConfigured()
    {
        if (!IsConfigured) throw new BillingException("Billing is not configured yet. Please contact support.");
    }
}

public sealed class StripePlanOptions
{
    public string PriceId { get; set; } = "";
    public string PriceLabel { get; set; } = "";
}
