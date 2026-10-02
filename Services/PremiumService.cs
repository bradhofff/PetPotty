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
    private readonly IWebHostEnvironment _environment;

    public PremiumService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<PremiumService> logger,
        IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _environment = environment;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    public bool BillingPortalConfigured
    {
        get
        {
            var key = _configuration["Stripe:SecretKey"]?.Trim() ?? string.Empty;
            var validKey = _environment.IsProduction()
                ? key.StartsWith("sk_live_", StringComparison.Ordinal) || key.StartsWith("rk_live_", StringComparison.Ordinal)
                : key.StartsWith("sk_test_", StringComparison.Ordinal) || key.StartsWith("rk_test_", StringComparison.Ordinal);
            return validKey
                && !string.IsNullOrWhiteSpace(_configuration["Stripe:WebhookSecret"])
                && (_configuration["Stripe:BillingPortalConfigurationId"]?.StartsWith("bpc_", StringComparison.Ordinal) == true)
                && (!_environment.IsProduction() || (Uri.TryCreate(_configuration["App:BaseUrl"], UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps));
        }
    }

    public HouseholdPremiumStatus? GetStatus(int userID, int householdID)
    {
        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            SELECT h.HouseholdID, h.Name, h.PremiumStatus, h.PremiumPlanType, h.StripeCustomerID,
                   h.StripeSubscriptionID, h.PremiumCurrentPeriodEndUtc,
                   h.PremiumCancelAtPeriodEnd, h.PremiumUpdatedAtUtc,
                   (SELECT TOP (1) t.UserID FROM dbo.HouseholdPremiumTransactions t
                    WHERE t.HouseholdID = h.HouseholdID AND t.StripeSubscriptionID = h.StripeSubscriptionID
                      AND t.UserID IS NOT NULL
                      AND t.StripeEventType IN (N'checkout.session.completed', N'customer.subscription.created', N'customer.subscription.updated')
                    ORDER BY t.EventCreatedAtUtc DESC, t.HouseholdPremiumTransactionID DESC) AS BillingManagerUserID
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

    public async Task<string> CreateBillingPortalSessionAsync(int userID, int householdID, string baseUrl,
        CancellationToken cancellationToken = default)
    {
        // Recheck active membership and the purchaser on every request. Never accept a customer ID from the browser.
        var status = GetStatus(userID, householdID);
        if (status?.CanManageBilling(userID) != true)
            throw new UnauthorizedAccessException("Only the subscription purchaser can manage billing.");
        if (!BillingPortalConfigured)
            throw new InvalidOperationException("Billing management is temporarily unavailable. Please contact support.");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && !(_environment.IsDevelopment() && baseUri.IsLoopback && baseUri.Scheme == Uri.UriSchemeHttp)))
            throw new InvalidOperationException("Billing management is temporarily unavailable. Please contact support.");

        var secretKey = RequiredSetting("Stripe:SecretKey");
        var configurationID = RequiredSetting("Stripe:BillingPortalConfigurationId");
        var client = _httpClientFactory.CreateClient("Stripe");
        using var configurationRequest = new HttpRequestMessage(HttpMethod.Get,
            $"{StripeApiBase}/billing_portal/configurations/{Uri.EscapeDataString(configurationID)}");
        configurationRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        using var configurationResponse = await client.SendAsync(configurationRequest, cancellationToken);
        if (!configurationResponse.IsSuccessStatusCode)
            throw new InvalidOperationException("Billing management is temporarily unavailable. Please contact support.");
        using var configuration = JsonDocument.Parse(await configurationResponse.Content.ReadAsStringAsync(cancellationToken));
        var portal = configuration.RootElement;
        var features = portal.GetProperty("features");
        var cancel = features.GetProperty("subscription_cancel");
        if (!portal.GetProperty("active").GetBoolean() || !cancel.GetProperty("enabled").GetBoolean()
            || ReadString(cancel, "mode") != "at_period_end"
            || features.GetProperty("subscription_update").GetProperty("enabled").GetBoolean())
            throw new InvalidOperationException("Billing management is temporarily unavailable. Please contact support.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{StripeApiBase}/billing_portal/sessions")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["customer"] = status.StripeCustomerID!,
                ["configuration"] = configurationID,
                ["return_url"] = $"{baseUrl.TrimEnd('/')}/Profile?billing=return#premium-settings"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Stripe billing portal session creation failed with {StatusCode}", response.StatusCode);
            throw new InvalidOperationException("Stripe could not open billing management. Please try again.");
        }
        using var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var url = ReadString(session.RootElement, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var portalUri) || portalUri.Scheme != Uri.UriSchemeHttps
            || portalUri.Host != "billing.stripe.com" || !string.IsNullOrEmpty(portalUri.UserInfo) || !portalUri.IsDefaultPort)
            throw new InvalidOperationException("Stripe returned an invalid billing URL.");
        return url!;
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
        RequiredSetting("Stripe:WebhookSecret");
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
        if (existing.IsPremium || existing.HasBillingSubscription)
            throw new InvalidOperationException("This household already has a Premium plan. Manage its billing before purchasing another.");

        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var form = new Dictionary<string, string>
        {
            ["mode"] = mode,
            ["line_items[0][price]"] = priceID,
            ["line_items[0][quantity]"] = "1",
            ["client_reference_id"] = household.PublicID.ToString(),
            ["success_url"] = $"{normalizedBaseUrl}/Purchase?checkout=success",
            ["cancel_url"] = $"{normalizedBaseUrl}/Purchase?checkout=cancelled",
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
        else if (string.IsNullOrWhiteSpace(existing.StripeCustomerID))
        {
            // Keep the customer on the household for receipts and future billing support.
            form["customer_creation"] = "always";
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
        var secretKey = RequiredSetting("Stripe:SecretKey");
        var liveMode = secretKey.StartsWith("sk_live_", StringComparison.Ordinal)
            || secretKey.StartsWith("rk_live_", StringComparison.Ordinal);
        var testMode = secretKey.StartsWith("sk_test_", StringComparison.Ordinal)
            || secretKey.StartsWith("rk_test_", StringComparison.Ordinal);
        if ((!liveMode && !testMode)
            || !root.TryGetProperty("livemode", out var eventMode)
            || eventMode.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || (eventMode.GetBoolean() != liveMode))
            throw new InvalidOperationException("Stripe event mode does not match the configured API key.");
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

        DateTime? currentPeriodEnd = ReadUnixDate(data, "current_period_end");
        // Stripe API versions from 2025-03-31 onward put the billing period on
        // subscription items. The older top-level field is still accepted.
        if (currentPeriodEnd == null && eventType.StartsWith("customer.subscription.", StringComparison.Ordinal)
            && data.TryGetProperty("items", out var items)
            && items.TryGetProperty("data", out var itemData)
            && itemData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemData.EnumerateArray())
            {
                var itemEnd = ReadUnixDate(item, "current_period_end");
                if (itemEnd.HasValue && (!currentPeriodEnd.HasValue || itemEnd > currentPeriodEnd))
                    currentPeriodEnd = itemEnd;
            }
        }

        var cancelAtPeriodEnd = data.TryGetProperty("cancel_at_period_end", out var cancelValue)
            && cancelValue.ValueKind == JsonValueKind.True;
        // Flexible billing can represent a scheduled cancellation through cancel_at instead.
        var cancelAt = ReadUnixDate(data, "cancel_at");
        if (cancelAt.HasValue)
        {
            cancelAtPeriodEnd = true;
            currentPeriodEnd = cancelAt;
        }

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
        UpdatedAtUtc = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
        BillingManagerUserID = reader.IsDBNull(9) ? null : reader.GetInt32(9)
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

    private static DateTime? ReadUnixDate(JsonElement element, string propertyName) =>
        ReadLong(element, propertyName) is { } seconds && seconds > 0
            ? FromUnixSeconds(seconds) : null;

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
