using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services
{
    public class PetService : IPetService
    {
        private readonly string _connStr;

        public PetService(IConfiguration configuration)
        {
            _connStr = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found in appsettings.json.");
        }

        public List<Pet> GetPetsByHousehold(int userID, int householdID)
        {
            var pets = new List<Pet>();
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("GetPetsByHouseholdID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID", userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
            conn.Open();

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                pets.Add(new Pet
                {
                    PetID       = reader.GetInt32(reader.GetOrdinal("petID")),
                    HouseholdID = reader.GetInt32(reader.GetOrdinal("HouseholdID")),
                    Name        = reader["name"].ToString() ?? string.Empty,
                    Type        = reader["type"].ToString() ?? string.Empty,
                    Breed       = reader["breed"].ToString() ?? string.Empty,
                    Age         = reader["age"].ToString() ?? string.Empty,
                    Birthdate   = reader.GetDateTime(reader.GetOrdinal("birthdate")),
                    Gender      = reader["gender"].ToString() ?? string.Empty,
                    ProfileImagePath = reader.IsDBNull(reader.GetOrdinal("ProfileImagePath"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("ProfileImagePath"))
                });
            }
            return pets;
        }

        public Pet? GetPetByID(int userID, int householdID, int petID)
        {
            return GetPetsByHousehold(userID, householdID).FirstOrDefault(pet => pet.PetID == petID);
        }

        public List<TaskItem> GetTasksByPetID(int userID, int householdID, int petID, bool allTime)
        {
            string sp = allTime ? "GetTasksByPetID" : "GetTasksByPetID_Recent";

            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand(sp, conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID", userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
            cmd.Parameters.AddWithValue("@petID", petID);
            conn.Open();

            using var reader = cmd.ExecuteReader();
            return ReadTasks(reader);
        }

        public List<TaskItem> GetTasksByPetIDSince(int userID, int householdID, int petID, DateTime startDate, out bool hasOlderTasks)
        {
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
                INNER JOIN dbo.HouseholdMembers AS hm
                    ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
                LEFT JOIN dbo.Users AS u ON u.userID = t.RecordedByUserID
                WHERE t.petID = @petID
                  AND p.HouseholdID = @HouseholdID
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
            cmd.Parameters.AddWithValue("@UserID", userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
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

        public List<TaskItem> GetLatestActivityTasksByPetID(int userID, int householdID, int petID)
        {
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
                    LEFT JOIN dbo.Users AS u ON u.userID = t.RecordedByUserID
                    WHERE t.petID = @petID
                      AND p.HouseholdID = @HouseholdID
                      AND (t.taskType = activity.activityType OR t.taskType = 'Pee & Poop')
                    ORDER BY t.createdAt DESC
                ) AS latest
                INNER JOIN dbo.HouseholdMembers AS hm
                    ON hm.HouseholdID = @HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
                ORDER BY latest.createdAt DESC;
                """, conn);
            cmd.Parameters.AddWithValue("@UserID", userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
            cmd.Parameters.AddWithValue("@petID", petID);
            conn.Open();

            using var reader = cmd.ExecuteReader();
            return ReadTasks(reader);
        }

        public int AddPet(int userID, int householdID, string name, string type, string breed, string age, DateTime birthdate, string gender)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("AddPet", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID", userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@type", type);
            cmd.Parameters.AddWithValue("@breed", breed);
            cmd.Parameters.AddWithValue("@age", age);
            cmd.Parameters.AddWithValue("@birthdate", birthdate);
            cmd.Parameters.AddWithValue("@gender", gender);
            cmd.Parameters.AddWithValue("@createdAt", DateTime.Now);
            conn.Open();
            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value)
                throw new InvalidOperationException("AddPet did not return the new pet ID. Apply the profile-image migration first.");

            return Convert.ToInt32(result);
        }

        public bool EditPet(int userID, int householdID, int petID, string name, string type, string breed, string age, DateTime birthdate, string gender)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UpdatePet", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID", userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
            cmd.Parameters.AddWithValue("@petID", petID);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@type", type);
            cmd.Parameters.AddWithValue("@breed", breed);
            cmd.Parameters.AddWithValue("@age", age);
            cmd.Parameters.AddWithValue("@birthDate", birthdate);
            cmd.Parameters.AddWithValue("@gender", gender);
            conn.Open();
            return Convert.ToBoolean(cmd.ExecuteScalar());
        }

        public void UpdatePetProfileImagePath(int petID, string? profileImagePath)
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
            cmd.ExecuteNonQuery();
        }

        public bool DeletePet(int userID, int householdID, int petID)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("DeletePetByPetID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID", userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
            cmd.Parameters.AddWithValue("@petID", petID);
            conn.Open();
            return Convert.ToBoolean(cmd.ExecuteScalar());
        }

        public bool AddTask(int userID, int householdID, int petID, string taskType, string notes, DateTime createdAt)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("AddTaskByPetID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID", userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
            cmd.Parameters.AddWithValue("@petID", petID);
            cmd.Parameters.AddWithValue("@taskType", taskType);
            cmd.Parameters.AddWithValue("@notes", string.IsNullOrEmpty(notes) ? string.Empty : notes);
            cmd.Parameters.AddWithValue("@createdAt", createdAt);
            cmd.Parameters.AddWithValue("@RecordedByUserID", userID);
            conn.Open();
            return Convert.ToBoolean(cmd.ExecuteScalar());
        }

        public bool UpdateTask(int userID, int householdID, int taskID, string taskType, string notes, DateTime createdAt)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UpdateTaskByID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID", userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
            cmd.Parameters.AddWithValue("@taskID", taskID);
            cmd.Parameters.AddWithValue("@taskType", taskType);
            cmd.Parameters.AddWithValue("@notes", string.IsNullOrEmpty(notes) ? string.Empty : notes);
            cmd.Parameters.AddWithValue("@createdAt", createdAt);
            conn.Open();
            return Convert.ToBoolean(cmd.ExecuteScalar());
        }

        public bool DeleteTask(int userID, int householdID, int taskID)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("DeleteTaskByTaskID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID", userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
            cmd.Parameters.AddWithValue("@taskID", taskID);
            conn.Open();
            return Convert.ToBoolean(cmd.ExecuteScalar());
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
                        : reader.GetString(reader.GetOrdinal("RecordedByName"))
                });
            }

            return tasks;
        }
    }
}
