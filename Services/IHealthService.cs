using PetPotty.Models;

namespace PetPotty.Services;

public interface IHealthService
{
    int AddHealthEvent(int userID, int householdID, HealthEventInput input);
    bool UpdateHealthEvent(int userID, int householdID, HealthEventInput input);
    bool DeleteHealthEvent(int userID, int householdID, int healthEventID);
    HealthEvent? GetHealthEventByID(int userID, int householdID, int healthEventID);
    List<HealthEvent> GetHealthEvents(
        int userID, int householdID, int? petID, DateTime startUtc, DateTime endUtc, string? eventKind);
    List<HealthTimelineItem> GetTimeline(
        int userID,
        int householdID,
        int? petID,
        DateTime startUtc,
        DateTime endUtc,
        int utcOffsetMinutes,
        string? sourceType,
        bool oldestFirst);
}
