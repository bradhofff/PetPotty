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
                    Gender    = reader["gender"].ToString() ?? string.Empty
                });
            }
            return pets;
        }

        public List<TaskItem> GetTasksByPetID(int petID, bool allTime)
        {
            var tasks = new List<TaskItem>();
            string sp = allTime ? "GetTasksByPetID" : "GetTasksByPetID_Past24";

            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand(sp, conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@petID", petID);
            conn.Open();

            using var reader = cmd.ExecuteReader();
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

        public void AddPet(int userID, string name, string type, string breed, string age, DateTime birthdate, string gender)
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
            cmd.ExecuteNonQuery();
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
    }
}
