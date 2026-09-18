using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services;

public sealed class HouseholdContextService : IHouseholdContextService
{
    private const string ActiveHouseholdSessionKey = "activeHouseholdID";
    private readonly string _connectionString;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HouseholdContextService(IConfiguration configuration, IHttpContextAccessor httpContextAccessor)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        _httpContextAccessor = httpContextAccessor;
    }

    public HouseholdContext? GetActiveHousehold(int userID)
    {
        var session = _httpContextAccessor.HttpContext?.Session;
        var requestedID = session?.GetInt32(ActiveHouseholdSessionKey);

        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            SELECT TOP (1) h.HouseholdID, h.PublicID, h.Name, hm.Role
            FROM dbo.HouseholdMembers hm
            INNER JOIN dbo.Households h ON h.HouseholdID = hm.HouseholdID
            WHERE hm.UserID = @UserID
              AND hm.Status = N'Active'
              AND (@HouseholdID IS NULL OR hm.HouseholdID = @HouseholdID)
            ORDER BY CASE hm.Role WHEN N'Owner' THEN 0 WHEN N'Member' THEN 1 ELSE 2 END,
                     hm.JoinedAtUtc, h.HouseholdID;
            """, connection);
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = (object?)requestedID ?? DBNull.Value;
        connection.Open();
        using var reader = command.ExecuteReader();

        if (!reader.Read() && requestedID.HasValue)
        {
            reader.Close();
            command.Parameters["@HouseholdID"].Value = DBNull.Value;
            using var fallbackReader = command.ExecuteReader();
            if (!fallbackReader.Read())
            {
                session?.Remove(ActiveHouseholdSessionKey);
                return null;
            }

            var fallback = Map(fallbackReader);
            session?.SetInt32(ActiveHouseholdSessionKey, fallback.HouseholdID);
            return fallback;
        }

        if (!reader.HasRows)
        {
            session?.Remove(ActiveHouseholdSessionKey);
            return null;
        }

        var result = Map(reader);
        session?.SetInt32(ActiveHouseholdSessionKey, result.HouseholdID);
        return result;
    }

    public bool TrySetActiveHousehold(int userID, Guid publicID)
    {
        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            SELECT h.HouseholdID
            FROM dbo.Households h
            INNER JOIN dbo.HouseholdMembers hm ON hm.HouseholdID = h.HouseholdID
            WHERE h.PublicID = @PublicID AND hm.UserID = @UserID AND hm.Status = N'Active';
            """, connection);
        command.Parameters.Add("@PublicID", SqlDbType.UniqueIdentifier).Value = publicID;
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        connection.Open();
        var value = command.ExecuteScalar();
        return value != null && TrySetSession(Convert.ToInt32(value));
    }

    public bool TrySetActiveHousehold(int userID, int householdID)
    {
        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            SELECT 1
            FROM dbo.HouseholdMembers
            WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active';
            """, connection);
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        connection.Open();
        return command.ExecuteScalar() != null && TrySetSession(householdID);
    }

    public void ClearActiveHousehold() =>
        _httpContextAccessor.HttpContext?.Session.Remove(ActiveHouseholdSessionKey);

    private bool TrySetSession(int householdID)
    {
        var session = _httpContextAccessor.HttpContext?.Session;
        if (session == null)
            return false;
        session.SetInt32(ActiveHouseholdSessionKey, householdID);
        return true;
    }

    private static HouseholdContext Map(SqlDataReader reader)
    {
        var roleValue = reader.GetString(reader.GetOrdinal("Role"));
        if (!HouseholdAccessRules.TryParseRole(roleValue, out var role))
            throw new InvalidOperationException($"Unsupported household role '{roleValue}'.");

        return new HouseholdContext
        {
            HouseholdID = reader.GetInt32(reader.GetOrdinal("HouseholdID")),
            PublicID = reader.GetGuid(reader.GetOrdinal("PublicID")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Role = role
        };
    }
}

