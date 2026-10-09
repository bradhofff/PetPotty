using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.Data.SqlClient;
using PetPotty.Models;

namespace PetPotty.Services;

public sealed class PetWeightService(IConfiguration configuration, IHouseholdContextService context,
    IHouseholdAuthorizationService authorization, IUserTimeZoneService timeZone) : IPetWeightService
{
    private readonly string connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing database configuration.");

    private int? AuthorizedHousehold(int userID, HouseholdPermission permission)
    {
        var household = context.GetActiveHousehold(userID);
        return household != null && authorization.HasPermission(userID, household.HouseholdID, permission)
            ? household.HouseholdID : null;
    }

    public List<PetWeightEntry> GetWeights(int userID, int petID)
    {
        var householdID = AuthorizedHousehold(userID, HouseholdPermission.ViewPets);
        if (!householdID.HasValue) return [];
        using var connection = new SqlConnection(connectionString);
        using var command = new SqlCommand("""
            SELECT w.WeightEntryID, w.PetID, w.Weight, w.Unit, w.MeasuredAtUtc, w.Notes,
                   u.name AS RecordedBy
            FROM dbo.PetWeights w
            INNER JOIN dbo.Pets p ON p.petID = w.PetID
            INNER JOIN dbo.HouseholdMembers hm ON hm.HouseholdID = p.HouseholdID
                AND hm.UserID = @UserID AND hm.Status = N'Active'
            LEFT JOIN dbo.Users u ON u.userID = w.CreatedByUserID
            WHERE w.PetID = @PetID AND p.HouseholdID = @HouseholdID
            ORDER BY w.MeasuredAtUtc DESC, w.WeightEntryID DESC;
            """, connection);
        Scope(command, userID, petID, householdID.Value);
        connection.Open();
        using var reader = command.ExecuteReader();
        var entries = new List<PetWeightEntry>();
        while (reader.Read())
            entries.Add(new()
            {
                WeightEntryID = reader.GetInt32(0), PetID = reader.GetInt32(1),
                Weight = reader.GetDecimal(2), Unit = reader.GetString(3),
                MeasuredAtUtc = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                Notes = reader.IsDBNull(5) ? null : reader.GetString(5),
                RecordedBy = reader.IsDBNull(6) ? string.Empty : reader.GetString(6)
            });
        return entries;
    }

    public bool SaveWeight(int userID, PetWeightInput input)
    {
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);
        var householdID = AuthorizedHousehold(userID, HouseholdPermission.RecordCare);
        if (!householdID.HasValue) return false;
        using var connection = new SqlConnection(connectionString);
        using var command = new SqlCommand(input.WeightEntryID == 0 ? """
            INSERT dbo.PetWeights (PetID, Weight, Unit, MeasuredAtUtc, Notes, CreatedByUserID)
            OUTPUT INSERTED.WeightEntryID
            SELECT @PetID, @Weight, @Unit, @MeasuredAtUtc, @Notes, @UserID
            FROM dbo.Pets p
            INNER JOIN dbo.HouseholdMembers hm ON hm.HouseholdID = p.HouseholdID
                AND hm.UserID = @UserID AND hm.Status = N'Active'
                AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
            WHERE p.petID = @PetID AND p.HouseholdID = @HouseholdID;
            """ : """
            UPDATE w SET Weight = @Weight, Unit = @Unit, MeasuredAtUtc = @MeasuredAtUtc,
                Notes = @Notes, UpdatedAtUtc = SYSUTCDATETIME()
            OUTPUT INSERTED.WeightEntryID
            FROM dbo.PetWeights w
            INNER JOIN dbo.Pets p ON p.petID = w.PetID
            INNER JOIN dbo.HouseholdMembers hm ON hm.HouseholdID = p.HouseholdID
                AND hm.UserID = @UserID AND hm.Status = N'Active'
                AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
            WHERE w.WeightEntryID = @WeightEntryID AND w.PetID = @PetID
                AND p.HouseholdID = @HouseholdID;
            """, connection);
        Scope(command, userID, input.PetID, householdID.Value);
        command.Parameters.Add("@WeightEntryID", SqlDbType.Int).Value = input.WeightEntryID;
        var weight = command.Parameters.Add("@Weight", SqlDbType.Decimal);
        weight.Precision = 9; weight.Scale = 3; weight.Value = input.Weight!.Value;
        command.Parameters.Add("@Unit", SqlDbType.VarChar, 2).Value = input.Unit;
        command.Parameters.Add("@MeasuredAtUtc", SqlDbType.DateTime2).Value =
            timeZone.ToUtc(input.MeasuredAtLocal!.Value, input.UtcOffsetMinutes);
        command.Parameters.Add("@Notes", SqlDbType.NVarChar, 1000).Value =
            string.IsNullOrWhiteSpace(input.Notes) ? DBNull.Value : input.Notes.Trim();
        connection.Open();
        return command.ExecuteScalar() != null;
    }

    public bool DeleteWeight(int userID, int petID, int weightEntryID)
    {
        var householdID = AuthorizedHousehold(userID, HouseholdPermission.RecordCare);
        if (!householdID.HasValue) return false;
        using var connection = new SqlConnection(connectionString);
        using var command = new SqlCommand("""
            DELETE w
            OUTPUT DELETED.WeightEntryID
            FROM dbo.PetWeights w
            INNER JOIN dbo.Pets p ON p.petID = w.PetID
            INNER JOIN dbo.HouseholdMembers hm ON hm.HouseholdID = p.HouseholdID
                AND hm.UserID = @UserID AND hm.Status = N'Active'
                AND hm.Role IN (N'Owner', N'Member', N'Caregiver')
            WHERE w.WeightEntryID = @WeightEntryID AND w.PetID = @PetID
                AND p.HouseholdID = @HouseholdID;
            """, connection);
        Scope(command, userID, petID, householdID.Value);
        command.Parameters.Add("@WeightEntryID", SqlDbType.Int).Value = weightEntryID;
        connection.Open();
        return command.ExecuteScalar() != null;
    }

    private static void Scope(SqlCommand command, int userID, int petID, int householdID)
    {
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@PetID", SqlDbType.Int).Value = petID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
    }
}
