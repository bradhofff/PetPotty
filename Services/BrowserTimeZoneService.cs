using Microsoft.Extensions.Configuration;

namespace PetPotty.Services;

public sealed class BrowserTimeZoneService : IUserTimeZoneService
{
    private readonly int _fallbackOffsetMinutes;

    public BrowserTimeZoneService(IConfiguration configuration)
    {
        _fallbackOffsetMinutes = Math.Clamp(
            configuration.GetValue<int?>("TimeZone:FallbackUtcOffsetMinutes") ?? 0,
            -840,
            840);
    }

    public int FallbackOffsetMinutes => _fallbackOffsetMinutes;

    public DateTime ToUtc(DateTime local, int utcOffsetMinutes)
    {
        var offset = Math.Clamp(utcOffsetMinutes, -840, 840);
        var utc = DateTime.SpecifyKind(local, DateTimeKind.Unspecified).AddMinutes(offset);
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    }

    public DateTime ToLocal(DateTime utc, int utcOffsetMinutes)
    {
        var offset = Math.Clamp(utcOffsetMinutes, -840, 840);
        return DateTime.SpecifyKind(utc, DateTimeKind.Unspecified).AddMinutes(-offset);
    }

    public int GetRequestOffsetMinutes(Microsoft.AspNetCore.Http.HttpContext context)
    {
        var raw = context.Request.Cookies["tzOffset"];
        return int.TryParse(raw, out var offset) ? Math.Clamp(offset, -840, 840) : _fallbackOffsetMinutes;
    }
}
