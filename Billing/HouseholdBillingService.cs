using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PetPotty.Models;
using PetPotty.Services;
using static PetPotty.Billing.StripeJson;

namespace PetPotty.Billing;

public sealed class HouseholdBillingService(IBillingStore store, IStripeGateway stripe,
    IHouseholdAuthorizationService authorization, IOptions<StripeOptions> configured,
    TimeProvider clock, ILogger<HouseholdBillingService> logger)
{
    private StripeOptions Options => configured.Value;

    public async Task<HouseholdBilling> GetAsync(int userId, int householdId, CancellationToken ct)
    {
        if (authorization.GetRole(userId, householdId) == null) throw new UnauthorizedAccessException();
        return await store.ReadAsync(householdId, ct);
    }

    public async Task<string> CheckoutAsync(int userId, HouseholdContext household, BillingPlan plan, CancellationToken ct)
    {
        RequireOwner(userId, household.HouseholdID);
        Options.RequireConfigured();
        if (plan is not (BillingPlan.Monthly or BillingPlan.Yearly or BillingPlan.Lifetime))
            throw new BillingException("Choose Monthly, Yearly, or Lifetime.");
        await using var lease = await store.LockAsync(household.HouseholdID, ct);
        RequireOwner(userId, household.HouseholdID);
        var state = lease.State;
        var now = clock.GetUtcNow();
        if (state.Resolve(now).Plan != BillingPlan.Free || state.Subscriptions.Any(s => s.Status is not ("canceled" or "incomplete_expired")))
            throw new BillingException("This household already has a plan or subscription. Use Manage billing to review it.");

        if (state.CustomerId == null)
        {
            state.CustomerCreationStartedAt ??= now;
            if (now - state.CustomerCreationStartedAt > TimeSpan.FromHours(23))
                throw new BillingException("Customer creation needs reconciliation. Please contact support.");
            await lease.SaveAsync(null, ct);
            var customer = await stripe.PostAsync("customers", new()
            {
                ["metadata[household_id]"] = household.HouseholdID.ToString(CultureInfo.InvariantCulture),
                ["metadata[household_public_id]"] = household.PublicID.ToString()
            }, $"household-customer-{household.PublicID}", ct);
            state.CustomerId = customer.Id();
            if (state.CustomerId.Length == 0) throw new InvalidOperationException("Stripe customer ID missing.");
            await lease.SaveAsync(null, ct);
        }

        var attempt = state.Checkouts.LastOrDefault(c => c.Status is "creating" or "open" or "processing");
        if (attempt?.SessionId != null)
        {
            var session = await stripe.GetAsync($"checkout/sessions/{PathId(attempt.SessionId)}", ct);
            if (session.At("status").Text() == "expired")
            {
                attempt.Status = "expired";
                await lease.SaveAsync(null, ct);
                attempt = null;
            }
            else if (session.At("status").Text() != "open")
                throw new BillingException("Your payment is being confirmed. Refresh this page shortly.");
        }
        if (attempt != null && attempt.Plan != plan)
            throw new BillingException("A checkout is already pending. Complete it or wait for it to expire before choosing another plan.");
        if (attempt == null)
        {
            var priceId = Options.Price(plan).PriceId;
            var price = await stripe.GetAsync($"prices/{PathId(priceId)}", ct);
            ValidatePrice(price, plan);
            attempt = new()
            {
                Plan = plan, PriceId = priceId, CreatedAt = now, ExpiresAt = now.AddHours(1),
                ReturnBaseUrl = Options.BaseUrl.TrimEnd('/')
            };
            state.Checkouts.Add(attempt);
            // Persist the operation before Stripe is called, so a timeout/retry reuses the same key and parameters.
            await lease.SaveAsync(null, ct);
        }
        if (attempt.SessionId != null) return SafeStripeUrl(attempt.Url, "checkout.stripe.com");
        if (now - attempt.CreatedAt > TimeSpan.FromMinutes(25))
            throw new BillingException("The interrupted checkout needs reconciliation. Please contact support.");

        var values = new Dictionary<string, string>
        {
            ["customer"] = state.CustomerId,
            ["client_reference_id"] = household.PublicID.ToString(),
            ["mode"] = plan == BillingPlan.Lifetime ? "payment" : "subscription",
            ["line_items[0][price]"] = attempt.PriceId, ["line_items[0][quantity]"] = "1",
            ["payment_method_types[0]"] = "card",
            ["success_url"] = attempt.ReturnBaseUrl + "/Billing?checkout=returned",
            ["cancel_url"] = attempt.ReturnBaseUrl + "/Billing?checkout=canceled",
            ["expires_at"] = attempt.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["metadata[household_id]"] = household.HouseholdID.ToString(CultureInfo.InvariantCulture),
            ["metadata[checkout_attempt_id]"] = attempt.Id.ToString()
        };
        var metadataPrefix = plan == BillingPlan.Lifetime ? "payment_intent_data" : "subscription_data";
        values[$"{metadataPrefix}[metadata][household_id]"] = household.HouseholdID.ToString(CultureInfo.InvariantCulture);
        values[$"{metadataPrefix}[metadata][checkout_attempt_id]"] = attempt.Id.ToString();
        var created = await stripe.PostAsync("checkout/sessions", values, $"household-checkout-{attempt.Id}", ct);
        attempt.SessionId = created.Id();
        attempt.Url = SafeStripeUrl(created.At("url").Text(), "checkout.stripe.com");
        attempt.Status = "open";
        await lease.SaveAsync(null, ct);
        return attempt.Url;
    }

    public async Task<string> PortalAsync(int userId, int householdId, CancellationToken ct)
    {
        RequireOwner(userId, householdId);
        Options.RequireConfigured();
        await using var lease = await store.LockAsync(householdId, ct);
        RequireOwner(userId, householdId);
        if (lease.State.CustomerId == null) throw new BillingException("There is no billing account to manage yet.");
        var portal = await stripe.PostAsync("billing_portal/sessions", new()
        {
            ["customer"] = lease.State.CustomerId, ["return_url"] = Options.BaseUrl.TrimEnd('/') + "/Billing"
        }, null, ct);
        return SafeStripeUrl(portal.At("url").Text(), "billing.stripe.com");
    }

    public async Task ProcessAsync(StripeEvent evt, CancellationToken ct)
    {
        if (!StripeWebhook.EventTypes.Contains(evt.Type)) return;
        if (evt.LiveMode != Options.LiveMode) throw new InvalidOperationException("Stripe mode mismatch.");
        var householdId = await store.FindHouseholdAsync(evt.CustomerId, ct);
        if (householdId == null)
        {
            logger.LogInformation("Ignoring Stripe event {EventId} for an unassociated customer", evt.Id);
            return;
        }
        await using var lease = await store.LockAsync(householdId.Value, ct);
        if (await lease.WasProcessedAsync(evt.Id, ct)) return;
        // Fetch only after obtaining the distributed lock. Never apply event snapshots or order by event timestamps.
        if (evt.Type.StartsWith("checkout.session.", StringComparison.Ordinal))
            await RefreshCheckoutAsync(lease.State, householdId.Value, evt.ObjectId, ct);
        else if (evt.Type.StartsWith("customer.subscription.", StringComparison.Ordinal))
            await RefreshSubscriptionAsync(lease.State, householdId.Value, evt.ObjectId, null, ct);
        else
        {
            var invoice = await stripe.GetAsync($"invoices/{PathId(evt.ObjectId)}", ct);
            RequireCustomer(invoice, lease.State);
            var subscriptionId = invoice.At("parent", "subscription_details", "subscription").Id();
            if (subscriptionId.Length > 0)
                await RefreshSubscriptionAsync(lease.State, householdId.Value, subscriptionId, invoice, ct);
        }
        await lease.SaveAsync(evt, ct);
    }

    private async Task RefreshCheckoutAsync(HouseholdBilling state, int householdId, string id, CancellationToken ct)
    {
        var session = await stripe.GetAsync($"checkout/sessions/{PathId(id)}", ct);
        RequireCustomer(session, state);
        var attempt = FindAttempt(state, householdId, session);
        if (attempt.SessionId != null && attempt.SessionId != id) throw new InvalidOperationException("Checkout ID mismatch.");
        attempt.SessionId = id;
        attempt.Status = session.At("status").Text() == "expired" ? "expired" : "processing";
        attempt.SubscriptionId = NullId(session.At("subscription"));
        attempt.PaymentIntentId = NullId(session.At("payment_intent"));
        if (attempt.Plan != BillingPlan.Lifetime)
        {
            if (session.At("mode").Text() != "subscription") throw new InvalidOperationException("Checkout mode mismatch.");
            if (attempt.SubscriptionId != null)
                await RefreshSubscriptionAsync(state, householdId, attempt.SubscriptionId, null, ct);
            return;
        }
        if (session.At("mode").Text() != "payment") throw new InvalidOperationException("Checkout mode mismatch.");
        if (attempt.PaymentIntentId == null) return;
        var intent = await stripe.GetAsync($"payment_intents/{PathId(attempt.PaymentIntentId)}", ct);
        RequireCustomer(intent, state);
        if (FindAttempt(state, householdId, intent).Id != attempt.Id) throw new InvalidOperationException("Payment metadata mismatch.");
        if (intent.At("status").Text() is "requires_payment_method" or "canceled") attempt.Status = "failed";
        if (session.At("payment_status").Text() != "paid" || session.At("status").Text() != "complete"
            || intent.At("status").Text() != "succeeded" || intent.At("amount_received").Number() <= 0) return;
        var lines = await stripe.GetAsync($"checkout/sessions/{PathId(id)}/line_items?limit=100", ct);
        var items = lines.At("data").Items().ToArray();
        if (lines.At("has_more").Flag() || items.Length != 1 || items[0].At("price").Id() != attempt.PriceId
            || items[0].At("quantity").Number() != 1) throw new InvalidOperationException("Lifetime price mismatch.");
        state.RecordPayment(new(id, BillingPlan.Lifetime, attempt.PriceId, null, attempt.PaymentIntentId,
            clock.GetUtcNow(), clock.GetUtcNow(), null, intent.At("amount_received").Number(), intent.At("currency").Text()));
        attempt.Status = "paid";
    }

    private async Task RefreshSubscriptionAsync(HouseholdBilling state, int householdId, string id, JsonElement? eventInvoice, CancellationToken ct)
    {
        var subscription = await stripe.GetAsync($"subscriptions/{PathId(id)}", ct);
        RequireCustomer(subscription, state);
        var attempt = FindAttempt(state, householdId, subscription);
        if (attempt.Plan == BillingPlan.Lifetime || (attempt.SubscriptionId != null && attempt.SubscriptionId != id))
            throw new InvalidOperationException("Subscription association mismatch.");
        attempt.SubscriptionId = id;
        var items = subscription.At("items", "data").Items().ToArray();
        if (items.Length != 1 || subscription.At("items", "has_more").Flag())
            throw new InvalidOperationException("Expected a single household subscription item.");
        var priceId = items[0].At("price").Id();
        _ = PlanForPrice(priceId, attempt);
        var sub = state.Subscriptions.FirstOrDefault(s => s.Id == id);
        if (sub == null) { sub = new() { Id = id }; state.Subscriptions.Add(sub); }
        sub.CheckoutAttemptId = attempt.Id;
        sub.PriceId = priceId;
        sub.Status = subscription.At("status").Text();
        sub.CreatedAt = subscription.At("created").Date() ?? throw new InvalidOperationException("Subscription creation date missing.");
        sub.CurrentPeriodEnd = items[0].At("current_period_end").Date();
        sub.CancelAtPeriodEnd = subscription.At("cancel_at_period_end").Flag();
        sub.CancelAt = subscription.At("cancel_at").Date();
        sub.CanceledAt = subscription.At("canceled_at").Date();
        sub.EndedAt = subscription.At("ended_at").Date();
        sub.LatestInvoiceId = NullId(subscription.At("latest_invoice"));
        attempt.Status = sub.Status is "incomplete_expired" or "canceled" ? "closed" : "subscribed";
        if (eventInvoice.HasValue) await RecordInvoiceAsync(state, attempt, id, eventInvoice.Value, ct);
        if (sub.LatestInvoiceId != null && sub.LatestInvoiceId != eventInvoice?.Id())
        {
            var latest = await stripe.GetAsync($"invoices/{PathId(sub.LatestInvoiceId)}", ct);
            await RecordInvoiceAsync(state, attempt, id, latest, ct);
        }
    }

    private async Task RecordInvoiceAsync(HouseholdBilling state, CheckoutAttempt attempt, string subscriptionId, JsonElement invoice, CancellationToken ct)
    {
        RequireCustomer(invoice, state);
        if (invoice.At("parent", "subscription_details", "subscription").Id() != subscriptionId)
            throw new InvalidOperationException("Invoice subscription mismatch.");
        // A trial, unpaid invoice, or manually marked paid invoice never grants access.
        if (invoice.At("status").Text() != "paid" || invoice.At("amount_paid").Number() <= 0
            || invoice.At("paid_out_of_band").Flag()) return;
        var invoiceId = invoice.Id();
        if (state.Payments.Any(p => p.Id == invoiceId)) return;
        // Basil invoice payments provide the actual payment reference. Invoice status alone can also
        // mean a credit or an out-of-band payment; require Stripe to confirm a successful PaymentIntent.
        var paymentIntentId = await SuccessfulInvoicePaymentAsync(state, invoiceId, ct);
        if (paymentIntentId == null) return;
        var lines = new List<JsonElement>();
        var path = $"invoices/{PathId(invoiceId)}/lines?limit=100";
        while (true)
        {
            var page = await stripe.GetAsync(path, ct);
            var batch = page.At("data").Items().ToArray();
            lines.AddRange(batch);
            if (!page.At("has_more").Flag()) break;
            if (batch.Length == 0) throw new InvalidOperationException("Empty invoice page.");
            path = $"invoices/{PathId(invoiceId)}/lines?limit=100&starting_after={PathId(batch[^1].Id())}";
        }
        var line = lines.Where(l => l.At("parent", "subscription_item_details", "subscription").Id() == subscriptionId
                && !l.At("parent", "subscription_item_details", "proration").Flag() && l.At("amount").Number() > 0)
            .OrderByDescending(l => l.At("period", "end").Number()).FirstOrDefault();
        if (line.ValueKind == JsonValueKind.Undefined) return;
        var priceId = line.At("pricing", "price_details", "price").Id();
        var plan = PlanForPrice(priceId, attempt);
        var start = line.At("period", "start").Date();
        var end = line.At("period", "end").Date();
        if (start == null || end == null || end <= start) throw new InvalidOperationException("Invalid paid invoice period.");
        state.RecordPayment(new(invoiceId, plan, priceId, subscriptionId, paymentIntentId,
            invoice.At("status_transitions", "paid_at").Date() ?? throw new InvalidOperationException("Missing payment date."),
            start.Value, end, invoice.At("amount_paid").Number(), invoice.At("currency").Text()));
    }

    private async Task<string?> SuccessfulInvoicePaymentAsync(HouseholdBilling state, string invoiceId, CancellationToken ct)
    {
        var path = $"invoice_payments?invoice={PathId(invoiceId)}&status=paid&limit=100";
        while (true)
        {
            var page = await stripe.GetAsync(path, ct);
            var batch = page.At("data").Items().ToArray();
            foreach (var payment in batch)
            {
                if (payment.At("invoice").Id() != invoiceId || payment.At("status").Text() != "paid"
                    || payment.At("amount_paid").Number() <= 0 || payment.At("payment", "type").Text() != "payment_intent") continue;
                var id = payment.At("payment", "payment_intent").Id();
                var intent = await stripe.GetAsync($"payment_intents/{PathId(id)}", ct);
                RequireCustomer(intent, state);
                if (intent.At("status").Text() == "succeeded" && intent.At("amount_received").Number() > 0) return id;
            }
            if (!page.At("has_more").Flag()) return null;
            if (batch.Length == 0) throw new InvalidOperationException("Empty invoice payment page.");
            path = $"invoice_payments?invoice={PathId(invoiceId)}&status=paid&limit=100&starting_after={PathId(batch[^1].Id())}";
        }
    }

    private BillingPlan PlanForPrice(string priceId, CheckoutAttempt attempt)
    {
        if (priceId == attempt.PriceId && attempt.Plan is BillingPlan.Monthly or BillingPlan.Yearly) return attempt.Plan;
        foreach (var plan in new[] { BillingPlan.Monthly, BillingPlan.Yearly })
            if (Options.Price(plan).PriceId == priceId) return plan;
        throw new InvalidOperationException("Unknown recurring Stripe price.");
    }

    private static CheckoutAttempt FindAttempt(HouseholdBilling state, int householdId, JsonElement obj)
    {
        if (obj.At("metadata", "household_id").Text() != householdId.ToString(CultureInfo.InvariantCulture)
            || !Guid.TryParse(obj.At("metadata", "checkout_attempt_id").Text(), out var attemptId))
            throw new InvalidOperationException("Stripe household metadata mismatch.");
        return state.Checkouts.SingleOrDefault(c => c.Id == attemptId)
            ?? throw new InvalidOperationException("Unknown checkout attempt.");
    }

    private void RequireCustomer(JsonElement obj, HouseholdBilling state)
    {
        if (obj.At("customer").Id() != state.CustomerId || obj.At("livemode").Flag() != Options.LiveMode)
            throw new InvalidOperationException("Stripe customer or mode mismatch.");
    }
    private void RequireOwner(int userId, int householdId)
    {
        if (authorization.GetRole(userId, householdId) != HouseholdRole.Owner) throw new UnauthorizedAccessException();
    }
    private void ValidatePrice(JsonElement price, BillingPlan plan)
    {
        var interval = price.At("recurring", "interval").Text();
        if (!price.At("active").Flag() || price.At("livemode").Flag() != Options.LiveMode || price.At("unit_amount").Number() <= 0
            || (plan == BillingPlan.Lifetime ? price.At("type").Text() != "one_time"
                : price.At("type").Text() != "recurring" || price.At("recurring", "interval_count").Number() != 1
                    || interval != (plan == BillingPlan.Monthly ? "month" : "year")))
            throw new BillingException("The selected Stripe price is not configured correctly. Please contact support.");
    }
    private static string? NullId(JsonElement obj) => obj.Id() is { Length: > 0 } id ? id : null;
    private static string SafeStripeUrl(string? url, string host) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == host ? url : throw new InvalidOperationException("Invalid Stripe redirect URL.");
}
