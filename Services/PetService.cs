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

        public void EditPet(int petID, string name, string type, string breed, string age, DateTime birthdate, string gender)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UpdatePet", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@petID", petID);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@type", type);
            cmd.Parameters.AddWithValue("@breed", breed);
            cmd.Parameters.AddWithValue("@age", age);
            cmd.Parameters.AddWithValue("@birthdate", birthdate);
            cmd.Parameters.AddWithValue("@gender", gender);
            conn.Open();
            cmd.ExecuteNonQuery();
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

        public void DeletePet(int petID)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("DeletePetByPetID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@petID", petID);
            conn.Open();
            cmd.ExecuteNonQuery();
        }

        public void AddTask(int petID, string taskType, string notes, DateTime createdAt)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("AddTaskByPetID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@petID", petID);
            cmd.Parameters.AddWithValue("@taskType", taskType);
            cmd.Parameters.AddWithValue("@notes", string.IsNullOrEmpty(notes) ? string.Empty : notes);
            cmd.Parameters.AddWithValue("@createdAt", createdAt);
            conn.Open();
            cmd.ExecuteNonQuery();
        }

        public void UpdateTask(int taskID, string taskType, string notes, DateTime createdAt)
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
            cmd.ExecuteNonQuery();
        }

        public void DeleteTask(int taskID)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("DeleteTaskByTaskID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@taskID", taskID);
            conn.Open();
            cmd.ExecuteNonQuery();
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
