namespace PetPotty.Services;

public interface IUserTimeZoneService
{
    int GetUtcOffsetMinutes(HttpRequest request);
    DateTime ToUtc(DateTime localTime, int utcOffsetMinutes);
    DateTime ToLocal(DateTime utcTime, int utcOffsetMinutes);
}

