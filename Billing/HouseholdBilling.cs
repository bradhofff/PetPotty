namespace PetPotty.Billing;

public enum BillingPlan { Free, Monthly, Yearly, Lifetime }

// This aggregate is persisted as versioned JSON, alongside indexed customer and plan columns.
// Keep historical attempts, subscriptions and paid invoices for reconciliation.
public sealed class HouseholdBilling
{
    public int SchemaVersion { get; set; } = 1;
    public string? CustomerId { get; set; }
    public DateTimeOffset? CustomerCreationStartedAt { get; set; }
    public List<CheckoutAttempt> Checkouts { get; set; } = [];
    public List<BillingSubscription> Subscriptions { get; set; } = [];
    public List<PaidPeriod> Payments { get; set; } = [];

    public BillingView Resolve(DateTimeOffset now)
    {
        var lifetime = Payments.FirstOrDefault(p => p.Plan == BillingPlan.Lifetime);
        if (lifetime != null) return new(BillingPlan.Lifetime, "Active", null, null);
        var paid = Payments.Where(p => p.StartsAt <= now && p.EndsAt > now)
            .OrderByDescending(p => p.StartsAt).ThenByDescending(p => p.PaidAt).FirstOrDefault();
        var sub = Subscriptions.FirstOrDefault(s => s.Id == paid?.SubscriptionId)
            ?? Subscriptions.OrderByDescending(s => s.CreatedAt).FirstOrDefault();
        var pending = Checkouts.LastOrDefault(c => c.Status is "creating" or "open" or "processing");
        var lastEnd = Payments.Where(p => p.EndsAt <= now).OrderByDescending(p => p.EndsAt).FirstOrDefault()?.EndsAt;
        if (paid == null)
            return new(BillingPlan.Free, sub?.Status is "past_due" or "unpaid" ? "Payment failed"
                : pending != null ? "Payment pending" : Payments.Count > 0 ? "Expired" : "Free", lastEnd, null);
        var status = sub?.Status is "past_due" or "unpaid" ? "Payment failed"
            : sub?.Status == "canceled" ? "Canceled"
            : sub?.CancelAtPeriodEnd == true ? "Cancels at period end"
            : sub?.CancelAt != null ? "Cancellation scheduled"
            : sub?.Status == "paused" ? "Paused" : "Active";
        var renewal = sub is { Status: "active", CancelAtPeriodEnd: false } && sub.CancelAt == null
            ? sub.CurrentPeriodEnd : null;
        return new(paid.Plan, status, paid.EndsAt, renewal);
    }

    public void RecordPayment(PaidPeriod payment)
    {
        if (Payments.All(p => p.Id != payment.Id)) Payments.Add(payment);
    }
}

public sealed record BillingView(BillingPlan Plan, string Status, DateTimeOffset? AccessEndsAt, DateTimeOffset? RenewsAt);

public sealed class CheckoutAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public BillingPlan Plan { get; set; }
    public string PriceId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string ReturnBaseUrl { get; set; } = "";
    public string? SessionId { get; set; }
    public string? Url { get; set; }
    public string? SubscriptionId { get; set; }
    public string? PaymentIntentId { get; set; }
    public string Status { get; set; } = "creating";
}

public sealed class BillingSubscription
{
    public string Id { get; set; } = "";
    public Guid CheckoutAttemptId { get; set; }
    public string PriceId { get; set; } = "";
    public string Status { get; set; } = "incomplete";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTimeOffset? CancelAt { get; set; }
    public DateTimeOffset? CanceledAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? LatestInvoiceId { get; set; }
}

public sealed record PaidPeriod(string Id, BillingPlan Plan, string PriceId, string? SubscriptionId,
    string? PaymentIntentId, DateTimeOffset PaidAt, DateTimeOffset StartsAt, DateTimeOffset? EndsAt,
    long AmountPaid, string Currency);

public sealed record StripeEvent(string Id, string Type, string ObjectId, string CustomerId,
    DateTimeOffset CreatedAt, bool LiveMode);

public sealed class BillingException(string message) : Exception(message);
