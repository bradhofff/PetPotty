using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services;

public sealed class HouseholdContextService : IHouseholdContextService
{
    public const string SessionKey = "activeHouseholdID";

    private readonly string _connStr;
    private readonly ILogger<HouseholdContextService> _logger;

    public HouseholdContextService(IConfiguration configuration, ILogger<HouseholdContextService> logger)
    {
        _connStr = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        _logger = logger;
    }

    public HouseholdContext? GetActiveHousehold(ISession session, int userID)
    {
        var sessionHouseholdID = session.GetInt32(SessionKey);
        if (sessionHouseholdID.HasValue)
        {
            var context = GetContext(userID, sessionHouseholdID.Value);
            if (context != null)
                return context;

            // The session pointed at a household the user is no longer part
            // of (removed, or stale data) — fall through and pick a safe default.
            _logger.LogWarning(
                "User {UserID} session referenced household {HouseholdID} they are no longer an active member of; falling back",
                userID, sessionHouseholdID.Value);
            session.Remove(SessionKey);
        }

        var households = GetHouseholds(userID);
        if (households.Count == 0)
            return null;

        var fallback = households[0];
        session.SetInt32(SessionKey, fallback.HouseholdID);
        return new HouseholdContext
        {
            HouseholdID = fallback.HouseholdID,
            Name = fallback.Name,
            Role = fallback.Role
        };
    }

    public List<HouseholdSummary> GetHouseholds(int userID)
    {
        var list = new List<HouseholdSummary>();
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.GetHouseholdsForUser", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new HouseholdSummary
            {
                HouseholdID = reader.GetInt32(reader.GetOrdinal("HouseholdID")),
                Name = reader["Name"]?.ToString() ?? string.Empty,
                Role = reader["Role"]?.ToString() ?? string.Empty
            });
        }
        return list;
    }

    public bool SwitchHousehold(ISession session, int userID, int requestedHouseholdID)
    {
        var context = GetContext(userID, requestedHouseholdID);
        if (context == null)
        {
            _logger.LogWarning(
                "Unauthorized household access attempt: user {UserID} requested household {HouseholdID} they are not an active member of",
                userID, requestedHouseholdID);
            return false;
        }

        _logger.LogInformation("User {UserID} switched active household to {HouseholdID}", userID, requestedHouseholdID);
        session.SetInt32(SessionKey, requestedHouseholdID);
        return true;
    }

    private HouseholdContext? GetContext(int userID, int householdID)
    {
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.GetHouseholdContext", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new HouseholdContext
        {
            HouseholdID = reader.GetInt32(reader.GetOrdinal("HouseholdID")),
            Name = reader["Name"]?.ToString() ?? string.Empty,
            Role = reader["Role"]?.ToString() ?? string.Empty
        };
    }

    private SqlConnection OpenConnection()
    {
        var connection = new SqlConnection(_connStr);
        connection.Open();
        return connection;
    }
}
