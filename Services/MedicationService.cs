using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services
{
    public class MedicationService : IMedicationService
    {
        private readonly string _connStr;
        private readonly IUserTimeZoneService _timeZone;
        private readonly IHouseholdContextService _householdContext;
        private readonly IHouseholdAuthorizationService _authorization;
        private readonly int _lateAfterMinutes;
        private readonly int _missedAfterMinutes;

        public MedicationService(
            IConfiguration configuration,
            IUserTimeZoneService timeZone,
            IHouseholdContextService householdContext,
            IHouseholdAuthorizationService authorization)
        {
            _connStr = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
            _timeZone = timeZone;
            _householdContext = householdContext;
            _authorization = authorization;
            _lateAfterMinutes = Math.Clamp(
                configuration.GetValue<int?>("MedicationAdherence:LateAfterMinutes") ?? 60,
                1,
                1440);
            _missedAfterMinutes = Math.Clamp(
                configuration.GetValue<int?>("MedicationAdherence:MissedAfterMinutes") ?? 720,
                _lateAfterMinutes,
                10080);
        }

        public bool OwnsMedication(int userID, int medID)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.ViewPets);
            if (household == null)
                return false;
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("""
                SELECT 1 FROM dbo.Medications m
                INNER JOIN dbo.Pets p ON p.petID = m.petID
                INNER JOIN dbo.HouseholdMembers hm
                  ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
                WHERE m.medID = @MedID AND p.HouseholdID = @HouseholdID;
                """, conn);
            cmd.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            cmd.Parameters.Add("@MedID", SqlDbType.Int).Value = medID;
            cmd.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = household.HouseholdID;
            conn.Open();
            return cmd.ExecuteScalar() != null;
        }

        public List<Medication> GetMedicationsByPetID(int userID, int petID)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.ViewPets);
            if (household == null)
                return [];
            var list = new List<Medication>();
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("""
                SELECT m.medID, m.petID, m.medicationName, m.dosage, m.frequencyType,
                       m.frequencyInterval, m.TimingDoesNotMatter, m.startDate, m.endDate, m.notes
                FROM dbo.Medications m
                INNER JOIN dbo.Pets p ON p.petID = m.petID
                INNER JOIN dbo.HouseholdMembers hm
                  ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
                WHERE m.petID = @PetID AND p.HouseholdID = @HouseholdID
                ORDER BY m.medicationName, m.medID;
                """, conn);
            AddScopeParameters(cmd, userID, household.HouseholdID);
            cmd.Parameters.Add("@PetID", SqlDbType.Int).Value = petID;
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

        public List<MedSchedule> GetScheduleByPetID(int userID, int petID, bool showAllTime, int utcOffsetMinutes = 0)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.ViewPets);
            if (household == null)
                return [];
            var list = new List<MedSchedule>();
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand($"""
                SELECT ms.scheduleID, m.medID, m.medicationName, m.dosage, m.frequencyType,
                       m.TimingDoesNotMatter, ms.scheduleDate, ms.isConfirmed, ms.confirmedAt,
                       ms.DoseStatus, ms.AdministeredAtUtc, ms.RecordedAtUtc,
                       ms.RecordedByUserID, u.name AS RecordedByName,
                       ms.StatusReason, ms.AdministrationNotes
                FROM dbo.MedicationSchedule ms
                INNER JOIN dbo.Medications m ON m.medID = ms.medID
                INNER JOIN dbo.Pets p ON p.petID = m.petID
                INNER JOIN dbo.HouseholdMembers hm
                  ON hm.HouseholdID = p.HouseholdID AND hm.UserID = @UserID AND hm.Status = N'Active'
                LEFT JOIN dbo.Users u ON u.userID = ms.RecordedByUserID
                WHERE m.petID = @PetID AND p.HouseholdID = @HouseholdID
                {(showAllTime ? string.Empty : "AND ms.scheduleDate < DATEADD(MONTH, 2, @Today) AND (ms.DoseStatus = N'Due' OR ms.scheduleDate >= @Today)")}
                ORDER BY ms.scheduleDate;
                """, conn);
            AddScopeParameters(cmd, userID, household.HouseholdID);
            cmd.Parameters.Add("@PetID", SqlDbType.Int).Value = petID;
            if (!showAllTime)
                cmd.Parameters.Add("@Today", SqlDbType.Date).Value = _timeZone.ToLocal(DateTime.UtcNow, utcOffsetMinutes).Date;
            conn.Open();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var schedule = new MedSchedule
                {
                    ScheduleID     = reader.GetInt32(reader.GetOrdinal("scheduleID")),
                    MedID          = reader.GetInt32(reader.GetOrdinal("medID")),
                    MedicationName = reader["medicationName"].ToString() ?? string.Empty,
                    Dosage         = reader["dosage"].ToString() ?? string.Empty,
                    FrequencyType  = reader["frequencyType"].ToString() ?? string.Empty,
                    TimingDoesNotMatter = reader.GetBoolean(reader.GetOrdinal("TimingDoesNotMatter")),
                    ScheduleDate   = reader.GetDateTime(reader.GetOrdinal("scheduleDate")),
                    IsConfirmed    = reader.GetBoolean(reader.GetOrdinal("isConfirmed")),
                    ConfirmedAt    = reader.IsDBNull(reader.GetOrdinal("confirmedAt"))
                                        ? null
                                        : reader.GetDateTime(reader.GetOrdinal("confirmedAt")),
                    DoseStatus     = reader["DoseStatus"].ToString() ?? MedicationDoseStatuses.Due,
                    AdministeredAtUtc = ReadNullableUtc(reader, "AdministeredAtUtc"),
                    RecordedAtUtc  = ReadNullableUtc(reader, "RecordedAtUtc"),
                    RecordedByUserID = reader.IsDBNull(reader.GetOrdinal("RecordedByUserID"))
                        ? null
                        : reader.GetInt32(reader.GetOrdinal("RecordedByUserID")),
                    RecordedByName = reader["RecordedByName"].ToString() ?? string.Empty,
                    StatusReason   = reader["StatusReason"].ToString() ?? string.Empty,
                    AdministrationNotes = reader["AdministrationNotes"].ToString() ?? string.Empty
                };

                schedule.EffectiveStatus = GetEffectiveStatus(schedule, utcOffsetMinutes);
                list.Add(schedule);
            }
            return list;
        }

        public bool AddMedication(int userID, int petID, string medicationName, string dosage,
                                  string frequencyType, int? frequencyInterval, bool timingDoesNotMatter,
                                  DateTime startDate, DateTime? endDate, string notes)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.ManageCarePlans);
            if (household == null)
                return false;
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
            cmd.Parameters.AddWithValue("@TimingDoesNotMatter", timingDoesNotMatter);
            cmd.Parameters.AddWithValue("@startDate",         startDate);
            cmd.Parameters.AddWithValue("@endDate",           (object?)endDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@notes",             string.IsNullOrEmpty(notes) ? string.Empty : notes);
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, household.HouseholdID, petID, OwnedRecordCommand.Pet);
        }

        public bool UpdateMedication(int userID, int medID, string medicationName, string dosage,
                                     string frequencyType, int? frequencyInterval, bool timingDoesNotMatter,
                                     DateTime startDate, DateTime? endDate, string notes)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.ManageCarePlans);
            if (household == null)
                return false;
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
            cmd.Parameters.AddWithValue("@TimingDoesNotMatter", timingDoesNotMatter);
            cmd.Parameters.AddWithValue("@startDate",         startDate);
            cmd.Parameters.AddWithValue("@endDate",           (object?)endDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@notes",             string.IsNullOrEmpty(notes) ? string.Empty : notes);
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, household.HouseholdID, medID, OwnedRecordCommand.Medication);
        }

        public bool DeleteMedication(int userID, int medID)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.ManageCarePlans);
            if (household == null)
                return false;
            using var conn = new SqlConnection(_connStr);
            using var cmd = new SqlCommand("DeleteMedicationByID", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@medID", medID);
            conn.Open();
            return OwnedRecordCommand.Execute(cmd, userID, household.HouseholdID, medID, OwnedRecordCommand.Medication);
        }

        public bool ConfirmSchedule(
            int userID,
            int medID,
            DateTime logDate,
            DateTime confirmedAt,
            int utcOffsetMinutes,
            string notes)
        {
            return RecordDose(
                userID,
                medID,
                logDate,
                MedicationDoseStatuses.Taken,
                confirmedAt,
                utcOffsetMinutes,
                string.Empty,
                notes);
        }

        public bool RecordDose(
            int userID,
            int medID,
            DateTime logDate,
            string status,
            DateTime? administeredAtLocal,
            int utcOffsetMinutes,
            string reason,
            string notes)
        {
            var normalizedStatus = MedicationDoseStatuses.Recordable
                .FirstOrDefault(value => value.Equals(status, StringComparison.OrdinalIgnoreCase));
            if (normalizedStatus == null)
                return false;
            status = normalizedStatus;
            if (status.Equals(MedicationDoseStatuses.Taken, StringComparison.OrdinalIgnoreCase)
                && !administeredAtLocal.HasValue)
                return false;

            var household = GetAuthorizedHousehold(userID, HouseholdPermission.RecordCare);
            if (household == null)
                return false;

            using var conn = new SqlConnection(_connStr);
            conn.Open();
            using var transaction = conn.BeginTransaction();
            if (!OwnsMedication(conn, transaction, userID, household.HouseholdID, medID))
                return false;

            if (status.Equals(MedicationDoseStatuses.Taken, StringComparison.OrdinalIgnoreCase))
            {
                using var confirm = new SqlCommand("ConfirmMedicationSchedule", conn, transaction)
                {
                    CommandType = CommandType.StoredProcedure
                };
                confirm.Parameters.Add("@medID", SqlDbType.Int).Value = medID;
                confirm.Parameters.Add("@logDate", SqlDbType.DateTime2).Value = logDate;
                confirm.Parameters.Add("@confirmedAt", SqlDbType.DateTime2).Value = administeredAtLocal!.Value;
                confirm.ExecuteNonQuery();
            }

            var target = FindSchedule(conn, transaction, medID, logDate);
            if (target.ScheduleID == 0)
                return false;

            var storedStatus = status;
            DateTime? administeredAtUtc = null;
            if (status.Equals(MedicationDoseStatuses.Taken, StringComparison.OrdinalIgnoreCase))
            {
                administeredAtUtc = _timeZone.ToUtc(administeredAtLocal!.Value, utcOffsetMinutes);
                if (!target.TimingDoesNotMatter
                    && administeredAtLocal.Value - logDate > TimeSpan.FromMinutes(_lateAfterMinutes))
                {
                    storedStatus = MedicationDoseStatuses.TakenLate;
                }
            }

            const string updateSql = """
                UPDATE dbo.MedicationSchedule
                SET DoseStatus = @DoseStatus,
                    isConfirmed = @IsConfirmed,
                    confirmedAt = CASE WHEN @IsConfirmed = 1 THEN confirmedAt ELSE NULL END,
                    AdministeredAtUtc = @AdministeredAtUtc,
                    RecordedAtUtc = SYSUTCDATETIME(),
                    RecordedByUserID = @UserID,
                    StatusReason = @StatusReason,
                    AdministrationNotes = @AdministrationNotes
                WHERE scheduleID = @ScheduleID;
                """;
            using var update = new SqlCommand(updateSql, conn, transaction);
            update.Parameters.Add("@DoseStatus", SqlDbType.NVarChar, 20).Value = storedStatus;
            update.Parameters.Add("@IsConfirmed", SqlDbType.Bit).Value =
                storedStatus is MedicationDoseStatuses.Taken or MedicationDoseStatuses.TakenLate;
            update.Parameters.Add("@AdministeredAtUtc", SqlDbType.DateTime2).Value =
                (object?)administeredAtUtc ?? DBNull.Value;
            update.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            update.Parameters.Add("@StatusReason", SqlDbType.NVarChar, 500).Value = Clean(reason);
            update.Parameters.Add("@AdministrationNotes", SqlDbType.NVarChar, 1000).Value = Clean(notes);
            update.Parameters.Add("@ScheduleID", SqlDbType.Int).Value = target.ScheduleID;
            if (update.ExecuteNonQuery() != 1)
                return false;

            transaction.Commit();
            return true;
        }

        public bool UnconfirmSchedule(int userID, int medID, DateTime logDate)
        {
            var household = GetAuthorizedHousehold(userID, HouseholdPermission.RecordCare);
            if (household == null)
                return false;
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            using var transaction = conn.BeginTransaction();
            if (!OwnsMedication(conn, transaction, userID, household.HouseholdID, medID))
                return false;

            var target = FindSchedule(conn, transaction, medID, logDate);
            if (target.ScheduleID == 0)
                return false;

            using var command = new SqlCommand("""
                UPDATE dbo.MedicationSchedule
                SET DoseStatus = N'Due',
                    isConfirmed = 0,
                    confirmedAt = NULL,
                    AdministeredAtUtc = NULL,
                    RecordedAtUtc = NULL,
                    RecordedByUserID = NULL,
                    StatusReason = NULL,
                    AdministrationNotes = NULL
                WHERE scheduleID = @ScheduleID;
                """, conn, transaction);
            command.Parameters.Add("@ScheduleID", SqlDbType.Int).Value = target.ScheduleID;
            if (command.ExecuteNonQuery() != 1)
                return false;

            transaction.Commit();
            return true;
        }

        private string GetEffectiveStatus(MedSchedule schedule, int utcOffsetMinutes)
        {
            if (!schedule.DoseStatus.Equals(MedicationDoseStatuses.Due, StringComparison.OrdinalIgnoreCase))
                return schedule.DoseStatus;

            var missedAtLocal = schedule.TimingDoesNotMatter
                ? schedule.ScheduleDate.Date.AddDays(1).AddMinutes(_missedAfterMinutes)
                : schedule.ScheduleDate.AddMinutes(_missedAfterMinutes);
            return DateTime.UtcNow >= _timeZone.ToUtc(missedAtLocal, utcOffsetMinutes)
                ? MedicationDoseStatuses.Missed
                : MedicationDoseStatuses.Due;
        }

        private static bool OwnsMedication(
            SqlConnection connection,
            SqlTransaction transaction,
            int userID,
            int householdID,
            int medID)
        {
            using var ownership = new SqlCommand(OwnedRecordCommand.Medication, connection, transaction);
            ownership.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            ownership.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
            ownership.Parameters.Add("@RecordID", SqlDbType.Int).Value = medID;
            return ownership.ExecuteScalar() != null;
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

        private static (int ScheduleID, bool TimingDoesNotMatter) FindSchedule(
            SqlConnection connection,
            SqlTransaction transaction,
            int medID,
            DateTime logDate)
        {
            const string sql = """
                SELECT TOP (1) ms.scheduleID, m.TimingDoesNotMatter
                FROM dbo.MedicationSchedule ms WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN dbo.Medications m ON m.medID = ms.medID
                WHERE ms.medID = @MedID
                  AND
                  (
                      (m.TimingDoesNotMatter = 1
                       AND CONVERT(date, ms.scheduleDate) = CONVERT(date, @LogDate))
                      OR
                      (m.TimingDoesNotMatter = 0 AND ms.scheduleDate = @LogDate)
                  )
                ORDER BY CASE WHEN ms.isConfirmed = 1 THEN 0 ELSE 1 END, ms.scheduleID;
                """;
            using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@MedID", SqlDbType.Int).Value = medID;
            command.Parameters.Add("@LogDate", SqlDbType.DateTime2).Value = logDate;
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? (reader.GetInt32(0), reader.GetBoolean(1))
                : (0, false);
        }

        private static DateTime? ReadNullableUtc(SqlDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal)
                ? null
                : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
        }

        private static string Clean(string? value) => value?.Trim() ?? string.Empty;
    }
}
