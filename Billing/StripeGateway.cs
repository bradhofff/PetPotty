using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PetPotty.Billing;

public interface IStripeGateway
{
    Task<JsonElement> GetAsync(string path, CancellationToken ct);
    Task<JsonElement> PostAsync(string path, Dictionary<string, string> values, string? idempotencyKey, CancellationToken ct);
}

// Small, version-pinned Stripe REST client. No secret or response body is logged.
public sealed class StripeGateway(HttpClient client, IOptions<StripeOptions> options) : IStripeGateway
{
    public Task<JsonElement> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, null, ct);
    public Task<JsonElement> PostAsync(string path, Dictionary<string, string> values, string? idempotencyKey, CancellationToken ct)
        => SendAsync(HttpMethod.Post, path, values, idempotencyKey, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, Dictionary<string, string>? values,
        string? idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, "https://api.stripe.com/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.SecretKey);
        request.Headers.Add("Stripe-Version", StripeOptions.ApiVersion);
        if (idempotencyKey != null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (values != null) request.Content = new FormUrlEncodedContent(values);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Stripe returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return json.RootElement.Clone();
    }
}

public static class StripeJson
{
    public static JsonElement At(this JsonElement node, params string[] path)
    {
        foreach (var name in path)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node)) return default;
        }
        return node;
    }
    public static string Text(this JsonElement node) => node.ValueKind == JsonValueKind.String ? node.GetString()! : "";
    public static string Id(this JsonElement node) => node.ValueKind == JsonValueKind.String ? node.Text() : node.At("id").Text();
    public static bool Flag(this JsonElement node) => node.ValueKind == JsonValueKind.True;
    public static long Number(this JsonElement node) => node.ValueKind == JsonValueKind.Number ? node.GetInt64() : 0;
    public static DateTimeOffset? Date(this JsonElement node) => node.Number() > 0 ? DateTimeOffset.FromUnixTimeSeconds(node.Number()) : null;
    public static IEnumerable<JsonElement> Items(this JsonElement node) => node.ValueKind == JsonValueKind.Array ? node.EnumerateArray() : [];
    public static string PathId(string id) => Uri.EscapeDataString(id);
}
