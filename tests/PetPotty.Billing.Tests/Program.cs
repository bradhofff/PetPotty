using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PetPotty.Billing;
using PetPotty.Models;
using PetPotty.Services;

// Dependency-free executable regression suite. Any failed assertion returns a nonzero exit code.
var tests = new (string Name, Func<Task> Run)[]
{
    ("Free defaults and exact expiration boundary", () => Run(() =>
    {
        var f = new Fixture();
        f.Store.State.Checkouts.Clear();
        Equal(BillingPlan.Free, f.Store.State.Resolve(f.Now).Plan);
        f.Store.State.RecordPayment(f.Payment("in_old", f.Now.AddMonths(-1), f.Now));
        Equal(BillingPlan.Free, f.Store.State.Resolve(f.Now).Plan);
        Equal("Expired", f.Store.State.Resolve(f.Now).Status);
        Equal(BillingPlan.Monthly, f.Store.State.Resolve(f.Now.AddSeconds(-1)).Plan);
    })),
    ("Signature verification: raw bytes, rotation, tamper, replay, future and missing secret", () => Run(() =>
    {
        var f = new Fixture(); var bytes = Encoding.UTF8.GetBytes("{\"id\":\"evt_1\"}");
        var header = Sign(bytes, f.Now, "whsec_test");
        True(StripeSignature.Verify(bytes, header, "whsec_test", f.Now));
        True(StripeSignature.Verify(bytes, "v1=bad," + header, "whsec_test", f.Now));
        False(StripeSignature.Verify(Encoding.UTF8.GetBytes("{}"), header, "whsec_test", f.Now));
        False(StripeSignature.Verify(bytes, header, "whsec_test", f.Now.AddMinutes(6)));
        False(StripeSignature.Verify(bytes, header, "whsec_test", f.Now.AddMinutes(-6)));
        False(StripeSignature.Verify(bytes, header, "", f.Now));
        False(StripeSignature.Verify(bytes, "t=0,v1=garbage", "whsec_test", f.Now));
        False(StripeSignature.Verify(bytes, "t=" + f.Now.ToUnixTimeSeconds() + "," + header, "whsec_test", f.Now));
    })),
    ("Owner authorization enforced in service for checkout and portal", async () =>
    {
        var f = new Fixture();
        foreach (var user in new[] { 2, 3, 4 })
        {
            await Throws<UnauthorizedAccessException>(() => f.Service.CheckoutAsync(user, f.Household, BillingPlan.Monthly, default));
            await Throws<UnauthorizedAccessException>(() => f.Service.PortalAsync(user, 7, default));
        }
        await Throws<UnauthorizedAccessException>(() => f.Service.GetAsync(4, 7, default));
        await Throws<UnauthorizedAccessException>(() => f.Service.CheckoutAsync(1, new() { HouseholdID = 8 }, BillingPlan.Monthly, default));
        Equal(0, f.Api.Calls);
    }),
    ("Checkout reuses persisted session and never grants access", async () =>
    {
        var f = new Fixture(); f.Store.State.Checkouts.Clear();
        f.Api.Post = (path, values, key) =>
        {
            Equal("checkout/sessions", path);
            Equal("cus_7", values["customer"]);
            Equal("7", values["subscription_data[metadata][household_id]"]);
            Equal("price_month", values["line_items[0][price]"]);
            True(key!.StartsWith("household-checkout-"));
            return Json(new { id = "cs_new", url = "https://checkout.stripe.com/c/test", status = "open" });
        };
        f.Api.Responses["prices/price_month"] = Fixture.Price("month");
        f.Api.Responses["checkout/sessions/cs_new"] = Json(new { status = "open" });
        var url = await f.Service.CheckoutAsync(1, f.Household, BillingPlan.Monthly, default);
        Equal(url, await f.Service.CheckoutAsync(1, f.Household, BillingPlan.Monthly, default));
        Equal(1, f.Api.PostCalls);
        Equal(BillingPlan.Free, f.Store.State.Resolve(f.Now).Plan);
        await Throws<BillingException>(() => f.Service.CheckoutAsync(1, f.Household, BillingPlan.Lifetime, default));
    }),
    ("Wrong yearly interval is rejected before checkout", async () =>
    {
        var f = new Fixture(); f.Store.State.Checkouts.Clear();
        f.Api.Responses["prices/price_year"] = Fixture.Price("month");
        await Throws<BillingException>(() => f.Service.CheckoutAsync(1, f.Household, BillingPlan.Yearly, default));
        Equal(0, f.Api.PostCalls);
    }),
    ("Paid invoice arriving before checkout activates correct household", async () =>
    {
        var f = new Fixture(); f.PaidInvoice("in_1", f.Now, f.Now.AddMonths(1));
        await f.Service.ProcessAsync(f.Event("evt_1", "invoice.paid", "in_1"), default);
        Equal(BillingPlan.Monthly, f.Store.State.Resolve(f.Now).Plan);
        Equal(f.Now.AddMonths(1), f.Store.State.Resolve(f.Now).AccessEndsAt);
        Equal("sub_1", f.Store.State.Checkouts[0].SubscriptionId);
        Equal("in_1", f.Store.State.Payments[0].Id);
        True(f.Store.Receipts.Contains("evt_1"));
    }),
    ("Sequential and concurrent retries grant once", async () =>
    {
        var f = new Fixture(); f.PaidInvoice("in_1", f.Now, f.Now.AddMonths(1));
        var evt = f.Event("evt_1", "invoice.paid", "in_1");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Service.ProcessAsync(evt, default)));
        var calls = f.Api.Calls;
        await f.Service.ProcessAsync(evt, default);
        Equal(calls, f.Api.Calls);
        Equal(1, f.Store.State.Payments.Count);
        await f.Service.ProcessAsync(f.Event("evt_other", "invoice.payment_succeeded", "in_1"), default);
        Equal(1, f.Store.State.Payments.Count);
    }),
    ("Renewals and delayed old invoices cannot regress paid period", async () =>
    {
        var f = new Fixture();
        f.PaidInvoice("in_new", f.Now, f.Now.AddMonths(1));
        await f.Service.ProcessAsync(f.Event("evt_new", "invoice.paid", "in_new"), default);
        f.PaidInvoice("in_old", f.Now.AddMonths(-1), f.Now, updateSubscription: false);
        await f.Service.ProcessAsync(f.Event("evt_old", "invoice.paid", "in_old") with { CreatedAt = f.Now.AddMonths(-1) }, default);
        Equal(f.Now.AddMonths(1), f.Store.State.Resolve(f.Now).AccessEndsAt);
        Equal(2, f.Store.State.Payments.Count);
    }),
    ("Failed renewal preserves paid time then expires; recovery restores access", async () =>
    {
        var f = new Fixture();
        f.Store.State.RecordPayment(f.Payment("in_old", f.Now.AddMonths(-1), f.Now.AddHours(1)));
        f.PaidInvoice("in_due", f.Now, f.Now.AddMonths(1), paid: false);
        f.Subscription("past_due", "in_due", f.Now.AddMonths(1));
        await f.Service.ProcessAsync(f.Event("evt_failed", "invoice.payment_failed", "in_due"), default);
        Equal(BillingPlan.Monthly, f.Store.State.Resolve(f.Now).Plan);
        Equal("Payment failed", f.Store.State.Resolve(f.Now).Status);
        Equal(BillingPlan.Free, f.Store.State.Resolve(f.Now.AddHours(1)).Plan);
        f.PaidInvoice("in_due", f.Now, f.Now.AddMonths(1));
        await f.Service.ProcessAsync(f.Event("evt_recovered", "invoice.paid", "in_due"), default);
        // A delayed failure notification refreshes the current active subscription.
        await f.Service.ProcessAsync(f.Event("evt_late_failure", "invoice.payment_failed", "in_due"), default);
        Equal("Active", f.Store.State.Resolve(f.Now).Status);
        Equal(f.Now.AddMonths(1), f.Store.State.Resolve(f.Now).AccessEndsAt);
    }),
    ("Cancellation retains paid time, stops renewal and cannot be undone by stale snapshots", async () =>
    {
        var f = new Fixture(); f.PaidInvoice("in_1", f.Now, f.Now.AddMonths(1));
        f.Subscription("active", "in_1", f.Now.AddMonths(1), cancel: true);
        await f.Service.ProcessAsync(f.Event("evt_cancel", "customer.subscription.updated", "sub_1"), default);
        Equal("Cancels at period end", f.Store.State.Resolve(f.Now).Status);
        Equal<DateTimeOffset?>(null, f.Store.State.Resolve(f.Now).RenewsAt);
        f.Subscription("canceled", "in_1", f.Now.AddMonths(1));
        await f.Service.ProcessAsync(f.Event("evt_deleted", "customer.subscription.deleted", "sub_1"), default);
        await f.Service.ProcessAsync(f.Event("evt_old_update", "customer.subscription.updated", "sub_1"), default);
        Equal("Canceled", f.Store.State.Resolve(f.Now).Status);
        Equal(BillingPlan.Free, f.Store.State.Resolve(f.Now.AddMonths(1)).Plan);
    }),
    ("Unpaid, zero-value and out-of-band invoices cannot activate", async () =>
    {
        foreach (var variant in new[] { "unpaid", "zero", "outofband" })
        {
            var f = new Fixture(); f.PaidInvoice("in_1", f.Now, f.Now.AddMonths(1), paid: variant != "unpaid",
                amount: variant == "zero" ? 0 : 699, outOfBand: variant == "outofband");
            await f.Service.ProcessAsync(f.Event("evt_1", "invoice.paid", "in_1"), default);
            Equal(BillingPlan.Free, f.Store.State.Resolve(f.Now).Plan);
        }
    }),
    ("Yearly invoice uses its paid line period", async () =>
    {
        var f = new Fixture(); f.Store.State.Checkouts[0].Plan = BillingPlan.Yearly;
        f.Store.State.Checkouts[0].PriceId = "price_year";
        f.PaidInvoice("in_year", f.Now, f.Now.AddYears(1), price: "price_year");
        await f.Service.ProcessAsync(f.Event("evt_year", "invoice.paid", "in_year"), default);
        Equal(BillingPlan.Yearly, f.Store.State.Resolve(f.Now).Plan);
        Equal(f.Now.AddYears(1), f.Store.State.Resolve(f.Now).AccessEndsAt);
    }),
    ("Lifetime requires successful payment and survives later recurring events", async () =>
    {
        var f = new Fixture(); f.Lifetime(paid: false);
        await f.Service.ProcessAsync(f.Event("evt_pending", "checkout.session.completed", "cs_life"), default);
        Equal(BillingPlan.Free, f.Store.State.Resolve(f.Now).Plan);
        f.Lifetime(paid: true);
        await f.Service.ProcessAsync(f.Event("evt_paid", "checkout.session.async_payment_succeeded", "cs_life"), default);
        await f.Service.ProcessAsync(f.Event("evt_retry", "checkout.session.completed", "cs_life"), default);
        Equal(1, f.Store.State.Payments.Count);
        Equal(BillingPlan.Lifetime, f.Store.State.Resolve(f.Now.AddYears(100)).Plan);
        f.Store.State.RecordPayment(f.Payment("in_old", f.Now.AddMonths(-1), f.Now.AddMonths(1)));
        Equal(BillingPlan.Lifetime, f.Store.State.Resolve(f.Now).Plan);
        Equal<DateTimeOffset?>(null, f.Store.State.Resolve(f.Now).AccessEndsAt);
        await Throws<BillingException>(() => f.Service.CheckoutAsync(1, f.Household, BillingPlan.Monthly, default));
    }),
    ("Cross-customer and missing attempt metadata rejected without receipt", async () =>
    {
        var f = new Fixture(); f.PaidInvoice("in_bad", f.Now, f.Now.AddMonths(1), customer: "cus_other");
        await Throws<InvalidOperationException>(() => f.Service.ProcessAsync(f.Event("evt_bad", "invoice.paid", "in_bad"), default));
        False(f.Store.Receipts.Contains("evt_bad"));
        f.Api.Responses["subscriptions/sub_1"] = Json(new { id = "sub_1", customer = "cus_7", livemode = false, metadata = new { household_id = "8" } });
        await Throws<InvalidOperationException>(() => f.Service.ProcessAsync(f.Event("evt_meta", "customer.subscription.updated", "sub_1"), default));
        Equal(0, f.Store.State.Payments.Count);
    }),
    ("Unknown customer and other mode do not alter household", async () =>
    {
        var f = new Fixture();
        await f.Service.ProcessAsync(f.Event("evt_unknown", "invoice.paid", "in_x") with { CustomerId = "cus_other" }, default);
        Equal(0, f.Api.Calls);
        await Throws<InvalidOperationException>(() => f.Service.ProcessAsync(f.Event("evt_live", "invoice.paid", "in_x") with { LiveMode = true }, default));
        Equal(0, f.Store.Receipts.Count);
    }),
    ("Failure before commit leaves no receipt; retry succeeds", async () =>
    {
        var f = new Fixture(); f.PaidInvoice("in_1", f.Now, f.Now.AddMonths(1));
        f.Store.FailNextSave = true;
        await Throws<IOException>(() => f.Service.ProcessAsync(f.Event("evt_1", "invoice.paid", "in_1"), default));
        Equal(0, f.Store.Receipts.Count);
        Equal(0, f.Store.State.Payments.Count);
        await f.Service.ProcessAsync(f.Event("evt_1", "invoice.paid", "in_1"), default);
        Equal(1, f.Store.State.Payments.Count);
    }),
    ("Checkout timeout retries the same operation and parameters", async () =>
    {
        var f = new Fixture(); f.Store.State.Checkouts.Clear();
        f.Api.Responses["prices/price_month"] = Fixture.Price("month");
        string? originalKey = null; string? originalValues = null;
        f.Api.Post = (_, values, key) =>
        {
            if (originalKey == null)
            {
                originalKey = key; originalValues = JsonSerializer.Serialize(values);
                throw new HttpRequestException("Simulated lost response after Stripe accepted request");
            }
            Equal(originalKey, key); Equal(originalValues, JsonSerializer.Serialize(values));
            return Json(new { id = "cs_retry", url = "https://checkout.stripe.com/c/retry" });
        };
        await Throws<HttpRequestException>(() => f.Service.CheckoutAsync(1, f.Household, BillingPlan.Monthly, default));
        Equal(1, f.Store.State.Checkouts.Count);
        await f.Service.CheckoutAsync(1, f.Household, BillingPlan.Monthly, default);
        Equal(1, f.Store.State.Checkouts.Count);
        Equal(BillingPlan.Free, f.Store.State.Resolve(f.Now).Plan);
    }),
    ("Expired unpaid checkout remains Free", async () =>
    {
        var f = new Fixture();
        f.Api.Responses["checkout/sessions/cs_expired"] = Json(new { id = "cs_expired", customer = "cus_7", livemode = false,
            metadata = new { household_id = "7", checkout_attempt_id = f.AttemptId.ToString() }, mode = "subscription", status = "expired" });
        await f.Service.ProcessAsync(f.Event("evt_expired", "checkout.session.expired", "cs_expired"), default);
        Equal("expired", f.Store.State.Checkouts[0].Status);
        Equal(BillingPlan.Free, f.Store.State.Resolve(f.Now).Plan);
        Equal("Free", f.Store.State.Resolve(f.Now).Status);
    }),
    ("Paid invoice without a successful Stripe payment cannot grant access", async () =>
    {
        var f = new Fixture(); f.PaidInvoice("in_1", f.Now, f.Now.AddMonths(1));
        f.Api.Responses["payment_intents/pi_in_1"] = Json(new { id = "pi_in_1", customer = "cus_7", livemode = false, status = "processing", amount_received = 0 });
        await f.Service.ProcessAsync(f.Event("evt_unconfirmed", "invoice.paid", "in_1"), default);
        Equal(BillingPlan.Free, f.Store.State.Resolve(f.Now).Plan);
    }),
    ("Delayed cancellation of old subscription cannot replace a newer paid subscription", async () =>
    {
        var f = new Fixture(); f.PaidInvoice("in_old", f.Now.AddMonths(-2), f.Now.AddMonths(-1));
        f.Subscription("canceled", "in_old", f.Now.AddMonths(-1));
        f.Store.State.Subscriptions.Add(new() { Id = "sub_new", Status = "active", CreatedAt = f.Now,
            CurrentPeriodEnd = f.Now.AddYears(1), PriceId = "price_year" });
        f.Store.State.RecordPayment(new("in_new", BillingPlan.Yearly, "price_year", "sub_new", "pi_new", f.Now, f.Now, f.Now.AddYears(1), 6999, "usd"));
        await f.Service.ProcessAsync(f.Event("evt_old_cancel", "customer.subscription.deleted", "sub_1"), default);
        Equal(BillingPlan.Yearly, f.Store.State.Resolve(f.Now).Plan);
        Equal("Active", f.Store.State.Resolve(f.Now).Status);
        Equal(f.Now.AddYears(1), f.Store.State.Resolve(f.Now).RenewsAt);
    }),
    ("Portal binds to persisted customer and cannot activate a plan", async () =>
    {
        var f = new Fixture();
        f.Api.Post = (path, values, _) =>
        {
            Equal("billing_portal/sessions", path); Equal("cus_7", values["customer"]);
            Equal("https://petpotty.example/Billing", values["return_url"]);
            return Json(new { url = "https://billing.stripe.com/p/session/test" });
        };
        await f.Service.PortalAsync(1, 7, default);
        Equal(BillingPlan.Free, f.Store.State.Resolve(f.Now).Plan);
    }),
    ("Webhook rejects bad signature and processes signed event", async () =>
    {
        var f = new Fixture(); f.PaidInvoice("in_1", f.Now, f.Now.AddMonths(1));
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = "evt_http", type = "invoice.paid",
            created = f.Now.ToUnixTimeSeconds(), livemode = false, data = new { @object = new { id = "in_1", customer = "cus_7" } } }));
        async Task<int?> Send(string signature)
        {
            var http = new DefaultHttpContext(); http.Request.Body = new MemoryStream(payload);
            http.Request.Headers["Stripe-Signature"] = signature;
            var result = await StripeWebhook.HandleAsync(http.Request, Options.Create(f.Options), f.Service, f.Clock, NullLoggerFactory.Instance);
            return ((IStatusCodeHttpResult)result).StatusCode;
        }
        Equal<int?>(400, await Send("invalid")); Equal(0, f.Store.State.Payments.Count);
        f.Store.FailNextSave = true;
        Equal<int?>(500, await Send(Sign(payload, f.Now, "whsec_test"))); Equal(0, f.Store.Receipts.Count);
        Equal<int?>(200, await Send(Sign(payload, f.Now, "whsec_test"))); Equal(1, f.Store.State.Payments.Count);
    })
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures++; Console.WriteLine($"FAIL {name}: {e}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
if (args.Contains("--sql"))
{
    try { await SqlIntegration.RunAsync(); Console.WriteLine("PASS SQL migration and persistence integration"); }
    catch (Exception e) { failures++; Console.WriteLine($"FAIL SQL integration: {e}"); }
}
return failures == 0 ? 0 : 1;

static Task Run(Action action) { action(); return Task.CompletedTask; }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
static void True(bool value) => Equal(true, value);
static void False(bool value) => Equal(false, value);
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
static string Sign(byte[] bytes, DateTimeOffset now, string secret)
{
    var prefix = Encoding.UTF8.GetBytes(now.ToUnixTimeSeconds() + ".");
    var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), prefix.Concat(bytes).ToArray());
    return $"t={now.ToUnixTimeSeconds()},v1={Convert.ToHexString(signature).ToLowerInvariant()}";
}

sealed class Fixture
{
    public DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    public FakeStore Store = new();
    public FakeStripe Api = new();
    public FixedClock Clock;
    public HouseholdContext Household = new() { HouseholdID = 7, PublicID = Guid.NewGuid(), Role = HouseholdRole.Owner };
    public StripeOptions Options = new()
    {
        SecretKey = "sk_test_fixture", WebhookSecret = "whsec_test", BaseUrl = "https://petpotty.example",
        Plans = new() { ["Month"] = new() { PriceId = "price_month" }, ["Year"] = new() { PriceId = "price_year" }, ["Lifetime"] = new() { PriceId = "price_life" } }
    };
    public HouseholdBillingService Service;
    public Guid AttemptId = Guid.NewGuid();
    public Fixture()
    {
        Clock = new(Now);
        Store.State.CustomerId = "cus_7";
        Store.State.Checkouts.Add(new() { Id = AttemptId, Plan = BillingPlan.Monthly, PriceId = "price_month", CreatedAt = Now });
        Service = new(Store, Api, new FakeAuthorization(), Microsoft.Extensions.Options.Options.Create(Options), Clock, NullLogger<HouseholdBillingService>.Instance);
    }
    private object Metadata => new { household_id = "7", checkout_attempt_id = AttemptId.ToString() };
    public StripeEvent Event(string id, string type, string obj) => new(id, type, obj, "cus_7", Now, false);
    public PaidPeriod Payment(string id, DateTimeOffset start, DateTimeOffset end) => new(id, BillingPlan.Monthly, "price_month", "sub_1", null, start, start, end, 699, "usd");
    public static JsonElement Price(string interval) => J(new { active = true, livemode = false, unit_amount = 699, type = "recurring", recurring = new { interval, interval_count = 1 } });
    public void Subscription(string status, string invoice, DateTimeOffset end, bool cancel = false, string price = "price_month")
    {
        Api.Responses["subscriptions/sub_1"] = J(new
        {
            id = "sub_1", customer = "cus_7", livemode = false, metadata = Metadata, status,
            created = Now.AddMonths(-1).ToUnixTimeSeconds(), latest_invoice = invoice, cancel_at_period_end = cancel,
            items = new { data = new[] { new { price = new { id = price }, current_period_end = end.ToUnixTimeSeconds() } }, has_more = false }
        });
    }
    public void PaidInvoice(string id, DateTimeOffset start, DateTimeOffset end, bool updateSubscription = true,
        bool paid = true, long amount = 699, bool outOfBand = false, string price = "price_month", string customer = "cus_7")
    {
        Api.Responses[$"invoice_payments?invoice={id}&status=paid&limit=100"] = J(new
        {
            data = new[] { new { id = "inpay_" + id, invoice = id, status = "paid", amount_paid = amount,
                payment = new { type = "payment_intent", payment_intent = "pi_" + id } } }, has_more = false
        });
        Api.Responses[$"payment_intents/pi_{id}"] = J(new { id = "pi_" + id, customer, livemode = false,
            status = "succeeded", amount_received = amount });
        Api.Responses[$"invoices/{id}"] = J(new
        {
            id, customer, livemode = false, status = paid ? "paid" : "open", amount_paid = paid ? amount : 0,
            paid_out_of_band = outOfBand, currency = "usd", parent = new { subscription_details = new { subscription = "sub_1" } },
            // Deliberately different from the invoice line period: invoice.period_end must not grant access.
            period_end = start.ToUnixTimeSeconds(), status_transitions = new { paid_at = start.ToUnixTimeSeconds() }
        });
        Api.Responses[$"invoices/{id}/lines?limit=100"] = J(new
        {
            data = new[] { new { id = "il_1", amount, quantity = 1, parent = new { subscription_item_details = new { subscription = "sub_1", proration = false } },
                pricing = new { price_details = new { price } }, period = new { start = start.ToUnixTimeSeconds(), end = end.ToUnixTimeSeconds() } } }, has_more = false
        });
        if (updateSubscription) Subscription("active", id, end, price: price);
    }
    public void Lifetime(bool paid)
    {
        var attempt = Store.State.Checkouts[0]; attempt.Plan = BillingPlan.Lifetime; attempt.PriceId = "price_life";
        Api.Responses["checkout/sessions/cs_life"] = J(new { id = "cs_life", customer = "cus_7", livemode = false, metadata = Metadata,
            mode = "payment", status = "complete", payment_status = paid ? "paid" : "unpaid", payment_intent = "pi_life" });
        Api.Responses["payment_intents/pi_life"] = J(new { id = "pi_life", customer = "cus_7", livemode = false, metadata = Metadata,
            status = paid ? "succeeded" : "processing", amount_received = paid ? 13500 : 0, currency = "usd" });
        Api.Responses["checkout/sessions/cs_life/line_items?limit=100"] = J(new { data = new[] { new { price = new { id = "price_life" }, quantity = 1 } }, has_more = false });
    }
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);
}

sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
sealed class FakeAuthorization : IHouseholdAuthorizationService
{
    public HouseholdRole? GetRole(int userID, int householdID) => householdID != 7 ? null : userID switch { 1 => HouseholdRole.Owner, 2 => HouseholdRole.Member, 3 => HouseholdRole.Caregiver, _ => null };
    public bool HasPermission(int userID, int householdID, HouseholdPermission permission) => GetRole(userID, householdID) is { } role && HouseholdAccessRules.HasPermission(role, permission);
}
sealed class FakeStripe : IStripeGateway
{
    public Dictionary<string, JsonElement> Responses = [];
    public Func<string, Dictionary<string, string>, string?, JsonElement>? Post;
    public int Calls, PostCalls;
    public Task<JsonElement> GetAsync(string path, CancellationToken ct)
    { Calls++; return Task.FromResult(Responses.TryGetValue(path, out var response) ? response : throw new Exception("Unexpected Stripe GET: " + path)); }
    public Task<JsonElement> PostAsync(string path, Dictionary<string, string> values, string? key, CancellationToken ct)
    { Calls++; PostCalls++; return Task.FromResult(Post?.Invoke(path, values, key) ?? throw new Exception("Unexpected Stripe POST: " + path)); }
}
sealed class FakeStore : IBillingStore
{
    public HouseholdBilling State = new();
    public HashSet<string> Receipts = [];
    public bool FailNextSave;
    private readonly SemaphoreSlim gate = new(1);
    public Task<HouseholdBilling> ReadAsync(int id, CancellationToken ct) => Task.FromResult(Clone(State));
    public Task<int?> FindHouseholdAsync(string id, CancellationToken ct) => Task.FromResult<int?>(id == State.CustomerId ? 7 : null);
    public async Task<IBillingLease> LockAsync(int id, CancellationToken ct)
    { await gate.WaitAsync(ct); return new Lease(this, Clone(State)); }
    private static HouseholdBilling Clone(HouseholdBilling value) => JsonSerializer.Deserialize<HouseholdBilling>(JsonSerializer.Serialize(value))!;
    private sealed class Lease(FakeStore store, HouseholdBilling state) : IBillingLease
    {
        public HouseholdBilling State => state;
        public Task<bool> WasProcessedAsync(string id, CancellationToken ct) => Task.FromResult(store.Receipts.Contains(id));
        public Task SaveAsync(StripeEvent? receipt, CancellationToken ct)
        {
            if (store.FailNextSave) { store.FailNextSave = false; throw new IOException("Simulated database failure"); }
            store.State = Clone(state);
            if (receipt != null) store.Receipts.Add(receipt.Id);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { store.gate.Release(); return ValueTask.CompletedTask; }
    }
}
