using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services;

public sealed class HealthService : IHealthService
{
    private readonly string _connectionString;
    private readonly IUserTimeZoneService _timeZone;
    private readonly IHouseholdContextService _householdContext;
    private readonly IHouseholdAuthorizationService _authorization;
    private readonly int _missedAfterMinutes;

    public HealthService(
        IConfiguration configuration,
        IUserTimeZoneService timeZone,
        IHouseholdContextService householdContext,
        IHouseholdAuthorizationService authorization)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        _timeZone = timeZone;
        _householdContext = householdContext;
        _authorization = authorization;
        _missedAfterMinutes = Math.Clamp(
            configuration.GetValue<int?>("MedicationAdherence:MissedAfterMinutes") ?? 720,
            1,
            10080);
    }

    public int AddHealthEvent(int userID, HealthEventInput input)
    {
        if (!HealthEventKinds.All.Contains(input.EventKind, StringComparer.OrdinalIgnoreCase))
            return 0;
        var householdID = GetAuthorizedHouseholdID(userID, HouseholdPermission.RecordCare);
        if (!householdID.HasValue)
            return 0;

        var occurredAtUtc = _timeZone.ToUtc(input.OccurredAtLocal, input.UtcOffsetMinutes);
        var endedAtUtc = input.EndedAtLocal.HasValue
            ? _timeZone.ToUtc(input.EndedAtLocal.Value, input.UtcOffsetMinutes)
            : (DateTime?)null;
        var recoveredAtUtc = input.RecoveredAtLocal.HasValue
            ? _timeZone.ToUtc(input.RecoveredAtLocal.Value, input.UtcOffsetMinutes)
            : (DateTime?)null;

        using var connection = OpenConnection();
        const string sql = """
            INSERT dbo.HealthEvents
                (PetID, EventKind, EventType, OccurredAtUtc, EndedAtUtc, Severity,
                 Description, PossibleTrigger, AppetiteStatus, DrinkingStatus,
                 RelatedMedicationID, RecoveryStatus, RecoveredAtUtc,
                 VeterinarianContacted, CreatedByUserID, CreatedAtUtc, UpdatedAtUtc)
            OUTPUT INSERTED.HealthEventID
            SELECT @PetID, @EventKind, @EventType, @OccurredAtUtc, @EndedAtUtc, @Severity,
                   @Description, @PossibleTrigger, @AppetiteStatus, @DrinkingStatus,
                   @RelatedMedicationID, @RecoveryStatus, @RecoveredAtUtc,
                   @VeterinarianContacted, @UserID, SYSUTCDATETIME(), SYSUTCDATETIME()
            WHERE EXISTS
                (SELECT 1 FROM dbo.Pets p
                 INNER JOIN dbo.HouseholdMembers hm
                   ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
                 WHERE p.petID = @PetID AND p.HouseholdID = @HouseholdID)
              AND
                (@RelatedMedicationID IS NULL OR EXISTS
                    (SELECT 1 FROM dbo.Medications
                     WHERE medID = @RelatedMedicationID AND petID = @PetID));
            """;
        using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@PetID", SqlDbType.Int).Value = input.PetID;
        command.Parameters.Add("@EventKind", SqlDbType.NVarChar, 20).Value = input.EventKind.Trim();
        command.Parameters.Add("@EventType", SqlDbType.NVarChar, 100).Value = input.EventType.Trim();
        command.Parameters.Add("@OccurredAtUtc", SqlDbType.DateTime2).Value = occurredAtUtc;
        command.Parameters.Add("@EndedAtUtc", SqlDbType.DateTime2).Value = (object?)endedAtUtc ?? DBNull.Value;
        command.Parameters.Add("@Severity", SqlDbType.TinyInt).Value = (object?)input.Severity ?? DBNull.Value;
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 2000).Value = Clean(input.Description);
        command.Parameters.Add("@PossibleTrigger", SqlDbType.NVarChar, 500).Value = Clean(input.PossibleTrigger);
        command.Parameters.Add("@AppetiteStatus", SqlDbType.NVarChar, 30).Value = Clean(input.AppetiteStatus);
        command.Parameters.Add("@DrinkingStatus", SqlDbType.NVarChar, 30).Value = Clean(input.DrinkingStatus);
        command.Parameters.Add("@RelatedMedicationID", SqlDbType.Int).Value =
            (object?)input.RelatedMedicationID ?? DBNull.Value;
        command.Parameters.Add("@RecoveryStatus", SqlDbType.NVarChar, 30).Value = Clean(input.RecoveryStatus);
        command.Parameters.Add("@RecoveredAtUtc", SqlDbType.DateTime2).Value =
            (object?)recoveredAtUtc ?? DBNull.Value;
        command.Parameters.Add("@VeterinarianContacted", SqlDbType.Bit).Value = input.VeterinarianContacted;
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID.Value;
        var result = command.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    public bool UpdateHealthEvent(int userID, HealthEventInput input)
    {
        if (!HealthEventKinds.All.Contains(input.EventKind, StringComparer.OrdinalIgnoreCase))
            return false;
        var householdID = GetAuthorizedHouseholdID(userID, HouseholdPermission.RecordCare);
        if (!householdID.HasValue)
            return false;

        var occurredAtUtc = _timeZone.ToUtc(input.OccurredAtLocal, input.UtcOffsetMinutes);
        var endedAtUtc = input.EndedAtLocal.HasValue
            ? _timeZone.ToUtc(input.EndedAtLocal.Value, input.UtcOffsetMinutes)
            : (DateTime?)null;
        var recoveredAtUtc = input.RecoveredAtLocal.HasValue
            ? _timeZone.ToUtc(input.RecoveredAtLocal.Value, input.UtcOffsetMinutes)
            : (DateTime?)null;

        using var connection = OpenConnection();
        // The pet may be changing as part of this edit, so ownership is checked twice:
        // the row's CURRENT pet (via the join) and the NEW target pet (via the EXISTS clause).
        const string sql = """
            UPDATE h
            SET PetID = @PetID,
                EventKind = @EventKind,
                EventType = @EventType,
                OccurredAtUtc = @OccurredAtUtc,
                EndedAtUtc = @EndedAtUtc,
                Severity = @Severity,
                Description = @Description,
                PossibleTrigger = @PossibleTrigger,
                AppetiteStatus = @AppetiteStatus,
                DrinkingStatus = @DrinkingStatus,
                RelatedMedicationID = @RelatedMedicationID,
                RecoveryStatus = @RecoveryStatus,
                RecoveredAtUtc = @RecoveredAtUtc,
                VeterinarianContacted = @VeterinarianContacted,
                UpdatedAtUtc = SYSUTCDATETIME()
            FROM dbo.HealthEvents h
            INNER JOIN dbo.Pets currentPet ON currentPet.petID = h.PetID
            INNER JOIN dbo.HouseholdMembers hm
              ON hm.HouseholdID = currentPet.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
            WHERE h.HealthEventID = @HealthEventID
              AND h.IsDeleted = 0
              AND currentPet.HouseholdID = @HouseholdID
              AND EXISTS (SELECT 1 FROM dbo.Pets WHERE petID = @PetID AND HouseholdID = @HouseholdID)
              AND (@RelatedMedicationID IS NULL OR EXISTS
                  (SELECT 1 FROM dbo.Medications WHERE medID = @RelatedMedicationID AND petID = @PetID));
            """;
        using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@HealthEventID", SqlDbType.Int).Value = input.HealthEventID;
        command.Parameters.Add("@PetID", SqlDbType.Int).Value = input.PetID;
        command.Parameters.Add("@EventKind", SqlDbType.NVarChar, 20).Value = input.EventKind.Trim();
        command.Parameters.Add("@EventType", SqlDbType.NVarChar, 100).Value = input.EventType.Trim();
        command.Parameters.Add("@OccurredAtUtc", SqlDbType.DateTime2).Value = occurredAtUtc;
        command.Parameters.Add("@EndedAtUtc", SqlDbType.DateTime2).Value = (object?)endedAtUtc ?? DBNull.Value;
        command.Parameters.Add("@Severity", SqlDbType.TinyInt).Value = (object?)input.Severity ?? DBNull.Value;
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 2000).Value = Clean(input.Description);
        command.Parameters.Add("@PossibleTrigger", SqlDbType.NVarChar, 500).Value = Clean(input.PossibleTrigger);
        command.Parameters.Add("@AppetiteStatus", SqlDbType.NVarChar, 30).Value = Clean(input.AppetiteStatus);
        command.Parameters.Add("@DrinkingStatus", SqlDbType.NVarChar, 30).Value = Clean(input.DrinkingStatus);
        command.Parameters.Add("@RelatedMedicationID", SqlDbType.Int).Value =
            (object?)input.RelatedMedicationID ?? DBNull.Value;
        command.Parameters.Add("@RecoveryStatus", SqlDbType.NVarChar, 30).Value = Clean(input.RecoveryStatus);
        command.Parameters.Add("@RecoveredAtUtc", SqlDbType.DateTime2).Value =
            (object?)recoveredAtUtc ?? DBNull.Value;
        command.Parameters.Add("@VeterinarianContacted", SqlDbType.Bit).Value = input.VeterinarianContacted;
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID.Value;
        return command.ExecuteNonQuery() == 1;
    }

    public HealthEvent? GetHealthEventByID(int userID, int healthEventID)
    {
        var householdID = GetAuthorizedHouseholdID(userID, HouseholdPermission.ViewPets);
        if (!householdID.HasValue)
            return null;
        using var connection = OpenConnection();
        const string sql = """
            SELECT h.HealthEventID, h.PetID, p.name AS PetName, h.EventKind,
                   h.EventType, h.OccurredAtUtc, h.EndedAtUtc, h.Severity,
                   h.Description, h.PossibleTrigger, h.AppetiteStatus,
                   h.DrinkingStatus, h.RelatedMedicationID,
                   ISNULL(m.medicationName, N'') AS RelatedMedicationName,
                   h.RecoveryStatus, h.RecoveredAtUtc, h.VeterinarianContacted,
                   h.CreatedByUserID, u.name AS CreatedByName, h.CreatedAtUtc
            FROM dbo.HealthEvents h
            INNER JOIN dbo.Pets p ON p.petID = h.PetID
            LEFT JOIN dbo.Users u ON u.userID = h.CreatedByUserID
            INNER JOIN dbo.HouseholdMembers hm
              ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
            LEFT JOIN dbo.Medications m ON m.medID = h.RelatedMedicationID
            WHERE h.HealthEventID = @HealthEventID
              AND p.HouseholdID = @HouseholdID
              AND h.IsDeleted = 0;
            """;
        using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@HealthEventID", SqlDbType.Int).Value = healthEventID;
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID.Value;
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapHealthEvent(reader) : null;
    }

    public bool DeleteHealthEvent(int userID, int healthEventID)
    {
        var householdID = GetAuthorizedHouseholdID(userID, HouseholdPermission.RecordCare);
        if (!householdID.HasValue)
            return false;
        using var connection = OpenConnection();
        const string sql = """
            UPDATE h
            SET IsDeleted = 1,
                DeletedAtUtc = SYSUTCDATETIME(),
                DeletedByUserID = @UserID,
                UpdatedAtUtc = SYSUTCDATETIME()
            FROM dbo.HealthEvents h
            INNER JOIN dbo.Pets p ON p.petID = h.PetID
            INNER JOIN dbo.HouseholdMembers hm
              ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
            WHERE h.HealthEventID = @HealthEventID
              AND p.HouseholdID = @HouseholdID
              AND h.IsDeleted = 0;
            """;
        using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@HealthEventID", SqlDbType.Int).Value = healthEventID;
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID.Value;
        return command.ExecuteNonQuery() == 1;
    }

    public List<HealthEvent> GetHealthEvents(
        int userID,
        int? petID,
        DateTime startUtc,
        DateTime endUtc)
    {
        var householdID = GetAuthorizedHouseholdID(userID, HouseholdPermission.ViewPets);
        if (!householdID.HasValue)
            return [];
        using var connection = OpenConnection();
        const string sql = """
            SELECT h.HealthEventID, h.PetID, p.name AS PetName, h.EventKind,
                   h.EventType, h.OccurredAtUtc, h.EndedAtUtc, h.Severity,
                   h.Description, h.PossibleTrigger, h.AppetiteStatus,
                   h.DrinkingStatus, h.RelatedMedicationID,
                   ISNULL(m.medicationName, N'') AS RelatedMedicationName,
                   h.RecoveryStatus, h.RecoveredAtUtc, h.VeterinarianContacted,
                   h.CreatedByUserID, u.name AS CreatedByName, h.CreatedAtUtc
            FROM dbo.HealthEvents h
            INNER JOIN dbo.Pets p ON p.petID = h.PetID
            LEFT JOIN dbo.Users u ON u.userID = h.CreatedByUserID
            INNER JOIN dbo.HouseholdMembers hm
              ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
            LEFT JOIN dbo.Medications m ON m.medID = h.RelatedMedicationID
            WHERE p.HouseholdID = @HouseholdID
              AND h.IsDeleted = 0
              AND (@PetID IS NULL OR h.PetID = @PetID)
              AND h.OccurredAtUtc >= @StartUtc
              AND h.OccurredAtUtc < @EndUtc
            ORDER BY h.OccurredAtUtc DESC, h.HealthEventID DESC;
            """;
        using var command = new SqlCommand(sql, connection);
        AddTimelineRangeParameters(command, userID, householdID.Value, petID, startUtc, endUtc);
        using var reader = command.ExecuteReader();
        var events = new List<HealthEvent>();
        while (reader.Read())
            events.Add(MapHealthEvent(reader));
        return events;
    }

    public List<HealthTimelineItem> GetTimeline(
        int userID,
        int? petID,
        DateTime startUtc,
        DateTime endUtc,
        int utcOffsetMinutes,
        string? sourceType,
        bool oldestFirst)
    {
        var householdID = GetAuthorizedHouseholdID(userID, HouseholdPermission.ViewPets);
        if (!householdID.HasValue)
            return [];
        using var connection = OpenConnection();
        const string sql = """
            SELECT SourceType, SourceID, PetID, PetName, EventAtUtc, Title, Summary,
                   Attribution, Status, Severity, Url
            FROM
            (
                SELECT h.EventKind AS SourceType,
                       h.HealthEventID AS SourceID,
                       h.PetID,
                       CONVERT(nvarchar(100), p.name) AS PetName,
                       h.OccurredAtUtc AS EventAtUtc,
                       h.EventType AS Title,
                       h.Description AS Summary,
                       CONVERT(nvarchar(100), u.name) AS Attribution,
                       h.RecoveryStatus AS Status,
                       CONVERT(int, h.Severity) AS Severity,
                       CONCAT(N'/Health?petID=', h.PetID, N'#health-event-', h.HealthEventID) AS Url
                FROM dbo.HealthEvents h
                INNER JOIN dbo.Pets p ON p.petID = h.PetID
                LEFT JOIN dbo.Users u ON u.userID = h.CreatedByUserID
                INNER JOIN dbo.HouseholdMembers hm
                  ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
                WHERE p.HouseholdID = @HouseholdID AND h.IsDeleted = 0
                  AND (@PetID IS NULL OR h.PetID = @PetID)

                UNION ALL

                SELECT N'Medication', ms.scheduleID, m.petID, CONVERT(nvarchar(100), p.name),
                       COALESCE(ms.AdministeredAtUtc,
                           DATEADD(MINUTE, @UtcOffsetMinutes, CONVERT(datetime2(0), ms.scheduleDate))),
                       CONVERT(nvarchar(100), m.medicationName),
                       CONVERT(nvarchar(2000), CONCAT(ISNULL(m.dosage, N''),
                           CASE WHEN NULLIF(ms.AdministrationNotes, N'') IS NULL THEN N''
                                ELSE CONCAT(N' — ', ms.AdministrationNotes) END)),
                       CONVERT(nvarchar(100), u.name),
                       CASE
                           WHEN ms.DoseStatus <> N'Due' THEN ms.DoseStatus
                           WHEN m.TimingDoesNotMatter = 1 AND @NowUtc >= DATEADD(MINUTE, @MissedAfterMinutes,
                               DATEADD(MINUTE, @UtcOffsetMinutes,
                                   DATEADD(DAY, 1, CONVERT(datetime2(0), CONVERT(date, ms.scheduleDate))))) THEN N'Missed'
                           WHEN m.TimingDoesNotMatter = 0 AND @NowUtc >= DATEADD(MINUTE, @MissedAfterMinutes,
                               DATEADD(MINUTE, @UtcOffsetMinutes, CONVERT(datetime2(0), ms.scheduleDate))) THEN N'Missed'
                           ELSE N'Due'
                       END,
                       NULL,
                       CONCAT(N'/Medications?petID=', m.petID, N'&editMedID=', m.medID)
                FROM dbo.MedicationSchedule ms
                INNER JOIN dbo.Medications m ON m.medID = ms.medID
                INNER JOIN dbo.Pets p ON p.petID = m.petID
                LEFT JOIN dbo.Users u ON u.userID = ms.RecordedByUserID
                INNER JOIN dbo.HouseholdMembers medicationMember
                  ON medicationMember.HouseholdID = p.HouseholdID
                 AND medicationMember.UserID = @UserID AND medicationMember.Status = N'Active'
                WHERE p.HouseholdID = @HouseholdID AND (@PetID IS NULL OR m.petID = @PetID)

                UNION ALL

                SELECT N'VetVisit', v.VetVisitID, v.PetID, CONVERT(nvarchar(100), p.name),
                       DATEADD(MINUTE, @UtcOffsetMinutes,
                           DATEADD(SECOND,
                               DATEDIFF(SECOND, CONVERT(time(0), '00:00'),
                                   COALESCE(v.VisitTime, CONVERT(time(0), '09:00'))),
                               CONVERT(datetime2(0), CONVERT(date, v.VisitDate)))),
                       CONVERT(nvarchar(100), CONCAT(N'Vet visit: ', v.VisitReason)),
                       CONVERT(nvarchar(2000), CONCAT(v.ClinicName,
                           CASE WHEN NULLIF(v.VisitSummary, N'') IS NULL THEN N''
                                ELSE CONCAT(N' — ', v.VisitSummary) END)),
                       CONVERT(nvarchar(100), visitUser.name), CONVERT(nvarchar(30), v.Status), NULL,
                       CONCAT(N'/VetVisits?petID=', v.PetID, N'&vetVisitID=', v.VetVisitID)
                FROM dbo.VetVisits v
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                LEFT JOIN dbo.Users visitUser ON visitUser.userID = v.CreatedByUserID
                INNER JOIN dbo.HouseholdMembers visitMember
                  ON visitMember.HouseholdID = p.HouseholdID
                 AND visitMember.UserID = @UserID AND visitMember.Status = N'Active'
                WHERE p.HouseholdID = @HouseholdID AND v.IsDeleted = 0
                  AND (@PetID IS NULL OR v.PetID = @PetID)
            ) timeline
            WHERE EventAtUtc >= @StartUtc AND EventAtUtc < @EndUtc
            ORDER BY EventAtUtc, SourceType, SourceID;
            """;
        using var command = new SqlCommand(sql, connection);
        AddTimelineRangeParameters(command, userID, householdID.Value, petID, startUtc, endUtc);
        command.Parameters.Add("@UtcOffsetMinutes", SqlDbType.Int).Value =
            Math.Clamp(utcOffsetMinutes, -840, 840);
        command.Parameters.Add("@NowUtc", SqlDbType.DateTime2).Value = DateTime.UtcNow;
        command.Parameters.Add("@MissedAfterMinutes", SqlDbType.Int).Value = _missedAfterMinutes;

        using var reader = command.ExecuteReader();
        var items = new List<HealthTimelineItem>();
        while (reader.Read())
        {
            var item = new HealthTimelineItem
            {
                SourceType = GetString(reader, "SourceType"),
                SourceID = reader.GetInt32(reader.GetOrdinal("SourceID")),
                PetID = reader.GetInt32(reader.GetOrdinal("PetID")),
                PetName = GetString(reader, "PetName"),
                EventAtUtc = DateTime.SpecifyKind(
                    reader.GetDateTime(reader.GetOrdinal("EventAtUtc")),
                    DateTimeKind.Utc),
                Title = GetString(reader, "Title"),
                Summary = GetString(reader, "Summary"),
                Attribution = GetString(reader, "Attribution"),
                Status = GetString(reader, "Status"),
                Severity = reader.IsDBNull(reader.GetOrdinal("Severity"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("Severity")),
                Url = GetString(reader, "Url")
            };
            item.EventAtLocal = _timeZone.ToLocal(item.EventAtUtc, utcOffsetMinutes);
            items.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(sourceType)
            && !sourceType.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            items = items
                .Where(item => item.SourceType.Equals(sourceType, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return oldestFirst
            ? items.OrderBy(item => item.EventAtUtc).ThenBy(item => item.SourceID).ToList()
            : items.OrderByDescending(item => item.EventAtUtc).ThenByDescending(item => item.SourceID).ToList();
    }

    private SqlConnection OpenConnection()
    {
        var connection = new SqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void AddTimelineRangeParameters(
        SqlCommand command,
        int userID,
        int householdID,
        int? petID,
        DateTime startUtc,
        DateTime endUtc)
    {
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@PetID", SqlDbType.Int).Value = (object?)petID ?? DBNull.Value;
        command.Parameters.Add("@StartUtc", SqlDbType.DateTime2).Value = startUtc;
        command.Parameters.Add("@EndUtc", SqlDbType.DateTime2).Value = endUtc;
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
        RelatedMedicationID = reader.IsDBNull(reader.GetOrdinal("RelatedMedicationID"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("RelatedMedicationID")),
        RelatedMedicationName = GetString(reader, "RelatedMedicationName"),
        RecoveryStatus = GetString(reader, "RecoveryStatus"),
        RecoveredAtUtc = GetNullableUtc(reader, "RecoveredAtUtc"),
        VeterinarianContacted = reader.GetBoolean(reader.GetOrdinal("VeterinarianContacted")),
        CreatedByUserID = reader.GetInt32(reader.GetOrdinal("CreatedByUserID")),
        CreatedByName = GetString(reader, "CreatedByName"),
        CreatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")), DateTimeKind.Utc)
    };

    private static DateTime? GetNullableUtc(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
    }

    private static string GetString(SqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? string.Empty : reader[name].ToString() ?? string.Empty;

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private int? GetAuthorizedHouseholdID(int userID, HouseholdPermission permission)
    {
        var household = _householdContext.GetActiveHousehold(userID);
        return household != null
            && _authorization.HasPermission(userID, household.HouseholdID, permission)
            ? household.HouseholdID
            : null;
    }
}
