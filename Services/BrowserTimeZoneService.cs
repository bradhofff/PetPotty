namespace PetPotty.Services;

public sealed class BrowserTimeZoneService(IConfiguration configuration) : IUserTimeZoneService
{
    public const string OffsetCookieName = "PetPottyUtcOffsetMinutes";

    private readonly int _fallbackOffset = Math.Clamp(
        configuration.GetValue<int?>("TimeZone:FallbackUtcOffsetMinutes") ?? 0,
        -840,
        840);

    public int GetUtcOffsetMinutes(HttpRequest request)
    {
        return request.Cookies.TryGetValue(OffsetCookieName, out var rawOffset)
            && int.TryParse(rawOffset, out var offset)
            && offset is >= -840 and <= 840
                ? offset
                : _fallbackOffset;
    }

    public DateTime ToUtc(DateTime localTime, int utcOffsetMinutes)
    {
        var offset = Math.Clamp(utcOffsetMinutes, -840, 840);
        var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(unspecified.AddMinutes(offset), DateTimeKind.Utc);
    }

    public DateTime ToLocal(DateTime utcTime, int utcOffsetMinutes)
    {
        var offset = Math.Clamp(utcOffsetMinutes, -840, 840);
        var normalizedUtc = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        return DateTime.SpecifyKind(normalizedUtc.AddMinutes(-offset), DateTimeKind.Unspecified);
    }
}

