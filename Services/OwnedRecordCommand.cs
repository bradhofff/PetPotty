using Microsoft.Data.SqlClient;
using System.Data;

namespace PetPotty.Services;

// Keep the ownership check and legacy stored procedure in one transaction. The
// locks prevent ownership/parent changes between authorization and the mutation.
internal static class OwnedRecordCommand
{
    public static bool Execute(
        SqlCommand command,
        int userID,
        int householdID,
        int recordID,
        string ownershipSql)
    {
        using var transaction = command.Connection!.BeginTransaction();
        using var ownership = new SqlCommand(ownershipSql, command.Connection, transaction);
        ownership.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        ownership.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        ownership.Parameters.Add("@RecordID", SqlDbType.Int).Value = recordID;
        if (ownership.ExecuteScalar() == null)
            return false;

        command.Transaction = transaction;
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    public const string Pet = """
        SELECT 1
        FROM dbo.Pets p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.HouseholdMembers hm WITH (UPDLOCK, HOLDLOCK)
          ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
        WHERE p.petID = @RecordID AND p.HouseholdID = @HouseholdID;
        """;

    public const string Task = """
        SELECT 1 FROM dbo.Tasks t WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.Pets p WITH (UPDLOCK, HOLDLOCK) ON p.petID = t.petID
        INNER JOIN dbo.HouseholdMembers hm WITH (UPDLOCK, HOLDLOCK)
          ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
        WHERE t.taskID = @RecordID AND p.HouseholdID = @HouseholdID;
        """;

    public const string Medication = """
        SELECT 1 FROM dbo.Medications m WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.Pets p WITH (UPDLOCK, HOLDLOCK) ON p.petID = m.petID
        INNER JOIN dbo.HouseholdMembers hm WITH (UPDLOCK, HOLDLOCK)
          ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
        WHERE m.medID = @RecordID AND p.HouseholdID = @HouseholdID;
        """;
}
