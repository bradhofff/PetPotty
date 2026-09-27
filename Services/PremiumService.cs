using System.Data;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PetPotty.Models;

namespace PetPotty.Services;

public sealed class PremiumService : IPremiumService
{
    private const string StripeApiBase = "https://api.stripe.com/v1";
    private readonly string _connectionString;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PremiumService> _logger;

    public PremiumService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<PremiumService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    public HouseholdPremiumStatus? GetStatus(int userID, int householdID)
    {
        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            SELECT h.HouseholdID, h.Name, h.PremiumStatus, h.PremiumPlanType, h.StripeCustomerID,
                   h.StripeSubscriptionID, h.PremiumCurrentPeriodEndUtc,
                   h.PremiumCancelAtPeriodEnd, h.PremiumUpdatedAtUtc
            FROM dbo.Households h
            INNER JOIN dbo.HouseholdMembers hm ON hm.HouseholdID = h.HouseholdID
            WHERE h.HouseholdID = @HouseholdID
              AND hm.UserID = @UserID
              AND hm.Status = N'Active';
            """, connection);
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        connection.Open();
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return MapStatus(reader);
    }

    public async Task<PremiumCheckoutSession> CreateCheckoutSessionAsync(
        int userID,
        HouseholdContext household,
        string priceID,
        string planType,
        string mode,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var secretKey = RequiredSetting("Stripe:SecretKey");
        if (string.IsNullOrWhiteSpace(priceID))
            throw new InvalidOperationException("Select a configured Premium plan.");
        if (planType is not ("monthly" or "yearly" or "lifetime"))
            throw new InvalidOperationException("The selected Premium plan has an invalid plan type.");
        if (mode is not ("payment" or "subscription"))
            throw new InvalidOperationException("The selected Premium plan has an invalid checkout mode.");
        if ((planType == "lifetime") != (mode == "payment"))
            throw new InvalidOperationException("The selected Premium plan has an invalid checkout mode.");
        var existing = GetStatus(userID, household.HouseholdID);
        if (existing == null)
            throw new InvalidOperationException("The selected household is not available.");
        if (existing.IsPremium)
            throw new InvalidOperationException("This household already has Premium.");

        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var form = new Dictionary<string, string>
        {
            ["mode"] = mode,
            ["line_items[0][price]"] = priceID,
            ["line_items[0][quantity]"] = "1",
            ["client_reference_id"] = household.PublicID.ToString(),
            ["success_url"] = $"{normalizedBaseUrl}/Premium?checkout=success",
            ["cancel_url"] = $"{normalizedBaseUrl}/Premium?checkout=cancelled",
            ["metadata[household_public_id]"] = household.PublicID.ToString(),
            ["metadata[purchaser_user_id]"] = userID.ToString(CultureInfo.InvariantCulture),
            ["metadata[premium_plan_type]"] = planType
        };
        if (mode == "subscription")
        {
            form["subscription_data[metadata][household_public_id]"] = household.PublicID.ToString();
            form["subscription_data[metadata][purchaser_user_id]"] = userID.ToString(CultureInfo.InvariantCulture);
            form["subscription_data[metadata][premium_plan_type]"] = planType;
        }
        if (!string.IsNullOrWhiteSpace(existing.StripeCustomerID))
            form["customer"] = existing.StripeCustomerID;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{StripeApiBase}/checkout/sessions")
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);

        var client = _httpClientFactory.CreateClient("Stripe");
        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Stripe Checkout session creation failed with {StatusCode}: {Response}",
                response.StatusCode, responseBody);
            throw new InvalidOperationException("Stripe could not start checkout. Please try again.");
        }

        using var json = JsonDocument.Parse(responseBody);
        var url = json.RootElement.TryGetProperty("url", out var urlElement)
            ? urlElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://checkout.stripe.com/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Stripe returned an invalid checkout URL.");

        return new PremiumCheckoutSession(url);
    }

    public async Task ProcessWebhookAsync(
        string payload,
        string? signature,
        CancellationToken cancellationToken = default)
    {
        VerifyWebhookSignature(payload, signature, RequiredSetting("Stripe:WebhookSecret"));

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var eventID = RequiredString(root, "id");
        var eventType = RequiredString(root, "type");
        var eventCreatedAtUtc = FromUnixSeconds(root.TryGetProperty("created", out var created)
            ? created.GetInt64()
            : 0);
        var data = root.GetProperty("data").GetProperty("object");

        var stripeEvent = ParseStripeEvent(eventID, eventType, eventCreatedAtUtc, data);
        if (stripeEvent.HouseholdPublicID == null && string.IsNullOrWhiteSpace(stripeEvent.StripeSubscriptionID)
            && string.IsNullOrWhiteSpace(stripeEvent.StripeCustomerID))
        {
            _logger.LogInformation("Ignoring Stripe event {EventID} without a household reference", eventID);
            return;
        }

        var household = ResolveHousehold(stripeEvent.HouseholdPublicID,
            stripeEvent.StripeSubscriptionID, stripeEvent.StripeCustomerID);
        if (household == null)
        {
            _logger.LogWarning("Ignoring Stripe event {EventID}; no matching household was found", eventID);
            return;
        }

        ApplyStripeEvent(household.Value.HouseholdID, stripeEvent);
    }

    private StripeEvent ParseStripeEvent(
        string eventID,
        string eventType,
        DateTime eventCreatedAtUtc,
        JsonElement data)
    {
        var metadata = ReadMetadata(data);
        var planType = metadata.TryGetValue("premium_plan_type", out var rawPlanType)
            && rawPlanType is "monthly" or "yearly" or "lifetime"
                ? rawPlanType : null;
        var householdPublicID = ParseGuid(metadata, "household_public_id");
        if (householdPublicID == null)
            householdPublicID = ParseGuid(data, "client_reference_id");
        var subscriptionID = ReadString(data, "subscription");
        if (eventType.StartsWith("customer.subscription.", StringComparison.Ordinal))
            subscriptionID = ReadString(data, "id");

        var customerID = ReadString(data, "customer");
        var status = eventType switch
        {
            "checkout.session.completed" when ReadString(data, "mode") == "payment"
                && ReadString(data, "payment_status") == "paid" => "active",
            "checkout.session.async_payment_succeeded" when ReadString(data, "mode") == "payment" => "active",
            "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted"
                => ReadString(data, "status") ?? "free",
            _ => null
        };

        DateTime? currentPeriodEnd = null;
        if (data.TryGetProperty("current_period_end", out var currentPeriodEndValue)
            && currentPeriodEndValue.ValueKind == JsonValueKind.Number)
            currentPeriodEnd = FromUnixSeconds(currentPeriodEndValue.GetInt64());

        var cancelAtPeriodEnd = data.TryGetProperty("cancel_at_period_end", out var cancelValue)
            && cancelValue.ValueKind == JsonValueKind.True;

        return new StripeEvent
        {
            EventID = eventID,
            EventType = eventType,
            EventCreatedAtUtc = eventCreatedAtUtc,
            HouseholdPublicID = householdPublicID,
            UserID = ParseInt(metadata, "purchaser_user_id"),
            PlanType = status == null ? null : planType,
            CheckoutSessionID = eventType.StartsWith("checkout.session.", StringComparison.Ordinal)
                ? ReadString(data, "id") : null,
            StripeCustomerID = customerID,
            StripeSubscriptionID = subscriptionID,
            StripeInvoiceID = ReadString(data, "id", eventType.StartsWith("invoice.", StringComparison.Ordinal)),
            StripePaymentIntentID = ReadString(data, "payment_intent"),
            AmountMinor = ReadLong(data, "amount_paid") ?? ReadLong(data, "amount_total"),
            Currency = ReadString(data, "currency"),
            TransactionStatus = eventType == "invoice.payment_failed" ? "payment_failed"
                : eventType == "invoice.paid" ? "paid"
                : eventType == "customer.subscription.deleted" ? "canceled"
                : "completed",
            EntitlementStatus = status,
            CurrentPeriodEndUtc = currentPeriodEnd,
            CancelAtPeriodEnd = eventType.StartsWith("customer.subscription.", StringComparison.Ordinal)
                ? cancelAtPeriodEnd : null
        };
    }

    private void ApplyStripeEvent(int householdID, StripeEvent stripeEvent)
    {
        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("dbo.ApplyHouseholdPremiumStripeEvent", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = (object?)stripeEvent.UserID ?? DBNull.Value;
        AddNullable(command, "@PremiumPlanType", stripeEvent.PlanType, 20);
        command.Parameters.Add("@StripeEventID", SqlDbType.NVarChar, 255).Value = stripeEvent.EventID;
        command.Parameters.Add("@StripeEventType", SqlDbType.NVarChar, 100).Value = stripeEvent.EventType;
        AddNullable(command, "@StripeCheckoutSessionID", stripeEvent.CheckoutSessionID, 255);
        AddNullable(command, "@StripeCustomerID", stripeEvent.StripeCustomerID, 255);
        AddNullable(command, "@StripeSubscriptionID", stripeEvent.StripeSubscriptionID, 255);
        AddNullable(command, "@StripeInvoiceID", stripeEvent.StripeInvoiceID, 255);
        AddNullable(command, "@StripePaymentIntentID", stripeEvent.StripePaymentIntentID, 255);
        command.Parameters.Add("@AmountMinor", SqlDbType.BigInt).Value = (object?)stripeEvent.AmountMinor ?? DBNull.Value;
        AddNullable(command, "@Currency", stripeEvent.Currency, 3);
        command.Parameters.Add("@TransactionStatus", SqlDbType.NVarChar, 40).Value = stripeEvent.TransactionStatus;
        command.Parameters.Add("@EventCreatedAtUtc", SqlDbType.DateTime2).Value = stripeEvent.EventCreatedAtUtc;
        AddNullable(command, "@EntitlementStatus", stripeEvent.EntitlementStatus, 20);
        command.Parameters.Add("@CurrentPeriodEndUtc", SqlDbType.DateTime2).Value = (object?)stripeEvent.CurrentPeriodEndUtc ?? DBNull.Value;
        command.Parameters.Add("@CancelAtPeriodEnd", SqlDbType.Bit).Value = (object?)stripeEvent.CancelAtPeriodEnd ?? DBNull.Value;

        connection.Open();
        var applied = command.ExecuteScalar() is true;
        _logger.LogInformation("Stripe event {EventID} ({EventType}) for household {HouseholdID}: applied={Applied}",
            stripeEvent.EventID, stripeEvent.EventType, householdID, applied);
    }

    private (int HouseholdID, Guid PublicID)? ResolveHousehold(Guid? publicID, string? subscriptionID, string? customerID)
    {
        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            SELECT TOP (1) HouseholdID, PublicID
            FROM dbo.Households
            WHERE (@PublicID IS NOT NULL AND PublicID = @PublicID)
               OR (@SubscriptionID IS NOT NULL AND StripeSubscriptionID = @SubscriptionID)
               OR (@CustomerID IS NOT NULL AND StripeCustomerID = @CustomerID);
            """, connection);
        command.Parameters.Add("@PublicID", SqlDbType.UniqueIdentifier).Value = (object?)publicID ?? DBNull.Value;
        AddNullable(command, "@SubscriptionID", subscriptionID, 255);
        AddNullable(command, "@CustomerID", customerID, 255);
        connection.Open();
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt32(0), reader.GetGuid(1))
            : null;
    }

    private static HouseholdPremiumStatus MapStatus(SqlDataReader reader) => new()
    {
        HouseholdID = reader.GetInt32(0),
        HouseholdName = reader.GetString(1),
        Status = reader.GetString(2),
        PlanType = reader.GetString(3),
        StripeCustomerID = reader.IsDBNull(4) ? null : reader.GetString(4),
        StripeSubscriptionID = reader.IsDBNull(5) ? null : reader.GetString(5),
        CurrentPeriodEndUtc = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
        CancelAtPeriodEnd = reader.GetBoolean(7),
        UpdatedAtUtc = reader.IsDBNull(8) ? null : reader.GetDateTime(8)
    };

    private static Dictionary<string, string> ReadMetadata(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!element.TryGetProperty("metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in metadata.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = property.Value.GetString() ?? string.Empty;
        return result;
    }

    private static string? ReadString(JsonElement element, string propertyName, bool enabled = true) =>
        enabled && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? ReadLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number : null;

    private static Guid? ParseGuid(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && Guid.TryParse(value, out var parsed) ? parsed : null;

    private static Guid? ParseGuid(JsonElement element, string propertyName) =>
        ReadString(element, propertyName) is { } value && Guid.TryParse(value, out var parsed) ? parsed : null;

    private static int? ParseInt(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : null;

    private string RequiredSetting(string key) =>
        _configuration[key]?.Trim() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Missing configuration value '{key}'.");

    private static string RequiredString(JsonElement element, string propertyName) =>
        ReadString(element, propertyName)
            ?? throw new InvalidOperationException($"Stripe event is missing '{propertyName}'.");

    private static DateTime FromUnixSeconds(long seconds) =>
        seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.UtcNow;

    private static void AddNullable(SqlCommand command, string name, string? value, int size)
    {
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = (object?)value ?? DBNull.Value;
    }

    private static void VerifyWebhookSignature(string payload, string? signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(signature))
            throw new InvalidOperationException("Missing Stripe webhook signature.");

        long timestamp = 0;
        var signatures = new List<string>();
        foreach (var part in signature.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length != 2) continue;
            if (pieces[0] == "t") long.TryParse(pieces[1], out timestamp);
            if (pieces[0] == "v1") signatures.Add(pieces[1]);
        }

        if (timestamp <= 0 || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > 300 || signatures.Count == 0)
            throw new InvalidOperationException("Invalid Stripe webhook signature.");

        var signedPayload = $"{timestamp}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload))).ToLowerInvariant();
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        if (!signatures.Any(signatureValue =>
                signatureValue.Length == expected.Length
                && CryptographicOperations.FixedTimeEquals(expectedBytes, Encoding.UTF8.GetBytes(signatureValue))))
            throw new InvalidOperationException("Invalid Stripe webhook signature.");
    }

    private sealed class StripeEvent
    {
        public string EventID { get; init; } = string.Empty;
        public string EventType { get; init; } = string.Empty;
        public DateTime EventCreatedAtUtc { get; init; }
        public Guid? HouseholdPublicID { get; init; }
        public int? UserID { get; init; }
        public string? PlanType { get; init; }
        public string? CheckoutSessionID { get; init; }
        public string? StripeCustomerID { get; init; }
        public string? StripeSubscriptionID { get; init; }
        public string? StripeInvoiceID { get; init; }
        public string? StripePaymentIntentID { get; init; }
        public long? AmountMinor { get; init; }
        public string? Currency { get; init; }
        public string TransactionStatus { get; init; } = string.Empty;
        public string? EntitlementStatus { get; init; }
        public DateTime? CurrentPeriodEndUtc { get; init; }
        public bool? CancelAtPeriodEnd { get; init; }
    }
}
