using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PetPotty.Billing;

public static class StripeSignature
{
    public static bool Verify(byte[] body, string header, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;
        var parts = header.Split(',').Select(p => p.Trim().Split('=', 2)).Where(p => p.Length == 2).ToArray();
        var timestamps = parts.Where(p => p[0] == "t").ToArray();
        if (timestamps.Length != 1 || !long.TryParse(timestamps[0][1], NumberStyles.None, CultureInfo.InvariantCulture, out var time)
            || time < now.ToUnixTimeSeconds() - 300 || time > now.ToUnixTimeSeconds() + 300) return false;
        var prefix = Encoding.UTF8.GetBytes(timestamps[0][1] + ".");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed, prefix.Length);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        foreach (var signature in parts.Where(p => p[0] == "v1"))
        {
            try
            {
                if (CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature[1]))) return true;
            }
            catch (FormatException) { }
        }
        return false;
    }
}

public static class StripeWebhook
{
    public static readonly HashSet<string> EventTypes =
    [
        "checkout.session.completed", "checkout.session.async_payment_succeeded",
        "checkout.session.async_payment_failed", "checkout.session.expired",
        "invoice.paid", "invoice.payment_succeeded", "invoice.payment_failed", "invoice.payment_action_required",
        "customer.subscription.created", "customer.subscription.updated", "customer.subscription.deleted",
        "customer.subscription.paused", "customer.subscription.resumed"
    ];

    public static async Task<IResult> HandleAsync(HttpRequest request, IOptions<StripeOptions> options,
        HouseholdBillingService billing, TimeProvider clock, ILoggerFactory logs)
    {
        if (!options.Value.IsConfigured) return Results.StatusCode(503);
        const int limit = 1024 * 1024;
        if (request.ContentLength > limit) return Results.StatusCode(413);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0)
        {
            if (body.Length + read > limit) return Results.StatusCode(413);
            body.Write(buffer, 0, read);
        }
        var bytes = body.ToArray();
        if (!StripeSignature.Verify(bytes, request.Headers["Stripe-Signature"].ToString(), options.Value.WebhookSecret, clock.GetUtcNow()))
            return Results.BadRequest();
        StripeEvent stripeEvent;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var type = root.At("type").Text();
            if (!EventTypes.Contains(type)) return Results.Ok();
            var obj = root.At("data", "object");
            stripeEvent = new(root.At("id").Text(), type, obj.Id(), obj.At("customer").Id(),
                root.At("created").Date() ?? throw new JsonException(), root.At("livemode").Flag());
            if (stripeEvent.LiveMode != options.Value.LiveMode || string.IsNullOrEmpty(stripeEvent.Id)
                || string.IsNullOrEmpty(stripeEvent.ObjectId) || string.IsNullOrEmpty(stripeEvent.CustomerId))
                return Results.BadRequest();
        }
        catch (Exception e) when (e is JsonException or ArgumentOutOfRangeException or FormatException)
        { return Results.BadRequest(); }
        try
        {
            await billing.ProcessAsync(stripeEvent, request.HttpContext.RequestAborted);
            return Results.Ok();
        }
        catch (Exception exception)
        {
            // A receipt is written only with a committed update. Stripe can safely retry any 5xx.
            logs.CreateLogger("StripeWebhook").LogError(exception, "Stripe event {EventId} could not be processed", stripeEvent.Id);
            return Results.StatusCode(500);
        }
    }
}
