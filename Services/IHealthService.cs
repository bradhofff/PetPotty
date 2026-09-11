using PetPotty.Models;

namespace PetPotty.Services;

public interface IHealthService
{
    int AddHealthEvent(int userID, HealthEventInput input);
    bool UpdateHealthEvent(int userID, HealthEventInput input);
    bool DeleteHealthEvent(int userID, int healthEventID);
    HealthEvent? GetHealthEventByID(int userID, int healthEventID);
    List<HealthEvent> GetHealthEvents(int userID, int? petID, DateTime startUtc, DateTime endUtc);
    List<HealthTimelineItem> GetTimeline(
        int userID,
        int? petID,
        DateTime startUtc,
        DateTime endUtc,
        int utcOffsetMinutes,
        string? sourceType,
        bool oldestFirst);
}
