using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PetPotty.Billing;

internal static class SqlIntegration
{
    // Explicitly opt in with --sql. The configured database must have a test-only name.
    public static async Task RunAsync()
    {
        var config = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json").AddJsonFile("appsettings.development.json", optional: true)
            .AddEnvironmentVariables().Build();
        var cs = config.GetConnectionString("DefaultConnection") ?? throw new Exception("No connection string.");
        var parsed = new SqlConnectionStringBuilder(cs);
        if (!parsed.InitialCatalog.EndsWith("_Test", StringComparison.OrdinalIgnoreCase)
            && !parsed.InitialCatalog.EndsWith("_BillingTests", StringComparison.OrdinalIgnoreCase))
            throw new Exception("SQL tests require a database ending in _Test or _BillingTests.");
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        var migration = await File.ReadAllTextAsync("Migrationsss/2026-09-25_AddHouseholdBilling.sql");
        for (var run = 0; run < 2; run++)
            foreach (var batch in Regex.Split(migration, @"^GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch)) await Execute(connection, batch);
        var ids = new List<int>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                using var create = new SqlCommand("""
                    INSERT dbo.Households(PublicID,Name,CreatedByUserID,CreatedAtUtc,UpdatedAtUtc)
                    OUTPUT INSERTED.HouseholdID
                    SELECT TOP (1) NEWID(),N'Billing integration test',userID,SYSUTCDATETIME(),SYSUTCDATETIME()
                    FROM dbo.Users ORDER BY userID;
                    """, connection);
                ids.Add((int)(await create.ExecuteScalarAsync() ?? throw new Exception("Test database needs one user.")));
            }
            var store = new SqlBillingStore(config, TimeProvider.System);
            var now = DateTimeOffset.UtcNow;
            var customer = "cus_billing_test_" + Guid.NewGuid().ToString("N");
            var receipt = new StripeEvent("evt_test_" + Guid.NewGuid().ToString("N"), "invoice.paid", "in_test", customer, now, false);
            Require((await store.ReadAsync(ids[0], default)).Resolve(now).Plan == BillingPlan.Free, "New household must be Free");
            await using (var lease = await store.LockAsync(ids[0], default))
            {
                lease.State.CustomerId = customer;
                lease.State.RecordPayment(new("in_test", BillingPlan.Monthly, "price_test", "sub_test", "pi_test", now, now, now.AddDays(30), 699, "usd"));
                await lease.SaveAsync(receipt, default);
            }
            Require(await store.FindHouseholdAsync(customer, default) == ids[0], "Customer mapping");
            Require((await store.ReadAsync(ids[0], default)).Resolve(now).Plan == BillingPlan.Monthly, "State survives a new connection");
            using (var current = new SqlCommand("SELECT PlanName FROM dbo.HouseholdBillingCurrent WHERE HouseholdID=@id", connection))
            {
                current.Parameters.AddWithValue("@id", ids[0]);
                Require((string?)await current.ExecuteScalarAsync() == "Monthly", "SQL view resolves paid JSON coverage");
            }
            // Receipt collision must roll back the preceding state update in the same transaction.
            await using (var lease = await store.LockAsync(ids[0], default))
            {
                Require(await lease.WasProcessedAsync(receipt.Id, default), "Receipt persisted");
                lease.State.Payments.Clear();
                var failed = false;
                try { await lease.SaveAsync(receipt, default); }
                catch (SqlException e) when (e.Number is 2601 or 2627) { failed = true; }
                Require(failed, "Duplicate receipt rejected");
            }
            Require((await store.ReadAsync(ids[0], default)).Payments.Count == 1, "Receipt conflict must roll back state");
            await using (var lease = await store.LockAsync(ids[1], default))
            {
                lease.State.CustomerId = customer;
                var failed = false;
                try { await lease.SaveAsync(null, default); }
                catch (SqlException e) when (e.Number is 2601 or 2627) { failed = true; }
                Require(failed, "Customer cannot map to two households");
            }
            // Independent connections must see each preceding write after acquiring the same application lock.
            await Task.WhenAll(Enumerable.Range(0, 4).Select(async n =>
            {
                await using var lease = await store.LockAsync(ids[0], default);
                lease.State.Checkouts.Add(new() { PriceId = "concurrency_" + n });
                await lease.SaveAsync(null, default);
            }));
            Require((await store.ReadAsync(ids[0], default)).Checkouts.Count == 4, "No lost updates across concurrent connections");
            await using (var lease = await store.LockAsync(ids[0], default))
            {
                lease.State.Payments.Clear();
                lease.State.RecordPayment(new("in_expired", BillingPlan.Yearly, "price_test", "sub_test", "pi_test", now.AddYears(-2), now.AddYears(-2), now.AddYears(-1), 6999, "usd"));
                await lease.SaveAsync(null, default);
            }
            using var query = new SqlCommand("SELECT PlanName FROM dbo.HouseholdBillingCurrent WHERE HouseholdID=@id", connection);
            query.Parameters.AddWithValue("@id", ids[0]);
            Require((string?)await query.ExecuteScalarAsync() == "Free", "SQL projection must expire paid access");
            // The migration never gives an existing household a paid plan without its own billing record.
            using var free = new SqlCommand("""
                SELECT COUNT(*) FROM dbo.HouseholdBillingCurrent v
                WHERE NOT EXISTS (SELECT 1 FROM dbo.HouseholdBilling b WHERE b.HouseholdID=v.HouseholdID)
                  AND v.PlanName <> 'Free';
                """, connection);
            Require((int)(await free.ExecuteScalarAsync())! == 0, "Legacy households default to Free");
        }
        finally
        {
            // Only fixture IDs created by this invocation are removed; the additive migration remains applied.
            foreach (var id in ids)
            {
                using var cleanup = new SqlCommand("""
                    DELETE dbo.StripeWebhookEvents WHERE HouseholdID=@id;
                    DELETE dbo.HouseholdBilling WHERE HouseholdID=@id;
                    DELETE dbo.Households WHERE HouseholdID=@id AND Name=N'Billing integration test';
                    """, connection);
                cleanup.Parameters.Add("@id", SqlDbType.Int).Value = id;
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
    private static async Task Execute(SqlConnection connection, string sql)
    { using var command = new SqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
