using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services
{
    public class PetService : IPetService
    {
        private readonly string _connStr;
        private readonly IHouseholdContextService _householdContext;
        private readonly IHouseholdAuthorizationService _authorization;

        // DI supplies configuration. Each method opens/disposes its own SQL connection;
        // scoped lifetime shares the service within a request, not a connection/transaction.
        public PetService(
            IConfiguration configuration,
            IHouseholdContextService householdContext,
            IHouseholdAuthorizationService authorization)
        {
            _connStr = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found in appsettings.json.");
            _householdContext = householdContext;
            _authorization = authorization;
        }

        public List<Pet> GetPetsByUser(int userID)
        {
            var household = _householdContext.GetActiveHousehold(userID);
            if (household == null)
                return [];

            var pets = new List<Pet>();
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("""
                SELECT p.petID, p.name, p.type, p.breed, p.age, p.birthdate,
                       p.gender, p.createdAt, p.ProfileImagePath
                FROM dbo.Pets p
                INNER JOIN dbo.HouseholdMembers hm
                  ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
                WHERE p.HouseholdID = @HouseholdID
                ORDER BY p.createdAt, p.petID;
                """, conn);
            AddScopeParameters(cmd, userID, household.HouseholdID);
            conn.Open();

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                pets.Add(new Pet
                {
                    PetID     = reader.GetInt32(reader.GetOrdinal("petID")),
                    Name      = reader["name"].ToString() ?? string.Empty,
                    Type      = reader["type"].ToString() ?? string.Empty,
                    Breed     = reader["breed"].ToString() ?? string.Empty,
                    Age       = reader["age"].ToString() ?? string.Empty,
                    Birthdate = reader.IsDBNull(reader.GetOrdinal("birthdate"))
                        ? default
                        : reader.GetDateTime(reader.GetOrdinal("birthdate")),
                    Gender    = reader["gender"].ToString() ?? string.Empty,
                    ProfileImagePath = reader.IsDBNull(reader.GetOrdinal("ProfileImagePath"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("ProfileImagePath"))
                });
            }
            return pets;
        }

        public List<TaskItem> GetTasksByPetID(int userID, int petID, bool allTime)
        {
            var household = _householdContext.GetActiveHousehold(userID);
            if (household == null)
                return [];

            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand($"""
                SELECT t.taskID, t.petID, p.name AS petName, t.taskType, t.notes,
                       t.createdAt, t.RecordedByUserID, u.name AS RecordedByName
                FROM dbo.Tasks t
                INNER JOIN dbo.Pets p ON p.petID = t.petID
                INNER JOIN dbo.HouseholdMembers hm
                  ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
                LEFT JOIN dbo.Users u ON u.userID = t.RecordedByUserID
                WHERE t.petID = @PetID AND p.HouseholdID = @HouseholdID
                {(allTime ? string.Empty : "AND t.createdAt >= DATEADD(DAY, -7, SYSDATETIME())")}
                ORDER BY t.createdAt DESC, t.taskID DESC;
                """, conn);
            cmd.Parameters.Add("@PetID", SqlDbType.Int).Value = petID;
            AddScopeParameters(cmd, userID, household.HouseholdID);
            conn.Open();

            using var reader = cmd.ExecuteReader();
            return ReadTasks(reader);
        }

        public List<TaskItem> GetTasksByPetIDSince(int userID, int petID, DateTime startDate, out bool hasOlderTasks)
        {
            var household = _householdContext.GetActiveHousehold(userID);
            if (household == null)
            {
                hasOlderTasks = false;
                return [];
            }

            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("""
                SELECT
                    t.taskID,
                    t.petID,
                    p.[name] AS petName,
                    t.taskType,
                    t.notes,
                    t.createdAt,
                    t.RecordedByUserID,
                    u.name AS RecordedByName
                FROM dbo.Tasks AS t
                INNER JOIN dbo.Pets AS p ON p.petID = t.petID
                INNER JOIN dbo.HouseholdMembers hm
                  ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
                LEFT JOIN dbo.Users u ON u.userID = t.RecordedByUserID
                WHERE t.petID = @petID
                  AND p.HouseholdID = @HouseholdID
                  AND t.createdAt >= @startDate
                ORDER BY t.createdAt DESC;

                SELECT CAST(CASE WHEN EXISTS
                (
                    SELECT 1
                    FROM dbo.Tasks AS older
                    INNER JOIN dbo.Pets olderPet ON olderPet.petID = older.petID
                    INNER JOIN dbo.HouseholdMembers olderMember
                      ON olderMember.HouseholdID = olderPet.HouseholdID
                     AND olderMember.UserID = @UserID AND olderMember.Status = N'Active'
                    WHERE older.petID = @petID AND olderPet.HouseholdID = @HouseholdID
                      AND older.createdAt < @startDate
                ) THEN 1 ELSE 0 END AS bit) AS hasOlder;
                """, conn);
            cmd.Parameters.AddWithValue("@petID", petID);
            cmd.Parameters.Add("@startDate", SqlDbType.DateTime2).Value = startDate;
            AddScopeParameters(cmd, userID, household.HouseholdID);
            conn.Open();

            using var reader = cmd.ExecuteReader();
            var tasks = ReadTasks(reader);
            hasOlderTasks = reader.NextResult()
                && reader.Read()
                && reader.GetBoolean(reader.GetOrdinal("hasOlder"));
            return tasks;
        }

        public List<TaskItem> GetLatestActivityTasksByPetID(int userID, int petID)
        {
            var household = _householdContext.GetActiveHousehold(userID);
            if (household == null)
                return [];

            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("""
                SELECT DISTINCT
                    latest.taskID,
                    latest.petID,
                    latest.petName,
                    latest.taskType,
                    latest.notes,
                    latest.createdAt,
                    latest.RecordedByUserID,
                    latest.RecordedByName
                FROM (VALUES ('Pee'), ('Poop')) AS activity(activityType)
                CROSS APPLY
                (
                    SELECT TOP (1)
                        t.taskID,
                        t.petID,
                        p.[name] AS petName,
                        t.taskType,
                        t.notes,
                        t.createdAt,
                        t.RecordedByUserID,
                        u.name AS RecordedByName
                    FROM dbo.Tasks AS t
                    INNER JOIN dbo.Pets AS p ON p.petID = t.petID
                    INNER JOIN dbo.HouseholdMembers hm
                      ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
                    LEFT JOIN dbo.Users u ON u.userID = t.RecordedByUserID
                    WHERE t.petID = @petID
                      AND p.HouseholdID = @HouseholdID
                      AND (t.taskType = activity.activityType OR t.taskType = 'Pee & Poop')
                    ORDER BY t.createdAt DESC
                ) AS latest
                ORDER BY latest.createdAt DESC;
                """, conn);
            cmd.Parameters.AddWithValue("@petID", petID);
            AddScopeParameters(cmd, userID, household.HouseholdID);
            conn.Open();

            using var reader = cmd.ExecuteReader();
            return ReadTasks(reader);
        }

        public Pet? GetPetByID(int userID, int petID)
        {
            return GetPetsByUser(userID).FirstOrDefault(pet => pet.PetID == petID);
        }

        public int AddPet(int userID, string name, string type, string breed, string age, DateTime birthdate, string gender)
        {
            var household = _householdContext.GetActiveHousehold(userID);
            if (household == null
                || !_authorization.HasPermission(userID, household.HouseholdID, HouseholdPermission.ManagePets))
                return 0;

            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("""
                INSERT dbo.Pets
                    (userID, HouseholdID, name, type, breed, age, birthdate, gender, createdAt)
                OUTPUT INSERTED.petID
                VALUES
                    (@UserID, @HouseholdID, @Name, @Type, @Breed, @Age, @Birthdate, @Gender, SYSDATETIME());
                """, conn);
            cmd.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            cmd.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = household.HouseholdID;
            cmd.Parameters.Add("@Name", SqlDbType.VarChar, 100).Value = (name ?? string.Empty).Trim();
            cmd.Parameters.Add("@Type", SqlDbType.VarChar, 50).Value = CleanOrNull(type);
            cmd.Parameters.Add("@Breed", SqlDbType.VarChar, 50).Value = CleanOrNull(breed);
            cmd.Parameters.Add("@Age", SqlDbType.Int).Value =
                int.TryParse(age, out var parsedAge) ? parsedAge : DBNull.Value;
            cmd.Parameters.Add("@Birthdate", SqlDbType.Date).Value = birthdate == default ? DBNull.Value : birthdate.Date;
            cmd.Parameters.Add("@Gender", SqlDbType.VarChar, 20).Value = CleanOrNull(gender);
            conn.Open();
            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value)
                throw new InvalidOperationException("AddPet did not return the new pet ID. Apply the profile-image migration first.");

            return Convert.ToInt32(result);
        }

        public bool EditPet(int userID, int petID, string name, string type, string breed, string age, DateTime birthdate, string gender)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.ManagePets);
            if (household == null)
                return false;
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UpdatePet", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            cmd.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = household.HouseholdID;
            cmd.Parameters.AddWithValue("@petID", petID);
            // Same AddWithValue(null) pitfall as AddPet (see the comment there) — clearing
            // Breed or Age while editing a pet would hit the identical "parameter not
            // supplied" failure without this.
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = name ?? string.Empty;
            cmd.Parameters.Add("@type", SqlDbType.NVarChar, 255).Value = type ?? string.Empty;
            cmd.Parameters.Add("@breed", SqlDbType.NVarChar, 255).Value = breed ?? string.Empty;
            cmd.Parameters.Add("@age", SqlDbType.NVarChar, 50).Value = age ?? string.Empty;
            cmd.Parameters.AddWithValue("@birthdate", birthdate);
            cmd.Parameters.Add("@gender", SqlDbType.NVarChar, 50).Value = gender ?? string.Empty;
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, household.HouseholdID, petID, OwnedRecordCommand.Pet);
        }

        public bool UpdatePetProfileImagePath(int userID, int petID, string? profileImagePath)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.ManagePets);
            if (household == null)
                return false;
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UpdatePetProfileImagePath", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@petID", petID);
            cmd.Parameters.Add("@ProfileImagePath", SqlDbType.NVarChar, 255).Value =
                profileImagePath == null ? DBNull.Value : profileImagePath;
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, household.HouseholdID, petID, OwnedRecordCommand.Pet);
        }

        public bool DeletePet(int userID, int petID)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.ManagePets);
            if (household == null)
                return false;
            using var conn = new SqlConnection(_connStr);
            // The legacy procedure was created with QUOTED_IDENTIFIER OFF,
            // which fails against the vet-visit filtered indexes. Execute the
            // cascade with correct SET options inside the ownership transaction.
            using var cmd = new SqlCommand("""
                SET QUOTED_IDENTIFIER ON;
                DELETE s FROM dbo.MedicationSchedule s
                INNER JOIN dbo.Medications m ON m.medID = s.medID
                INNER JOIN dbo.Pets p ON p.petID = m.petID
                WHERE p.petID = @petID;
                DELETE m FROM dbo.Medications m
                INNER JOIN dbo.Pets p ON p.petID = m.petID
                WHERE p.petID = @petID;
                DELETE t FROM dbo.Tasks t
                INNER JOIN dbo.Pets p ON p.petID = t.petID
                WHERE p.petID = @petID;
                DELETE v FROM dbo.VetVisits v
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                WHERE p.petID = @petID;
                DELETE FROM dbo.Pets WHERE petID = @petID;
                """, conn);
            cmd.Parameters.AddWithValue("@petID", petID);
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, household.HouseholdID, petID, OwnedRecordCommand.Pet);
        }

        public bool AddTask(int userID, int petID, string taskType, string notes, DateTime createdAt)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.RecordCare);
            if (household == null)
                return false;
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("""
                INSERT dbo.Tasks (petID, taskType, notes, createdAt, RecordedByUserID)
                VALUES (@petID, @taskType, @notes, @createdAt, @RecordedByUserID);
                """, conn);
            cmd.Parameters.Add("@petID", SqlDbType.Int).Value = petID;
            cmd.Parameters.Add("@taskType", SqlDbType.VarChar, 50).Value = taskType;
            cmd.Parameters.Add("@notes", SqlDbType.VarChar, 255).Value =
                string.IsNullOrEmpty(notes) ? string.Empty : notes;
            cmd.Parameters.Add("@createdAt", SqlDbType.DateTime).Value = createdAt;
            cmd.Parameters.Add("@RecordedByUserID", SqlDbType.Int).Value = userID;
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, household.HouseholdID, petID, OwnedRecordCommand.Pet);
        }

        public bool UpdateTask(int userID, int taskID, string taskType, string notes, DateTime createdAt)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.RecordCare);
            if (household == null)
                return false;
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UpdateTaskByID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            cmd.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = household.HouseholdID;
            cmd.Parameters.AddWithValue("@taskID", taskID);
            cmd.Parameters.AddWithValue("@taskType", taskType);
            cmd.Parameters.AddWithValue("@notes", string.IsNullOrEmpty(notes) ? string.Empty : notes);
            cmd.Parameters.AddWithValue("@createdAt", createdAt);
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, household.HouseholdID, taskID, OwnedRecordCommand.Task);
        }

        public bool DeleteTask(int userID, int taskID)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.RecordCare);
            if (household == null)
                return false;
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("DeleteTaskByTaskID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            cmd.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = household.HouseholdID;
            cmd.Parameters.AddWithValue("@taskID", taskID);
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, household.HouseholdID, taskID, OwnedRecordCommand.Task);
        }

        private static List<TaskItem> ReadTasks(SqlDataReader reader)
        {
            var tasks = new List<TaskItem>();
            while (reader.Read())
            {
                tasks.Add(new TaskItem
                {
                    TaskID    = reader.GetInt32(reader.GetOrdinal("taskID")),
                    PetID     = reader.GetInt32(reader.GetOrdinal("petID")),
                    PetName   = reader["petName"].ToString() ?? string.Empty,
                    TaskType  = reader["taskType"].ToString() ?? string.Empty,
                    Notes     = reader["notes"].ToString() ?? string.Empty,
                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("createdAt")),
                    RecordedByUserID = reader.IsDBNull(reader.GetOrdinal("RecordedByUserID"))
                        ? null
                        : reader.GetInt32(reader.GetOrdinal("RecordedByUserID")),
                    RecordedByName = reader.IsDBNull(reader.GetOrdinal("RecordedByName"))
                        ? string.Empty
                        : reader["RecordedByName"].ToString() ?? string.Empty
                });
            }

            return tasks;
        }

        private HouseholdContext? GetAuthorizedHousehold(int userID, HouseholdPermission permission)
        {
            var household = _householdContext.GetActiveHousehold(userID);
            return household != null
                && _authorization.HasPermission(userID, household.HouseholdID, permission)
                ? household
                : null;
        }

        private static void AddScopeParameters(SqlCommand command, int userID, int householdID)
        {
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        }

        private static object CleanOrNull(string? value) =>
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    }
}
