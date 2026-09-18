using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services;

public sealed class HouseholdAuthorizationService : IHouseholdAuthorizationService
{
    private readonly string _connectionString;
    private readonly ILogger<HouseholdAuthorizationService> _logger;

    public HouseholdAuthorizationService(
        IConfiguration configuration,
        ILogger<HouseholdAuthorizationService> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        _logger = logger;
    }

    public HouseholdRole? GetRole(int userID, int householdID)
    {
        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            SELECT Role
            FROM dbo.HouseholdMembers
            WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active';
            """, connection);
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        connection.Open();
        var value = command.ExecuteScalar()?.ToString();
        return HouseholdAccessRules.TryParseRole(value, out var role) ? role : null;
    }

    public bool HasPermission(int userID, int householdID, HouseholdPermission permission)
    {
        var role = GetRole(userID, householdID);
        var allowed = role.HasValue && HouseholdAccessRules.HasPermission(role.Value, permission);
        if (!allowed)
        {
            _logger.LogWarning(
                "Unauthorized household access attempt by user {UserID} for household {HouseholdID} and permission {Permission}",
                userID,
                householdID,
                permission);
        }
        return allowed;
    }
}
