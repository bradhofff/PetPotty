using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services;

public sealed class HealthService : IHealthService
{
    private readonly string _connectionString;
    private readonly IUserTimeZoneService _timeZone;
    private readonly int _missedAfterMinutes;

    public HealthService(IConfiguration configuration, IUserTimeZoneService timeZone)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        _timeZone = timeZone;
        _missedAfterMinutes = Math.Clamp(
            configuration.GetValue<int?>("MedicationAdherence:MissedAfterMinutes") ?? 720, 1, 10080);
    }

    public int AddHealthEvent(int userID, int householdID, HealthEventInput input)
    {
        if (!HealthEventKinds.All.Contains(input.EventKind, StringComparer.OrdinalIgnoreCase))
            return 0;

        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.AddHealthEvent", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        AddEventParameters(command, input);
        var result = command.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    public bool UpdateHealthEvent(int userID, int householdID, HealthEventInput input)
    {
        if (!HealthEventKinds.All.Contains(input.EventKind, StringComparer.OrdinalIgnoreCase))
            return false;

        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.UpdateHealthEvent", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@HealthEventID", SqlDbType.Int).Value = input.HealthEventID;
        AddEventParameters(command, input);
        return Convert.ToBoolean(command.ExecuteScalar());
    }

    public bool DeleteHealthEvent(int userID, int householdID, int healthEventID)
    {
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.DeleteHealthEvent", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@HealthEventID", SqlDbType.Int).Value = healthEventID;
        return Convert.ToBoolean(command.ExecuteScalar());
    }

    public HealthEvent? GetHealthEventByID(int userID, int householdID, int healthEventID)
    {
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.GetHealthEventByID", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@HealthEventID", SqlDbType.Int).Value = healthEventID;
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapHealthEvent(reader) : null;
    }

    public List<HealthEvent> GetHealthEvents(
        int userID, int householdID, int? petID, DateTime startUtc, DateTime endUtc, string? eventKind)
    {
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.GetHealthEvents", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@PetID", SqlDbType.Int).Value = (object?)petID ?? DBNull.Value;
        command.Parameters.Add("@StartUtc", SqlDbType.DateTime2).Value = startUtc;
        command.Parameters.Add("@EndUtc", SqlDbType.DateTime2).Value = endUtc;
        command.Parameters.Add("@EventKind", SqlDbType.NVarChar, 40).Value = (object?)eventKind ?? DBNull.Value;
        using var reader = command.ExecuteReader();
        var events = new List<HealthEvent>();
        while (reader.Read())
            events.Add(MapHealthEvent(reader));
        return events;
    }

    public List<HealthTimelineItem> GetTimeline(
        int userID,
        int householdID,
        int? petID,
        DateTime startUtc,
        DateTime endUtc,
        int utcOffsetMinutes,
        string? sourceType,
        bool oldestFirst)
    {
        var offset = Math.Clamp(utcOffsetMinutes, -840, 840);
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.GetHealthTimeline", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@PetID", SqlDbType.Int).Value = (object?)petID ?? DBNull.Value;
        command.Parameters.Add("@StartUtc", SqlDbType.DateTime2).Value = startUtc;
        command.Parameters.Add("@EndUtc", SqlDbType.DateTime2).Value = endUtc;

        using var reader = command.ExecuteReader();
        var items = new List<HealthTimelineItem>();
        var nowLocal = _timeZone.ToLocal(DateTime.UtcNow, offset);
        while (reader.Read())
        {
            var thisSourceType = GetString(reader, "SourceType");
            var eventAtUtc = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("EventAtUtc")), DateTimeKind.Utc);
            var status = GetString(reader, "Status");

            if (thisSourceType == "Medication")
            {
                var rawDoseStatus = GetString(reader, "RawDoseStatus");
                var scheduleDateRawOrdinal = reader.GetOrdinal("ScheduleDateRaw");
                var timingOrdinal = reader.GetOrdinal("TimingDoesNotMatter");
                var timingDoesNotMatter = !reader.IsDBNull(timingOrdinal) && reader.GetBoolean(timingOrdinal);
                var scheduleDateLocal = reader.IsDBNull(scheduleDateRawOrdinal)
                    ? nowLocal
                    : reader.GetDateTime(scheduleDateRawOrdinal);
                status = MedicationAdherence.EffectiveStatus(
                    rawDoseStatus, scheduleDateLocal, timingDoesNotMatter, _missedAfterMinutes, nowLocal);
            }

            var item = new HealthTimelineItem
            {
                SourceType = thisSourceType,
                SourceID = reader.GetInt32(reader.GetOrdinal("SourceID")),
                PetID = reader.GetInt32(reader.GetOrdinal("PetID")),
                PetName = GetString(reader, "PetName"),
                EventAtUtc = eventAtUtc,
                Title = GetString(reader, "Title"),
                Summary = GetString(reader, "Summary"),
                Attribution = GetString(reader, "Attribution"),
                Status = status,
                Severity = reader.IsDBNull(reader.GetOrdinal("Severity")) ? null : reader.GetInt32(reader.GetOrdinal("Severity")),
                Url = GetString(reader, "Url")
            };
            item.EventAtLocal = _timeZone.ToLocal(item.EventAtUtc, offset);
            items.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(sourceType) && !sourceType.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            items = items
                .Where(item => item.SourceType.Equals(sourceType, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return oldestFirst
            ? items.OrderBy(item => item.EventAtUtc).ThenBy(item => item.SourceID).ToList()
            : items.OrderByDescending(item => item.EventAtUtc).ThenByDescending(item => item.SourceID).ToList();
    }

    private void AddEventParameters(SqlCommand command, HealthEventInput input)
    {
        var occurredAtUtc = _timeZone.ToUtc(input.OccurredAtLocal, input.UtcOffsetMinutes);
        var endedAtUtc = input.EndedAtLocal.HasValue ? _timeZone.ToUtc(input.EndedAtLocal.Value, input.UtcOffsetMinutes) : (DateTime?)null;
        var recoveredAtUtc = input.RecoveredAtLocal.HasValue ? _timeZone.ToUtc(input.RecoveredAtLocal.Value, input.UtcOffsetMinutes) : (DateTime?)null;

        command.Parameters.Add("@PetID", SqlDbType.Int).Value = input.PetID;
        command.Parameters.Add("@EventKind", SqlDbType.NVarChar, 40).Value = input.EventKind.Trim();
        command.Parameters.Add("@EventType", SqlDbType.NVarChar, 200).Value = input.EventType.Trim();
        command.Parameters.Add("@OccurredAtUtc", SqlDbType.DateTime2).Value = occurredAtUtc;
        command.Parameters.Add("@EndedAtUtc", SqlDbType.DateTime2).Value = (object?)endedAtUtc ?? DBNull.Value;
        command.Parameters.Add("@Severity", SqlDbType.TinyInt).Value = (object?)input.Severity ?? DBNull.Value;
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 4000).Value = Clean(input.Description);
        command.Parameters.Add("@PossibleTrigger", SqlDbType.NVarChar, 1000).Value = Clean(input.PossibleTrigger);
        command.Parameters.Add("@AppetiteStatus", SqlDbType.NVarChar, 60).Value = Clean(input.AppetiteStatus);
        command.Parameters.Add("@DrinkingStatus", SqlDbType.NVarChar, 60).Value = Clean(input.DrinkingStatus);
        command.Parameters.Add("@RelatedMedicationID", SqlDbType.Int).Value = (object?)input.RelatedMedicationID ?? DBNull.Value;
        command.Parameters.Add("@RecoveryStatus", SqlDbType.NVarChar, 60).Value = Clean(input.RecoveryStatus);
        command.Parameters.Add("@RecoveredAtUtc", SqlDbType.DateTime2).Value = (object?)recoveredAtUtc ?? DBNull.Value;
        command.Parameters.Add("@VeterinarianContacted", SqlDbType.Bit).Value = input.VeterinarianContacted;
    }

    private static HealthEvent MapHealthEvent(SqlDataReader reader) => new()
    {
        HealthEventID = reader.GetInt32(reader.GetOrdinal("HealthEventID")),
        PetID = reader.GetInt32(reader.GetOrdinal("PetID")),
        PetName = GetString(reader, "PetName"),
        EventKind = GetString(reader, "EventKind"),
        EventType = GetString(reader, "EventType"),
        OccurredAtUtc = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("OccurredAtUtc")), DateTimeKind.Utc),
        EndedAtUtc = GetNullableUtc(reader, "EndedAtUtc"),
        Severity = reader.IsDBNull(reader.GetOrdinal("Severity")) ? null : reader.GetByte(reader.GetOrdinal("Severity")),
        Description = GetString(reader, "Description"),
        PossibleTrigger = GetString(reader, "PossibleTrigger"),
        AppetiteStatus = GetString(reader, "AppetiteStatus"),
        DrinkingStatus = GetString(reader, "DrinkingStatus"),
        RelatedMedicationID = reader.IsDBNull(reader.GetOrdinal("RelatedMedicationID")) ? null : reader.GetInt32(reader.GetOrdinal("RelatedMedicationID")),
        RelatedMedicationName = GetString(reader, "RelatedMedicationName"),
        RecoveryStatus = GetString(reader, "RecoveryStatus"),
        RecoveredAtUtc = GetNullableUtc(reader, "RecoveredAtUtc"),
        VeterinarianContacted = reader.GetBoolean(reader.GetOrdinal("VeterinarianContacted")),
        CreatedByUserID = reader.GetInt32(reader.GetOrdinal("CreatedByUserID")),
        CreatedByName = GetString(reader, "CreatedByName"),
        CreatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")), DateTimeKind.Utc)
    };

    private SqlConnection OpenConnection()
    {
        var connection = new SqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static DateTime? GetNullableUtc(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
    }

    private static string GetString(SqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? string.Empty : reader[name].ToString() ?? string.Empty;

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
