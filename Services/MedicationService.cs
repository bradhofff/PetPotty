using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services
{
    public class MedicationService : IMedicationService
    {
        private readonly string _connStr;

        public MedicationService(IConfiguration configuration)
        {
            _connStr = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        }

        public List<Medication> GetMedicationsByPetID(int petID)
        {
            var list = new List<Medication>();
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("GetMedicationsByPetID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@petID", petID);
            conn.Open();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new Medication
                {
                    MedID           = reader.GetInt32(reader.GetOrdinal("medID")),
                    PetID           = petID,
                    MedicationName  = reader["medicationName"].ToString() ?? string.Empty,
                    Dosage          = reader["dosage"].ToString() ?? string.Empty,
                    FrequencyType   = reader["frequencyType"].ToString() ?? string.Empty,
                    FrequencyInterval = reader.IsDBNull(reader.GetOrdinal("frequencyInterval"))
                                        ? null
                                        : reader.GetInt32(reader.GetOrdinal("frequencyInterval")),
                    StartDate       = reader.GetDateTime(reader.GetOrdinal("startDate")),
                    EndDate         = reader.IsDBNull(reader.GetOrdinal("endDate"))
                                        ? null
                                        : reader.GetDateTime(reader.GetOrdinal("endDate")),
                    Notes           = reader["notes"].ToString() ?? string.Empty
                });
            }
            return list;
        }

        public List<MedSchedule> GetScheduleByPetID(int petID, bool allTime)
        {
            var list = new List<MedSchedule>();
            string sp = allTime ? "GetScheduledMedsByPetID" : "GetScheduledMedsByPetID_Month";
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
                list.Add(new MedSchedule
                {
                    MedID          = reader.GetInt32(reader.GetOrdinal("medID")),
                    MedicationName = reader["medicationName"].ToString() ?? string.Empty,
                    ScheduleDate   = reader.GetDateTime(reader.GetOrdinal("scheduleDate")),
                    IsConfirmed    = reader.GetBoolean(reader.GetOrdinal("isConfirmed"))
                });
            }
            return list;
        }

        public void AddMedication(int petID, string medicationName, string dosage,
                                  string frequencyType, int? frequencyInterval,
                                  DateTime startDate, DateTime? endDate, string notes)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("AddMedication", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@petID",             petID);
            cmd.Parameters.AddWithValue("@medicationName",    medicationName);
            cmd.Parameters.AddWithValue("@dosage",            dosage);
            cmd.Parameters.AddWithValue("@frequencyType",     frequencyType);
            cmd.Parameters.AddWithValue("@frequencyInterval", (object?)frequencyInterval ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@startDate",         startDate);
            cmd.Parameters.AddWithValue("@endDate",           (object?)endDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@notes",             string.IsNullOrEmpty(notes) ? string.Empty : notes);
            conn.Open();
            cmd.ExecuteNonQuery();
        }

        public void UpdateMedication(int medID, string medicationName, string dosage,
                                     string frequencyType, int? frequencyInterval,
                                     DateTime startDate, DateTime? endDate, string notes)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UpdateMedication", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@medID",             medID);
            cmd.Parameters.AddWithValue("@medicationName",    medicationName);
            cmd.Parameters.AddWithValue("@dosage",            dosage);
            cmd.Parameters.AddWithValue("@frequencyType",     frequencyType);
            cmd.Parameters.AddWithValue("@frequencyInterval", (object?)frequencyInterval ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@startDate",         startDate);
            cmd.Parameters.AddWithValue("@endDate",           (object?)endDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@notes",             string.IsNullOrEmpty(notes) ? string.Empty : notes);
            conn.Open();
            cmd.ExecuteNonQuery();
        }

        public void DeleteMedication(int medID)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("DeleteMedication", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@medID", medID);
            conn.Open();
            cmd.ExecuteNonQuery();
        }

        public void ConfirmSchedule(int medID, DateTime logDate)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("ConfirmMedicationSchedule", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@medID",   medID);
            cmd.Parameters.AddWithValue("@logDate", logDate);
            conn.Open();
            cmd.ExecuteNonQuery();
        }

        public void UnconfirmSchedule(int medID, DateTime logDate)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UnconfirmMedicationSchedule", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@medID",   medID);
            cmd.Parameters.AddWithValue("@logDate", logDate);
            conn.Open();
            cmd.ExecuteNonQuery();
        }
    }
}
