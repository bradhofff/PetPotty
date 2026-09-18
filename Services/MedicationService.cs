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

        public List<Medication> GetMedicationsByPetID(int userID, int householdID, int petID)
        {
            var list = new List<Medication>();
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("GetMedicationsByPetID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID", userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
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
                    TimingDoesNotMatter = reader.GetBoolean(reader.GetOrdinal("TimingDoesNotMatter")),
                    StartDate       = reader.GetDateTime(reader.GetOrdinal("startDate")),
                    EndDate         = reader.IsDBNull(reader.GetOrdinal("endDate"))
                                        ? null
                                        : reader.GetDateTime(reader.GetOrdinal("endDate")),
                    Notes           = reader["notes"].ToString() ?? string.Empty
                });
            }
            return list;
        }

        public List<MedSchedule> GetScheduleByPetID(int userID, int householdID, int petID, bool showAllTime)
        {
            var list = new List<MedSchedule>();
            string sp = showAllTime
                ? "GetScheduledMedsByPetID_AllTime"
                : "GetScheduledMedsByPetID_Next2Months";
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
            while (reader.Read())
            {
                list.Add(new MedSchedule
                {
                    MedID          = reader.GetInt32(reader.GetOrdinal("medID")),
                    MedicationName = reader["medicationName"].ToString() ?? string.Empty,
                    FrequencyType  = reader["frequencyType"].ToString() ?? string.Empty,
                    TimingDoesNotMatter = reader.GetBoolean(reader.GetOrdinal("TimingDoesNotMatter")),
                    ScheduleDate   = reader.GetDateTime(reader.GetOrdinal("scheduleDate")),
                    IsConfirmed    = reader.GetBoolean(reader.GetOrdinal("isConfirmed")),
                    ConfirmedAt    = reader.IsDBNull(reader.GetOrdinal("confirmedAt"))
                                        ? null
                                        : reader.GetDateTime(reader.GetOrdinal("confirmedAt")),
                    DoseStatus     = reader["DoseStatus"].ToString() ?? "Due",
                    AdministeredAtUtc = reader.IsDBNull(reader.GetOrdinal("AdministeredAtUtc"))
                                        ? null
                                        : DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("AdministeredAtUtc")), DateTimeKind.Utc),
                    RecordedAtUtc  = reader.IsDBNull(reader.GetOrdinal("RecordedAtUtc"))
                                        ? null
                                        : DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("RecordedAtUtc")), DateTimeKind.Utc),
                    RecordedByUserID = reader.IsDBNull(reader.GetOrdinal("RecordedByUserID"))
                                        ? null
                                        : reader.GetInt32(reader.GetOrdinal("RecordedByUserID")),
                    RecordedByName = reader.IsDBNull(reader.GetOrdinal("RecordedByName"))
                                        ? string.Empty
                                        : reader.GetString(reader.GetOrdinal("RecordedByName")),
                    StatusReason   = reader.IsDBNull(reader.GetOrdinal("StatusReason"))
                                        ? null
                                        : reader.GetString(reader.GetOrdinal("StatusReason")),
                    AdministrationNotes = reader.IsDBNull(reader.GetOrdinal("AdministrationNotes"))
                                        ? null
                                        : reader.GetString(reader.GetOrdinal("AdministrationNotes"))
                });
            }
            return list;
        }

        public int AddMedication(int userID, int householdID, int petID, string medicationName, string dosage,
                                  string frequencyType, int? frequencyInterval, bool timingDoesNotMatter,
                                  DateTime startDate, DateTime? endDate, string notes)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("AddMedication", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID",            userID);
            cmd.Parameters.AddWithValue("@HouseholdID",       householdID);
            cmd.Parameters.AddWithValue("@petID",             petID);
            cmd.Parameters.AddWithValue("@medicationName",    medicationName);
            cmd.Parameters.AddWithValue("@dosage",            dosage);
            cmd.Parameters.AddWithValue("@frequencyType",     frequencyType);
            cmd.Parameters.AddWithValue("@frequencyInterval", (object?)frequencyInterval ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@TimingDoesNotMatter", timingDoesNotMatter);
            cmd.Parameters.AddWithValue("@startDate",         startDate);
            cmd.Parameters.AddWithValue("@endDate",           (object?)endDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@notes",             string.IsNullOrEmpty(notes) ? string.Empty : notes);
            conn.Open();
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        public bool UpdateMedication(int userID, int householdID, int medID, string medicationName, string dosage,
                                     string frequencyType, int? frequencyInterval, bool timingDoesNotMatter,
                                     DateTime startDate, DateTime? endDate, string notes)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UpdateMedication", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID",            userID);
            cmd.Parameters.AddWithValue("@HouseholdID",       householdID);
            cmd.Parameters.AddWithValue("@medID",             medID);
            cmd.Parameters.AddWithValue("@medicationName",    medicationName);
            cmd.Parameters.AddWithValue("@dosage",            dosage);
            cmd.Parameters.AddWithValue("@frequencyType",     frequencyType);
            cmd.Parameters.AddWithValue("@frequencyInterval", (object?)frequencyInterval ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@TimingDoesNotMatter", timingDoesNotMatter);
            cmd.Parameters.AddWithValue("@startDate",         startDate);
            cmd.Parameters.AddWithValue("@endDate",           (object?)endDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@notes",             string.IsNullOrEmpty(notes) ? string.Empty : notes);
            conn.Open();
            return Convert.ToBoolean(cmd.ExecuteScalar());
        }

        public bool DeleteMedication(int userID, int householdID, int medID)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("DeleteMedicationByID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID", userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
            cmd.Parameters.AddWithValue("@medID", medID);
            conn.Open();
            return Convert.ToBoolean(cmd.ExecuteScalar());
        }

        public void ConfirmSchedule(int userID, int householdID, int medID, DateTime logDate, DateTime confirmedAt,
                                     int recordedByUserID, DateTime administeredAtUtc, string doseStatus,
                                     string? administrationNotes = null)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("ConfirmMedicationSchedule", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID",       userID);
            cmd.Parameters.AddWithValue("@HouseholdID",  householdID);
            cmd.Parameters.AddWithValue("@medID",        medID);
            cmd.Parameters.AddWithValue("@logDate",      logDate);
            cmd.Parameters.AddWithValue("@confirmedAt",  confirmedAt);
            cmd.Parameters.AddWithValue("@RecordedByUserID", recordedByUserID);
            cmd.Parameters.AddWithValue("@AdministeredAtUtc", administeredAtUtc);
            cmd.Parameters.AddWithValue("@DoseStatus",   doseStatus);
            cmd.Parameters.AddWithValue("@AdministrationNotes", (object?)administrationNotes ?? DBNull.Value);
            conn.Open();
            cmd.ExecuteNonQuery();
        }

        public void UnconfirmSchedule(int userID, int householdID, int medID, DateTime logDate)
        {
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("UnconfirmMedicationSchedule", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@UserID",      userID);
            cmd.Parameters.AddWithValue("@HouseholdID", householdID);
            cmd.Parameters.AddWithValue("@medID",       medID);
            cmd.Parameters.AddWithValue("@logDate",     logDate);
            conn.Open();
            cmd.ExecuteNonQuery();
        }
    }
}
