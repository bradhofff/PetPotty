using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services;

public sealed class HouseholdService : IHouseholdService
{
    private readonly string _connStr;
    private readonly ILogger<HouseholdService> _logger;

    public HouseholdService(IConfiguration configuration, ILogger<HouseholdService> logger)
    {
        _connStr = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        _logger = logger;
    }

    public int EnsureDefaultHousehold(int userID, string userName)
    {
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.CreateDefaultHouseholdForUser", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@UserName", SqlDbType.NVarChar, 100).Value = userName;
        var result = command.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    public List<HouseholdMember> GetMembers(int userID, int householdID)
    {
        var members = new List<HouseholdMember>();
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.GetHouseholdMembers", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            members.Add(new HouseholdMember
            {
                UserID = reader.GetInt32(reader.GetOrdinal("UserID")),
                Name = reader["Name"]?.ToString() ?? string.Empty,
                Email = reader["Email"]?.ToString() ?? string.Empty,
                Role = reader["Role"]?.ToString() ?? string.Empty,
                JoinedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("JoinedAtUtc")), DateTimeKind.Utc)
            });
        }
        return members;
    }

    public bool RenameHousehold(int userID, int householdID, string newName)
    {
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.RenameHousehold", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@NewName", SqlDbType.NVarChar, 150).Value = newName;
        var succeeded = Convert.ToBoolean(command.ExecuteScalar());
        if (succeeded)
            _logger.LogInformation("Household {HouseholdID} renamed by user {UserID}", householdID, userID);
        return succeeded;
    }

    public bool UpdateMemberRole(int userID, int householdID, int targetUserID, string newRole)
    {
        if (!HouseholdRoles.Assignable.Contains(newRole))
            return false;

        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.UpdateHouseholdMemberRole", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@TargetUserID", SqlDbType.Int).Value = targetUserID;
        command.Parameters.Add("@NewRole", SqlDbType.NVarChar, 40).Value = newRole;
        var succeeded = Convert.ToBoolean(command.ExecuteScalar());
        if (succeeded)
        {
            _logger.LogInformation(
                "Household {HouseholdID} member {TargetUserID} role changed to {NewRole} by user {UserID}",
                householdID, targetUserID, newRole, userID);
        }
        return succeeded;
    }

    public bool RemoveMember(int userID, int householdID, int targetUserID)
    {
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.RemoveHouseholdMember", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@TargetUserID", SqlDbType.Int).Value = targetUserID;
        var succeeded = Convert.ToBoolean(command.ExecuteScalar());
        if (succeeded)
            _logger.LogInformation("Household {HouseholdID} member {TargetUserID} removed by user {UserID}", householdID, targetUserID, userID);
        return succeeded;
    }

    private SqlConnection OpenConnection()
    {
        var connection = new SqlConnection(_connStr);
        connection.Open();
        return connection;
    }
}
