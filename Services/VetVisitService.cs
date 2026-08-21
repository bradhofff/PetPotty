using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services
{
    public sealed class VetVisitService : IVetVisitService
    {
        private readonly string _connectionString;

        public VetVisitService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        }

        public List<VetVisit> GetVisits(int userID, int? petID = null)
        {
            using var connection = OpenConnection();
            RefreshReminderStatuses(connection, userID);

            const string sql = """
                SELECT v.VetVisitID, v.PetID, p.name AS PetName, v.VisitDate, v.VisitTime,
                       v.IsAllDay, v.ClinicName, v.VeterinarianName, v.VisitReason,
                       v.VisitType, v.Location, v.PhoneNumber, v.Status, v.Notes,
                       v.FollowUpDate, v.Cost, v.IsEmergency, v.PreparationInstructions,
                       v.VisitSummary, v.Diagnosis, v.TreatmentProvided,
                       v.VaccinationsReceived, v.Prescriptions, v.FollowUpInstructions,
                       v.CreatedAt, v.UpdatedAt, r.VetVisitReminderID AS ReminderID,
                       r.ReminderAt, r.Status AS ReminderStatus
                FROM dbo.VetVisits v
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                LEFT JOIN dbo.VetVisitReminders r ON r.VetVisitID = v.VetVisitID
                WHERE p.userID = @UserID
                  AND v.IsDeleted = 0
                  AND (@PetID IS NULL OR v.PetID = @PetID)
                ORDER BY v.VisitDate DESC,
                         CASE WHEN v.VisitTime IS NULL THEN 1 ELSE 0 END,
                         v.VisitTime DESC, v.VetVisitID DESC;
                """;

            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            command.Parameters.Add("@PetID", SqlDbType.Int).Value = (object?)petID ?? DBNull.Value;
            using var reader = command.ExecuteReader();

            var visits = new List<VetVisit>();
            while (reader.Read())
                visits.Add(MapVisit(reader));
            return visits;
        }

        public VetVisit? GetVisit(int userID, int vetVisitID)
        {
            using var connection = OpenConnection();
            const string sql = """
                SELECT v.VetVisitID, v.PetID, p.name AS PetName, v.VisitDate, v.VisitTime,
                       v.IsAllDay, v.ClinicName, v.VeterinarianName, v.VisitReason,
                       v.VisitType, v.Location, v.PhoneNumber, v.Status, v.Notes,
                       v.FollowUpDate, v.Cost, v.IsEmergency, v.PreparationInstructions,
                       v.VisitSummary, v.Diagnosis, v.TreatmentProvided,
                       v.VaccinationsReceived, v.Prescriptions, v.FollowUpInstructions,
                       v.CreatedAt, v.UpdatedAt, r.VetVisitReminderID AS ReminderID,
                       r.ReminderAt, r.Status AS ReminderStatus
                FROM dbo.VetVisits v
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                LEFT JOIN dbo.VetVisitReminders r ON r.VetVisitID = v.VetVisitID
                WHERE p.userID = @UserID AND v.VetVisitID = @VetVisitID AND v.IsDeleted = 0;
                """;

            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            command.Parameters.Add("@VetVisitID", SqlDbType.Int).Value = vetVisitID;
            using var reader = command.ExecuteReader();
            return reader.Read() ? MapVisit(reader) : null;
        }

        public int AddVisit(int userID, VetVisitInput input, DateTime? reminderAt)
        {
            using var connection = OpenConnection();
            using var command = new SqlCommand("AddVetVisit", connection)
            {
                CommandType = CommandType.StoredProcedure
            };
            AddVisitParameters(command, input);
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            command.Parameters.Add("@ReminderAt", SqlDbType.DateTime2).Value =
                (object?)reminderAt ?? DBNull.Value;
            var result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        public bool UpdateVisit(int userID, VetVisitInput input, DateTime? reminderAt)
        {
            using var connection = OpenConnection();
            using var command = new SqlCommand("UpdateVetVisit", connection)
            {
                CommandType = CommandType.StoredProcedure
            };
            AddVisitParameters(command, input);
            command.Parameters.Add("@VetVisitID", SqlDbType.Int).Value = input.VetVisitID;
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            command.Parameters.Add("@ReminderAt", SqlDbType.DateTime2).Value =
                (object?)reminderAt ?? DBNull.Value;
            return Convert.ToBoolean(command.ExecuteScalar());
        }

        public bool ChangeStatus(int userID, int vetVisitID, string status, string details)
        {
            using var connection = OpenConnection();
            using var command = new SqlCommand("ChangeVetVisitStatus", connection)
            {
                CommandType = CommandType.StoredProcedure
            };
            command.Parameters.Add("@Status", SqlDbType.NVarChar, 25).Value = status;
            command.Parameters.Add("@VetVisitID", SqlDbType.Int).Value = vetVisitID;
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            command.Parameters.Add("@Details", SqlDbType.NVarChar, 1000).Value = Clean(details);
            return Convert.ToBoolean(command.ExecuteScalar());
        }

        public bool CompleteVisit(int userID, CompleteVetVisitInput input)
        {
            using var connection = OpenConnection();
            using var command = new SqlCommand("CompleteVetVisit", connection)
            {
                CommandType = CommandType.StoredProcedure
            };
            command.Parameters.Add("@VisitSummary", SqlDbType.NVarChar, 4000).Value = Clean(input.VisitSummary);
            command.Parameters.Add("@Diagnosis", SqlDbType.NVarChar, 2000).Value = Clean(input.Diagnosis);
            command.Parameters.Add("@TreatmentProvided", SqlDbType.NVarChar, 4000).Value = Clean(input.TreatmentProvided);
            command.Parameters.Add("@VaccinationsReceived", SqlDbType.NVarChar, 2000).Value = Clean(input.VaccinationsReceived);
            command.Parameters.Add("@Prescriptions", SqlDbType.NVarChar, 2000).Value = Clean(input.Prescriptions);
            command.Parameters.Add("@FollowUpInstructions", SqlDbType.NVarChar, 4000).Value = Clean(input.FollowUpInstructions);
            command.Parameters.Add("@FollowUpDate", SqlDbType.Date).Value = (object?)input.FollowUpDate?.Date ?? DBNull.Value;
            var cost = command.Parameters.Add("@FinalCost", SqlDbType.Decimal);
            cost.Precision = 10;
            cost.Scale = 2;
            cost.Value = (object?)input.FinalCost ?? DBNull.Value;
            command.Parameters.Add("@AdditionalNotes", SqlDbType.NVarChar, 4000).Value = Clean(input.AdditionalNotes);
            command.Parameters.Add("@VetVisitID", SqlDbType.Int).Value = input.VetVisitID;
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            return Convert.ToBoolean(command.ExecuteScalar());
        }

        public bool DeleteVisit(int userID, int vetVisitID)
        {
            using var connection = OpenConnection();
            using var command = new SqlCommand("DeleteVetVisit", connection)
            {
                CommandType = CommandType.StoredProcedure
            };
            command.Parameters.Add("@VetVisitID", SqlDbType.Int).Value = vetVisitID;
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            return Convert.ToBoolean(command.ExecuteScalar());
        }

        public bool DismissReminder(int userID, int reminderID)
        {
            using var connection = OpenConnection();
            const string sql = """
                UPDATE r SET Status = N'Dismissed', DismissedAt = SYSDATETIME()
                FROM dbo.VetVisitReminders r
                INNER JOIN dbo.VetVisits v ON v.VetVisitID = r.VetVisitID
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                WHERE r.VetVisitReminderID = @ReminderID AND p.userID = @UserID
                  AND r.Status IN (N'Pending', N'Displayed');
                """;
            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@ReminderID", SqlDbType.Int).Value = reminderID;
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            return command.ExecuteNonQuery() == 1;
        }

        public List<VetVisitDocument> GetDocuments(int userID, int vetVisitID)
        {
            using var connection = OpenConnection();
            const string sql = """
                SELECT d.VetVisitDocumentID, d.VetVisitID, d.DocumentType, d.DisplayName,
                       d.OriginalFileName, d.StoredPath, d.ContentType, d.FileSizeBytes,
                       d.Description, d.CreatedAt
                FROM dbo.VetVisitDocuments d
                INNER JOIN dbo.VetVisits v ON v.VetVisitID = d.VetVisitID
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                WHERE d.VetVisitID = @VetVisitID AND p.userID = @UserID AND v.IsDeleted = 0
                ORDER BY d.CreatedAt DESC;
                """;
            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@VetVisitID", SqlDbType.Int).Value = vetVisitID;
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            using var reader = command.ExecuteReader();
            var documents = new List<VetVisitDocument>();
            while (reader.Read())
                documents.Add(MapDocument(reader));
            return documents;
        }

        public VetVisitDocument? GetDocument(int userID, int documentID)
        {
            using var connection = OpenConnection();
            const string sql = """
                SELECT d.VetVisitDocumentID, d.VetVisitID, d.DocumentType, d.DisplayName,
                       d.OriginalFileName, d.StoredPath, d.ContentType, d.FileSizeBytes,
                       d.Description, d.CreatedAt
                FROM dbo.VetVisitDocuments d
                INNER JOIN dbo.VetVisits v ON v.VetVisitID = d.VetVisitID
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                WHERE d.VetVisitDocumentID = @DocumentID AND p.userID = @UserID
                  AND v.IsDeleted = 0;
                """;
            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@DocumentID", SqlDbType.Int).Value = documentID;
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            using var reader = command.ExecuteReader();
            return reader.Read() ? MapDocument(reader) : null;
        }

        public int AddDocument(int userID, VetVisitDocument document)
        {
            using var connection = OpenConnection();
            const string sql = """
                INSERT dbo.VetVisitDocuments
                    (VetVisitID, DocumentType, DisplayName, OriginalFileName, StoredPath,
                     ContentType, FileSizeBytes, Description, CreatedAt)
                OUTPUT INSERTED.VetVisitDocumentID
                SELECT @VetVisitID, @DocumentType, @DisplayName, @OriginalFileName, @StoredPath,
                       @ContentType, @FileSizeBytes, @Description, SYSDATETIME()
                WHERE EXISTS
                    (SELECT 1
                     FROM dbo.VetVisits v
                     INNER JOIN dbo.Pets p ON p.petID = v.PetID
                     WHERE v.VetVisitID = @VetVisitID AND p.userID = @UserID AND v.IsDeleted = 0);
                """;
            using var command = new SqlCommand(sql, connection);
            AddDocumentParameters(command, userID, document);
            var result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        public bool UpdateDocument(int userID, int documentID, string documentType, string displayName, string description)
        {
            using var connection = OpenConnection();
            const string sql = """
                UPDATE d
                SET DocumentType = @DocumentType, DisplayName = @DisplayName,
                    Description = @Description
                FROM dbo.VetVisitDocuments d
                INNER JOIN dbo.VetVisits v ON v.VetVisitID = d.VetVisitID
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                WHERE d.VetVisitDocumentID = @DocumentID AND p.userID = @UserID
                  AND v.IsDeleted = 0;
                """;
            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@DocumentType", SqlDbType.NVarChar, 50).Value = Clean(documentType);
            command.Parameters.Add("@DisplayName", SqlDbType.NVarChar, 260).Value = Clean(displayName);
            command.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value = Clean(description);
            command.Parameters.Add("@DocumentID", SqlDbType.Int).Value = documentID;
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            return command.ExecuteNonQuery() == 1;
        }

        public VetVisitDocument? DeleteDocument(int userID, int documentID)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            const string selectSql = """
                SELECT d.VetVisitDocumentID, d.VetVisitID, d.DocumentType, d.DisplayName,
                       d.OriginalFileName, d.StoredPath, d.ContentType, d.FileSizeBytes,
                       d.Description, d.CreatedAt
                FROM dbo.VetVisitDocuments d
                INNER JOIN dbo.VetVisits v ON v.VetVisitID = d.VetVisitID
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                WHERE d.VetVisitDocumentID = @DocumentID AND p.userID = @UserID
                  AND v.IsDeleted = 0;
                """;
            VetVisitDocument? document;
            using (var select = new SqlCommand(selectSql, connection, transaction))
            {
                select.Parameters.Add("@DocumentID", SqlDbType.Int).Value = documentID;
                select.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
                using var reader = select.ExecuteReader();
                document = reader.Read() ? MapDocument(reader) : null;
            }
            if (document == null)
                return null;

            using var delete = new SqlCommand(
                "DELETE dbo.VetVisitDocuments WHERE VetVisitDocumentID = @DocumentID;",
                connection, transaction);
            delete.Parameters.Add("@DocumentID", SqlDbType.Int).Value = documentID;
            delete.ExecuteNonQuery();
            AddHistory(connection, transaction, document.VetVisitID, "Document removed", null, null,
                $"Document '{document.DisplayName}' removed.");
            transaction.Commit();
            return document;
        }

        public List<VetVisitHistory> GetHistory(int userID, int vetVisitID)
        {
            using var connection = OpenConnection();
            const string sql = """
                SELECT h.VetVisitHistoryID, h.VetVisitID, h.ChangeType, h.OldStatus,
                       h.NewStatus, h.Details, h.ChangedAt
                FROM dbo.VetVisitHistory h
                INNER JOIN dbo.VetVisits v ON v.VetVisitID = h.VetVisitID
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                WHERE h.VetVisitID = @VetVisitID AND p.userID = @UserID
                ORDER BY h.ChangedAt DESC, h.VetVisitHistoryID DESC;
                """;
            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@VetVisitID", SqlDbType.Int).Value = vetVisitID;
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            using var reader = command.ExecuteReader();
            var history = new List<VetVisitHistory>();
            while (reader.Read())
            {
                history.Add(new VetVisitHistory
                {
                    VetVisitHistoryID = reader.GetInt32(reader.GetOrdinal("VetVisitHistoryID")),
                    VetVisitID = reader.GetInt32(reader.GetOrdinal("VetVisitID")),
                    ChangeType = GetString(reader, "ChangeType"),
                    OldStatus = GetNullableString(reader, "OldStatus"),
                    NewStatus = GetNullableString(reader, "NewStatus"),
                    Details = GetString(reader, "Details"),
                    ChangedAt = reader.GetDateTime(reader.GetOrdinal("ChangedAt"))
                });
            }
            return history;
        }

        public List<DashboardCareItem> GetDashboardVisits(int userID, DateTime startDate, DateTime endDate)
        {
            using var connection = OpenConnection();
            const string sql = """
                SELECT v.VetVisitID, v.PetID, v.VisitDate, v.VisitTime, v.IsAllDay,
                       v.VisitReason, v.ClinicName
                FROM dbo.VetVisits v
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                WHERE p.userID = @UserID AND v.IsDeleted = 0
                  AND v.Status IN (N'Scheduled', N'Confirmed', N'Rescheduled')
                  AND v.VisitDate >= @StartDate AND v.VisitDate < @EndDate
                ORDER BY v.VisitDate, CASE WHEN v.VisitTime IS NULL THEN 1 ELSE 0 END, v.VisitTime;
                """;
            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            command.Parameters.Add("@StartDate", SqlDbType.Date).Value = startDate.Date;
            command.Parameters.Add("@EndDate", SqlDbType.Date).Value = endDate.Date;
            using var reader = command.ExecuteReader();

            var items = new List<DashboardCareItem>();
            while (reader.Read())
            {
                var visitDate = reader.GetDateTime(reader.GetOrdinal("VisitDate")).Date;
                var visitTime = reader.IsDBNull(reader.GetOrdinal("VisitTime"))
                    ? (TimeSpan?)null
                    : reader.GetTimeSpan(reader.GetOrdinal("VisitTime"));
                var allDay = reader.GetBoolean(reader.GetOrdinal("IsAllDay"));
                var dueAt = visitDate + (visitTime ?? TimeSpan.FromHours(9));
                if (!allDay && visitTime.HasValue && dueAt < DateTime.Now)
                    continue;
                var reason = GetString(reader, "VisitReason");
                items.Add(new DashboardCareItem
                {
                    PetID = reader.GetInt32(reader.GetOrdinal("PetID")),
                    DueAt = dueAt,
                    Kind = "VetVisit",
                    Text = allDay ? $"Vet visit (all day) — {reason}" : $"Vet visit — {reason}",
                    Url = "/VetVisits",
                    IsOverdue = !allDay && dueAt < DateTime.Now
                });
            }
            return items;
        }

        public List<string> GetDocumentPathsByPet(int userID, int petID)
        {
            using var connection = OpenConnection();
            const string sql = """
                SELECT d.StoredPath
                FROM dbo.VetVisitDocuments d
                INNER JOIN dbo.VetVisits v ON v.VetVisitID = d.VetVisitID
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                WHERE v.PetID = @PetID AND p.userID = @UserID;
                """;
            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@PetID", SqlDbType.Int).Value = petID;
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            using var reader = command.ExecuteReader();
            var paths = new List<string>();
            while (reader.Read())
                paths.Add(reader.GetString(0));
            return paths;
        }

        private SqlConnection OpenConnection()
        {
            var connection = new SqlConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private static void AddVisitParameters(SqlCommand command, VetVisitInput input)
        {
            command.Parameters.Add("@PetID", SqlDbType.Int).Value = input.PetID;
            command.Parameters.Add("@VisitDate", SqlDbType.Date).Value = input.VisitDate.Date;
            command.Parameters.Add("@VisitTime", SqlDbType.Time).Value =
                input.IsAllDay ? DBNull.Value : (object?)input.VisitTime ?? DBNull.Value;
            command.Parameters.Add("@IsAllDay", SqlDbType.Bit).Value = input.IsAllDay;
            command.Parameters.Add("@ClinicName", SqlDbType.NVarChar, 200).Value = Clean(input.ClinicName);
            command.Parameters.Add("@VeterinarianName", SqlDbType.NVarChar, 150).Value = Clean(input.VeterinarianName);
            command.Parameters.Add("@VisitReason", SqlDbType.NVarChar, 500).Value = Clean(input.VisitReason);
            command.Parameters.Add("@VisitType", SqlDbType.NVarChar, 50).Value = Clean(input.VisitType);
            command.Parameters.Add("@Status", SqlDbType.NVarChar, 25).Value = Clean(input.Status);
            command.Parameters.Add("@Notes", SqlDbType.NVarChar, -1).Value = Clean(input.Notes);
            command.Parameters.Add("@FollowUpDate", SqlDbType.Date).Value = (object?)input.FollowUpDate?.Date ?? DBNull.Value;
            var cost = command.Parameters.Add("@Cost", SqlDbType.Decimal);
            cost.Precision = 10;
            cost.Scale = 2;
            cost.Value = (object?)input.Cost ?? DBNull.Value;
            command.Parameters.Add("@IsEmergency", SqlDbType.Bit).Value = input.IsEmergency;
        }

        private static void AddDocumentParameters(SqlCommand command, int userID, VetVisitDocument document)
        {
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            command.Parameters.Add("@VetVisitID", SqlDbType.Int).Value = document.VetVisitID;
            command.Parameters.Add("@DocumentType", SqlDbType.NVarChar, 50).Value = Clean(document.DocumentType);
            command.Parameters.Add("@DisplayName", SqlDbType.NVarChar, 260).Value = Clean(document.DisplayName);
            command.Parameters.Add("@OriginalFileName", SqlDbType.NVarChar, 260).Value = Clean(document.OriginalFileName);
            command.Parameters.Add("@StoredPath", SqlDbType.NVarChar, 500).Value = Clean(document.StoredPath);
            command.Parameters.Add("@ContentType", SqlDbType.NVarChar, 150).Value = Clean(document.ContentType);
            command.Parameters.Add("@FileSizeBytes", SqlDbType.BigInt).Value = document.FileSizeBytes;
            command.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value = Clean(document.Description);
        }

        private static void RefreshReminderStatuses(SqlConnection connection, int userID)
        {
            const string sql = """
                UPDATE r
                SET Status = CASE
                    WHEN v.Status NOT IN (N'Scheduled', N'Confirmed', N'Rescheduled') THEN N'Cancelled'
                    WHEN v.VisitDate < CAST(SYSDATETIME() AS date)
                      OR (v.VisitDate = CAST(SYSDATETIME() AS date)
                          AND v.VisitTime IS NOT NULL
                          AND DATEADD(SECOND,
                              DATEDIFF(SECOND, CAST(N'00:00:00' AS time), v.VisitTime),
                              CAST(v.VisitDate AS datetime2)) < SYSDATETIME())
                    THEN N'Expired'
                    WHEN r.Status = N'Pending' AND r.ReminderAt <= SYSDATETIME() THEN N'Displayed'
                    ELSE r.Status END,
                    DisplayedAt = CASE
                        WHEN r.Status = N'Pending' AND r.ReminderAt <= SYSDATETIME()
                        THEN COALESCE(r.DisplayedAt, SYSDATETIME()) ELSE r.DisplayedAt END,
                    UpdatedAt = SYSDATETIME()
                FROM dbo.VetVisitReminders r
                INNER JOIN dbo.VetVisits v ON v.VetVisitID = r.VetVisitID
                INNER JOIN dbo.Pets p ON p.petID = v.PetID
                WHERE p.userID = @UserID
                  AND r.Status IN (N'Pending', N'Displayed');
                """;
            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            command.ExecuteNonQuery();
        }

        private static void AddHistory(
            SqlConnection connection, SqlTransaction transaction, int visitID,
            string changeType, string? oldStatus, string? newStatus, string details)
        {
            const string sql = """
                INSERT dbo.VetVisitHistory
                    (VetVisitID, ChangeType, OldStatus, NewStatus, Details, ChangedAt)
                VALUES
                    (@VetVisitID, @ChangeType, @OldStatus, @NewStatus, @Details, SYSDATETIME());
                """;
            using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@VetVisitID", SqlDbType.Int).Value = visitID;
            command.Parameters.Add("@ChangeType", SqlDbType.NVarChar, 60).Value = changeType;
            command.Parameters.Add("@OldStatus", SqlDbType.NVarChar, 25).Value = (object?)oldStatus ?? DBNull.Value;
            command.Parameters.Add("@NewStatus", SqlDbType.NVarChar, 25).Value = (object?)newStatus ?? DBNull.Value;
            command.Parameters.Add("@Details", SqlDbType.NVarChar, 1000).Value = details;
            command.ExecuteNonQuery();
        }

        private static VetVisit MapVisit(SqlDataReader reader)
        {
            return new VetVisit
            {
                VetVisitID = reader.GetInt32(reader.GetOrdinal("VetVisitID")),
                PetID = reader.GetInt32(reader.GetOrdinal("PetID")),
                PetName = GetString(reader, "PetName"),
                VisitDate = reader.GetDateTime(reader.GetOrdinal("VisitDate")),
                VisitTime = reader.IsDBNull(reader.GetOrdinal("VisitTime"))
                    ? null
                    : reader.GetTimeSpan(reader.GetOrdinal("VisitTime")),
                IsAllDay = reader.GetBoolean(reader.GetOrdinal("IsAllDay")),
                ClinicName = GetString(reader, "ClinicName"),
                VeterinarianName = GetString(reader, "VeterinarianName"),
                VisitReason = GetString(reader, "VisitReason"),
                VisitType = GetString(reader, "VisitType"),
                Location = GetString(reader, "Location"),
                PhoneNumber = GetString(reader, "PhoneNumber"),
                Status = GetString(reader, "Status"),
                Notes = CombinePreparationAndNotes(
                    GetString(reader, "PreparationInstructions"),
                    GetString(reader, "Notes")),
                FollowUpDate = GetNullableDateTime(reader, "FollowUpDate"),
                Cost = GetNullableDecimal(reader, "Cost"),
                IsEmergency = reader.GetBoolean(reader.GetOrdinal("IsEmergency")),
                PreparationInstructions = string.Empty,
                VisitSummary = GetString(reader, "VisitSummary"),
                Diagnosis = GetString(reader, "Diagnosis"),
                TreatmentProvided = GetString(reader, "TreatmentProvided"),
                VaccinationsReceived = GetString(reader, "VaccinationsReceived"),
                Prescriptions = GetString(reader, "Prescriptions"),
                FollowUpInstructions = GetString(reader, "FollowUpInstructions"),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("UpdatedAt")),
                ReminderID = reader.IsDBNull(reader.GetOrdinal("ReminderID"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("ReminderID")),
                ReminderAt = GetNullableDateTime(reader, "ReminderAt"),
                ReminderStatus = GetNullableString(reader, "ReminderStatus")
            };
        }

        private static VetVisitDocument MapDocument(SqlDataReader reader)
        {
            return new VetVisitDocument
            {
                VetVisitDocumentID = reader.GetInt32(reader.GetOrdinal("VetVisitDocumentID")),
                VetVisitID = reader.GetInt32(reader.GetOrdinal("VetVisitID")),
                DocumentType = GetString(reader, "DocumentType"),
                DisplayName = GetString(reader, "DisplayName"),
                OriginalFileName = GetString(reader, "OriginalFileName"),
                StoredPath = GetString(reader, "StoredPath"),
                ContentType = GetString(reader, "ContentType"),
                FileSizeBytes = reader.GetInt64(reader.GetOrdinal("FileSizeBytes")),
                Description = GetString(reader, "Description"),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt"))
            };
        }

        private static string CombinePreparationAndNotes(string preparation, string notes)
        {
            if (string.IsNullOrWhiteSpace(preparation))
                return notes;
            if (string.IsNullOrWhiteSpace(notes))
                return preparation;
            return $"{preparation.Trim()}\r\n\r\n{notes.Trim()}";
        }

        private static string Clean(string? value) => value?.Trim() ?? string.Empty;
        private static string GetString(SqlDataReader reader, string name) =>
            reader.IsDBNull(reader.GetOrdinal(name)) ? string.Empty : reader.GetString(reader.GetOrdinal(name));
        private static string? GetNullableString(SqlDataReader reader, string name) =>
            reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
        private static DateTime? GetNullableDateTime(SqlDataReader reader, string name) =>
            reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetDateTime(reader.GetOrdinal(name));
        private static decimal? GetNullableDecimal(SqlDataReader reader, string name) =>
            reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetDecimal(reader.GetOrdinal(name));
    }
}
