using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services
{
    public class PetService : IPetService
    {
        private readonly string _connStr;

        // DI supplies configuration. Each method opens/disposes its own SQL connection;
        // scoped lifetime shares the service within a request, not a connection/transaction.
        public PetService(IConfiguration configuration)
        {
            _connStr = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found in appsettings.json.");
        }

        public List<Pet> GetPetsByUser(int userID)
        {
            var pets = new List<Pet>();
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("GetPetsByUserID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@userID", userID);
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
                    Birthdate = reader.GetDateTime(reader.GetOrdinal("birthdate")),
                    Gender    = reader["gender"].ToString() ?? string.Empty,
                    ProfileImagePath = reader.IsDBNull(reader.GetOrdinal("ProfileImagePath"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("ProfileImagePath"))
                });
            }
            return pets;
        }

        public List<TaskItem> GetTasksByPetID(int petID, bool allTime)
        {
            string sp = allTime ? "GetTasksByPetID" : "GetTasksByPetID_Recent";

            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand(sp, conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@petID", petID);
            conn.Open();

            using var reader = cmd.ExecuteReader();
            return ReadTasks(reader);
        }

        public List<TaskItem> GetTasksByPetIDSince(int petID, DateTime startDate, out bool hasOlderTasks)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("""
                SELECT
                    t.taskID,
                    t.petID,
                    p.[name] AS petName,
                    t.taskType,
                    t.notes,
                    t.createdAt
                FROM dbo.Tasks AS t
                INNER JOIN dbo.Pets AS p ON p.petID = t.petID
                WHERE t.petID = @petID
                  AND t.createdAt >= @startDate
                ORDER BY t.createdAt DESC;

                SELECT CAST(CASE WHEN EXISTS
                (
                    SELECT 1
                    FROM dbo.Tasks AS older
                    WHERE older.petID = @petID
                      AND older.createdAt < @startDate
                ) THEN 1 ELSE 0 END AS bit) AS hasOlder;
                """, conn);
            cmd.Parameters.AddWithValue("@petID", petID);
            cmd.Parameters.Add("@startDate", SqlDbType.DateTime2).Value = startDate;
            conn.Open();

            using var reader = cmd.ExecuteReader();
            var tasks = ReadTasks(reader);
            hasOlderTasks = reader.NextResult()
                && reader.Read()
                && reader.GetBoolean(reader.GetOrdinal("hasOlder"));
            return tasks;
        }

        public List<TaskItem> GetLatestActivityTasksByPetID(int petID)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("""
                SELECT DISTINCT
                    latest.taskID,
                    latest.petID,
                    latest.petName,
                    latest.taskType,
                    latest.notes,
                    latest.createdAt
                FROM (VALUES ('Pee'), ('Poop')) AS activity(activityType)
                CROSS APPLY
                (
                    SELECT TOP (1)
                        t.taskID,
                        t.petID,
                        p.[name] AS petName,
                        t.taskType,
                        t.notes,
                        t.createdAt
                    FROM dbo.Tasks AS t
                    INNER JOIN dbo.Pets AS p ON p.petID = t.petID
                    WHERE t.petID = @petID
                      AND (t.taskType = activity.activityType OR t.taskType = 'Pee & Poop')
                    ORDER BY t.createdAt DESC
                ) AS latest
                ORDER BY latest.createdAt DESC;
                """, conn);
            cmd.Parameters.AddWithValue("@petID", petID);
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
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("AddPet", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@userID", userID);
            // Breed and Age (and, defensively, the other free-text fields) are optional in
            // the Add Pet form. When that form field is submitted empty, ASP.NET Core's model
            // binder converts it to a C# null rather than "" (ConvertEmptyStringToNull). Handing
            // AddWithValue a null reference (as opposed to DBNull.Value) leaves SqlClient unable
            // to infer a type for the parameter, and it silently drops it from the RPC call
            // entirely — producing "Procedure 'AddPet' expects parameter '@breed', which was not
            // supplied" instead of inserting an empty string. Explicit typed parameters plus a
            // null-coalesce keep every one of these fields a real (possibly empty) NVARCHAR.
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = name ?? string.Empty;
            cmd.Parameters.Add("@type", SqlDbType.NVarChar, 255).Value = type ?? string.Empty;
            cmd.Parameters.Add("@breed", SqlDbType.NVarChar, 255).Value = breed ?? string.Empty;
            cmd.Parameters.Add("@age", SqlDbType.NVarChar, 50).Value = age ?? string.Empty;
            cmd.Parameters.AddWithValue("@birthdate", birthdate);
            cmd.Parameters.Add("@gender", SqlDbType.NVarChar, 50).Value = gender ?? string.Empty;
            cmd.Parameters.AddWithValue("@createdAt", DateTime.Now);
            conn.Open();
            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value)
                throw new InvalidOperationException("AddPet did not return the new pet ID. Apply the profile-image migration first.");

            return Convert.ToInt32(result);
        }

        public bool EditPet(int userID, int petID, string name, string type, string breed, string age, DateTime birthdate, string gender)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UpdatePet", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
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
            return OwnedRecordCommand.Execute(cmd, userID, petID, OwnedRecordCommand.Pet);
        }

        public bool UpdatePetProfileImagePath(int userID, int petID, string? profileImagePath)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UpdatePetProfileImagePath", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@petID", petID);
            cmd.Parameters.Add("@ProfileImagePath", SqlDbType.NVarChar, 255).Value =
                profileImagePath == null ? DBNull.Value : profileImagePath;
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, petID, OwnedRecordCommand.Pet);
        }

        public bool DeletePet(int userID, int petID)
        {
            using var conn = new SqlConnection(_connStr);
            // The legacy procedure was created with QUOTED_IDENTIFIER OFF,
            // which fails against the vet-visit filtered indexes. Execute the
            // cascade with correct SET options inside the ownership transaction.
            using var cmd = new SqlCommand("""
                SET QUOTED_IDENTIFIER ON;
                DELETE s FROM dbo.MedicationSchedule s
                INNER JOIN dbo.Medications m ON m.medID = s.medID
                INNER JOIN dbo.Pets p ON p.petID = m.petID
                WHERE p.petID = @petID AND p.userID = @UserID;
                DELETE m FROM dbo.Medications m
                INNER JOIN dbo.Pets p ON p.petID = m.petID
                WHERE p.petID = @petID AND p.userID = @UserID;
                DELETE t FROM dbo.Tasks t
                INNER JOIN dbo.Pets p ON p.petID = t.petID
                WHERE p.petID = @petID AND p.userID = @UserID;
                DELETE v FROM dbo.VetVisits v
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                WHERE p.petID = @petID AND p.userID = @UserID;
                DELETE FROM dbo.Pets WHERE petID = @petID AND userID = @UserID;
                """, conn);
            cmd.Parameters.AddWithValue("@petID", petID);
            cmd.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, petID, OwnedRecordCommand.Pet);
        }

        public bool AddTask(int userID, int petID, string taskType, string notes, DateTime createdAt)
        {
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
            return OwnedRecordCommand.Execute(cmd, userID, petID, OwnedRecordCommand.Pet);
        }

        public bool UpdateTask(int userID, int taskID, string taskType, string notes, DateTime createdAt)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UpdateTaskByID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@taskID", taskID);
            cmd.Parameters.AddWithValue("@taskType", taskType);
            cmd.Parameters.AddWithValue("@notes", string.IsNullOrEmpty(notes) ? string.Empty : notes);
            cmd.Parameters.AddWithValue("@createdAt", createdAt);
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, taskID, OwnedRecordCommand.Task);
        }

        public bool DeleteTask(int userID, int taskID)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("DeleteTaskByTaskID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@taskID", taskID);
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, taskID, OwnedRecordCommand.Task);
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
                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("createdAt"))
                });
            }

            return tasks;
        }
    }
}
