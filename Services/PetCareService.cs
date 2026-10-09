using Microsoft.Data.SqlClient;
using System.Data;
using PetPotty.Models;
namespace PetPotty.Services;

public sealed class PetCareService(IConfiguration configuration, IHouseholdContextService context,
    IHouseholdAuthorizationService authorization) : IPetCareService
{
    private readonly string connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing database configuration.");
    public PetCareProfile? Get(int userID, int petID)
    {
        var household = context.GetActiveHousehold(userID);
        if (household == null || !authorization.HasPermission(userID, household.HouseholdID, HouseholdPermission.ViewPets)) return null;
        using var connection = new SqlConnection(connectionString);
        using var command = new SqlCommand("""
            SELECT c.* FROM dbo.PetCareProfiles c
            INNER JOIN dbo.Pets p ON p.petID = c.PetID
            INNER JOIN dbo.HouseholdMembers hm ON hm.HouseholdID = p.HouseholdID
                AND hm.UserID = @UserID AND hm.Status = N'Active'
            WHERE p.petID = @PetID AND p.HouseholdID = @HouseholdID;
            """, connection);
        Scope(command, userID, petID, household.HouseholdID);
        connection.Open();
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var result = new PetCareProfile { UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")) };
        foreach (var field in PetCareProfile.Fields)
            result.Values[field.Name] = reader[field.Name] as string;
        return result;
    }

    public bool SaveSection(int userID, int petID, string section, IReadOnlyDictionary<string, string?> values)
    {
        var fields = PetCareProfile.Fields.Where(f => f.Section == section).ToArray();
        if (fields.Length == 0 || fields.Any(f => values.GetValueOrDefault(f.Name)?.Length > f.Limit))
            throw new ArgumentException("Invalid care information.");
        var household = context.GetActiveHousehold(userID);
        if (household == null || !authorization.HasPermission(userID, household.HouseholdID, HouseholdPermission.ManageCarePlans)) return false;
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        // Lock and recheck membership/role in the same transaction as the section update.
        using var ownership = new SqlCommand("""
            SELECT 1 FROM dbo.Pets p WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN dbo.HouseholdMembers hm WITH (UPDLOCK, HOLDLOCK)
                ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID
                AND hm.Status = N'Active' AND hm.Role IN (N'Owner', N'Member')
            WHERE p.petID = @PetID AND p.HouseholdID = @HouseholdID;
            """, connection, transaction);
        Scope(ownership, userID, petID, household.HouseholdID);
        if (ownership.ExecuteScalar() == null) return false;
        // Column names come only from the fixed field catalog, never request data.
        using var command = new SqlCommand($"""
            IF NOT EXISTS (SELECT 1 FROM dbo.PetCareProfiles WITH (UPDLOCK, HOLDLOCK) WHERE PetID = @PetID)
                INSERT dbo.PetCareProfiles (PetID, UpdatedByUserID) VALUES (@PetID, @UserID);
            UPDATE dbo.PetCareProfiles SET {string.Join(", ", fields.Select(f => $"[{f.Name}] = @{f.Name}"))},
                UpdatedAtUtc = SYSUTCDATETIME(), UpdatedByUserID = @UserID WHERE PetID = @PetID;
            """, connection, transaction);
        command.Parameters.Add("@PetID", SqlDbType.Int).Value = petID;
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        foreach (var field in fields) {
            var value = values.GetValueOrDefault(field.Name)?.Trim();
            command.Parameters.Add("@" + field.Name, SqlDbType.NVarChar, field.Limit).Value =
                string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
        }
        command.ExecuteNonQuery(); transaction.Commit(); return true;
    }
    private static void Scope(SqlCommand command, int userID, int petID, int householdID) {
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@PetID", SqlDbType.Int).Value = petID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
    }
}
