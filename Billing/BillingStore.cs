using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace PetPotty.Billing;

public interface IBillingStore
{
    Task<HouseholdBilling> ReadAsync(int householdId, CancellationToken ct);
    Task<int?> FindHouseholdAsync(string customerId, CancellationToken ct);
    Task<IBillingLease> LockAsync(int householdId, CancellationToken ct);
}

public interface IBillingLease : IAsyncDisposable
{
    HouseholdBilling State { get; }
    Task<bool> WasProcessedAsync(string eventId, CancellationToken ct);
    Task SaveAsync(StripeEvent? receipt, CancellationToken ct);
}

public sealed class SqlBillingStore(IConfiguration configuration, TimeProvider clock) : IBillingStore
{
    private readonly string connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("DefaultConnection is missing.");

    public async Task<HouseholdBilling> ReadAsync(int householdId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return await ReadAsync(connection, householdId, ct);
    }

    private static async Task<HouseholdBilling> ReadAsync(SqlConnection connection, int householdId, CancellationToken ct)
    {
        using var command = new SqlCommand("SELECT BillingJson FROM dbo.HouseholdBilling WHERE HouseholdID=@id", connection);
        command.Parameters.AddWithValue("@id", householdId);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return json == null ? new() : JsonSerializer.Deserialize<HouseholdBilling>(json)
            ?? throw new InvalidOperationException("Invalid billing state.");
    }

    public async Task<int?> FindHouseholdAsync(string customerId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        using var command = new SqlCommand("SELECT HouseholdID FROM dbo.HouseholdBilling WHERE StripeCustomerID=@id", connection);
        command.Parameters.Add("@id", SqlDbType.VarChar, 255).Value = customerId;
        return await command.ExecuteScalarAsync(ct) as int?;
    }

    public async Task<IBillingLease> LockAsync(int householdId, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            using var command = new SqlCommand("""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource=@key, @LockMode='Exclusive',
                    @LockOwner='Session', @LockTimeout=10000;
                IF @result < 0 THROW 51000, 'Billing is busy. Retry shortly.', 1;
                """, connection);
            command.Parameters.AddWithValue("@key", $"petpotty:billing:{householdId}");
            await command.ExecuteNonQueryAsync(ct);
            return new Lease(connection, householdId, await ReadAsync(connection, householdId, ct), clock);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class Lease(SqlConnection connection, int householdId, HouseholdBilling state, TimeProvider clock) : IBillingLease
    {
        public HouseholdBilling State => state;

        public async Task<bool> WasProcessedAsync(string eventId, CancellationToken ct)
        {
            using var command = new SqlCommand("SELECT 1 FROM dbo.StripeWebhookEvents WHERE EventID=@id", connection);
            command.Parameters.AddWithValue("@id", eventId);
            return await command.ExecuteScalarAsync(ct) != null;
        }

        public async Task SaveAsync(StripeEvent? receipt, CancellationToken ct)
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            var view = state.Resolve(clock.GetUtcNow());
            using var command = new SqlCommand("""
                UPDATE dbo.HouseholdBilling SET StripeCustomerID=@customer, BillingJson=@json,
                    PlanName=@plan, BillingStatus=@status, AccessEndsAtUtc=@ends, RenewsAtUtc=@renews,
                    UpdatedAtUtc=SYSUTCDATETIME() WHERE HouseholdID=@id;
                IF @@ROWCOUNT=0 INSERT dbo.HouseholdBilling
                    (HouseholdID, StripeCustomerID, BillingJson, PlanName, BillingStatus, AccessEndsAtUtc, RenewsAtUtc)
                    VALUES (@id,@customer,@json,@plan,@status,@ends,@renews);
                """, connection, transaction);
            command.Parameters.AddWithValue("@id", householdId);
            command.Parameters.Add("@customer", SqlDbType.VarChar, 255).Value = (object?)state.CustomerId ?? DBNull.Value;
            command.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(state);
            command.Parameters.AddWithValue("@plan", view.Plan.ToString());
            command.Parameters.AddWithValue("@status", view.Status);
            command.Parameters.Add("@ends", SqlDbType.DateTimeOffset).Value = (object?)view.AccessEndsAt ?? DBNull.Value;
            command.Parameters.Add("@renews", SqlDbType.DateTimeOffset).Value = (object?)view.RenewsAt ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(ct);
            if (receipt != null)
            {
                using var insert = new SqlCommand("""
                    INSERT dbo.StripeWebhookEvents(EventID,HouseholdID,EventType,StripeObjectID,EventCreatedAtUtc)
                    VALUES(@event,@id,@type,@object,@created)
                    """, connection, transaction);
                insert.Parameters.AddWithValue("@event", receipt.Id);
                insert.Parameters.AddWithValue("@id", householdId);
                insert.Parameters.AddWithValue("@type", receipt.Type);
                insert.Parameters.AddWithValue("@object", receipt.ObjectId);
                insert.Parameters.AddWithValue("@created", receipt.CreatedAt);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var command = new SqlCommand("EXEC sys.sp_releaseapplock @Resource=@key, @LockOwner='Session'", connection);
                command.Parameters.AddWithValue("@key", $"petpotty:billing:{householdId}");
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally { await connection.DisposeAsync(); }
        }
    }
}
