using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies Phase 6 retention/compaction policy management:
/// add_retention_policy, add_compaction_policy, remove_*_policy,
/// run_due_policies, catalog contents, and ON DELETE CASCADE from disable().
/// </summary>
public sealed class PolicyTests
{
    private readonly PgTemporalFixture _pg;

    public PolicyTests(PgTemporalFixture pg) => _pg = pg;

    // ---------------------------------------------------------------------------
    // Shared SQL fragments
    // ---------------------------------------------------------------------------

    private const string CreateTestTable = """
        CREATE TABLE t (
            id   int  PRIMARY KEY,
            name text
        )
        """;

    private const string EnableWithZero =
        "SELECT temporal.enable('t', combine_interval => interval '0')";

    // ---------------------------------------------------------------------------
    // 1. add_retention_policy + add_compaction_policy populate temporal.policies
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AddPolicies_PopulateCatalog()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWithZero);

        await db.ExecAsync(
            "SELECT temporal.add_retention_policy('t', p_drop_after => interval '30 days', p_run_every => interval '1 hour')");
        await db.ExecAsync(
            "SELECT temporal.add_compaction_policy('t', p_run_every => interval '2 hours')");

        var policyCount = await db.ScalarAsync<long>(
            "SELECT count(*) FROM temporal.policies WHERE table_oid = 't'::regclass");
        Assert.Equal(2L, policyCount);

        var retentionOlderThan = await db.ScalarAsync<TimeSpan>(
            "SELECT older_than FROM temporal.policies WHERE table_oid = 't'::regclass AND kind = 'retention'");
        Assert.Equal(TimeSpan.FromDays(30), retentionOlderThan);

        var retentionRunInterval = await db.ScalarAsync<TimeSpan>(
            "SELECT run_interval FROM temporal.policies WHERE table_oid = 't'::regclass AND kind = 'retention'");
        Assert.Equal(TimeSpan.FromHours(1), retentionRunInterval);

        var compactionRunInterval = await db.ScalarAsync<TimeSpan>(
            "SELECT run_interval FROM temporal.policies WHERE table_oid = 't'::regclass AND kind = 'compaction'");
        Assert.Equal(TimeSpan.FromHours(2), compactionRunInterval);
    }

    // ---------------------------------------------------------------------------
    // 2. run_due_policies runs all due policies and stamps last_run
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RunDuePolicies_RunsDueAndStampsLastRun()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWithZero);

        await db.ExecAsync(
            "SELECT temporal.add_compaction_policy('t', p_run_every => interval '0')");
        await db.ExecAsync(
            "SELECT temporal.add_retention_policy('t', p_drop_after => interval '1000 years', p_run_every => interval '0')");

        var runCount = await db.ScalarAsync<int>(
            "SELECT temporal.run_due_policies()");
        Assert.Equal(2, runCount);

        var stampedCount = await db.ScalarAsync<long>(
            "SELECT count(*) FROM temporal.policies WHERE table_oid = 't'::regclass AND last_run IS NOT NULL");
        Assert.Equal(2L, stampedCount);
    }

    // ---------------------------------------------------------------------------
    // 3. run_due_policies respects run_interval (does not re-run prematurely)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RunDuePolicies_RespectsRunInterval()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWithZero);

        await db.ExecAsync(
            "SELECT temporal.add_compaction_policy('t', p_run_every => interval '1 day')");

        // First call: policy is due (last_run IS NULL)
        var firstRun = await db.ScalarAsync<int>(
            "SELECT temporal.run_due_policies()");
        Assert.Equal(1, firstRun);

        // Second call immediately after: not yet due (just ran, interval is 1 day)
        var secondRun = await db.ScalarAsync<int>(
            "SELECT temporal.run_due_policies()");
        Assert.Equal(0, secondRun);
    }

    // ---------------------------------------------------------------------------
    // 4. Retention policy prunes history rows older than the configured cutoff
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RetentionPolicy_PrunesOldHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWithZero);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'a')");
        // UPDATE creates a closed history row with valid_to ~ now()
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'b' WHERE id = 1");

        var histBefore = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history");
        Assert.Equal(1L, histBefore);

        // Retention drop_after = 0: cutoff is now()-0 = now(); the closed row's
        // valid_to is in the past, so it is pruned.
        await ExecOnConnAsync(conn,
            "SELECT temporal.add_retention_policy('t', p_drop_after => interval '0', p_run_every => interval '0')");
        await ExecOnConnAsync(conn, "SELECT temporal.run_due_policies()");

        var histAfter = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history");
        Assert.Equal(0L, histAfter);
    }

    // ---------------------------------------------------------------------------
    // 5. run_due_policies is rejected while temporal.as_of is set
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RunDuePolicies_RejectedWhileAsOfSet()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWithZero);

        await db.ExecAsync(
            "SELECT temporal.add_compaction_policy('t', p_run_every => interval '0')");

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.as_of = '2999-01-01'");

        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecOnConnAsync(conn, "SELECT temporal.run_due_policies()"));
    }

    // ---------------------------------------------------------------------------
    // 6. remove_retention_policy removes only the retention row
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RemovePolicy_DeletesRow()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWithZero);

        await db.ExecAsync(
            "SELECT temporal.add_retention_policy('t', p_drop_after => interval '30 days', p_run_every => interval '1 hour')");
        await db.ExecAsync(
            "SELECT temporal.add_compaction_policy('t', p_run_every => interval '2 hours')");

        await db.ExecAsync("SELECT temporal.remove_retention_policy('t')");

        var remainingCount = await db.ScalarAsync<long>(
            "SELECT count(*) FROM temporal.policies WHERE table_oid = 't'::regclass");
        Assert.Equal(1L, remainingCount);

        var remainingKind = await db.ScalarAsync<string>(
            "SELECT kind FROM temporal.policies WHERE table_oid = 't'::regclass");
        Assert.Equal("compaction", remainingKind);
    }

    // ---------------------------------------------------------------------------
    // 7. temporal.disable() cascades and removes all policy rows (FK ON DELETE CASCADE)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DisableTable_CascadesPolicies()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWithZero);

        await db.ExecAsync(
            "SELECT temporal.add_compaction_policy('t', p_run_every => interval '1 day')");

        var beforeCount = await db.ScalarAsync<long>(
            "SELECT count(*) FROM temporal.policies WHERE table_oid = 't'::regclass");
        Assert.Equal(1L, beforeCount);

        await db.ExecAsync("SELECT temporal.disable('t')");

        var afterCount = await db.ScalarAsync<long>(
            "SELECT count(*) FROM temporal.policies WHERE table_oid = 't'::regclass");
        Assert.Equal(0L, afterCount);
    }

    // ---------------------------------------------------------------------------
    // Private helpers — execute SQL on a specific open connection
    // ---------------------------------------------------------------------------

    private static async Task ExecOnConnAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarOnConnAsync<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (T)result!;
    }
}
