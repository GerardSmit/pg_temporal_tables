using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies that pg_temporal_tables history is correctly replicated to a hot
/// standby, that time-travel queries (temporal.as_of) work on the replica, and
/// that write attempts on the standby fail with the expected SQL state.
/// </summary>
/// <remarks>
/// Each test class manages its own <see cref="PgReplicaFixture"/> lifetime so
/// that the cluster is started once per class and torn down afterwards —
/// matching the pattern used by FailoverSlotTests in the reference project.
/// Parallelisation is disabled assembly-wide (PgTemporalCollection.cs), so
/// only one cluster runs at a time.
/// </remarks>
public sealed class StandbyReplicationTests : IAsyncLifetime
{
    private readonly PgReplicaFixture _cluster = new();

    // -------------------------------------------------------------------------
    // IAsyncLifetime — forward to fixture
    // -------------------------------------------------------------------------

    public ValueTask InitializeAsync() => _cluster.InitializeAsync();
    public ValueTask DisposeAsync() => _cluster.DisposeAsync();

    // -------------------------------------------------------------------------
    // 1. History rows written on the primary appear on the standby
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HistoryReplicatesToStandby()
    {
        // --- Primary: set up extension + table + data ---
        await using var primary = await _cluster.OpenPrimaryAsync();

        await ExecAsync(primary, "CREATE EXTENSION IF NOT EXISTS pg_temporal_tables");
        await ExecAsync(primary, """
            CREATE TABLE t (
                id   int  PRIMARY KEY,
                name text
            )
            """);
        await ExecAsync(primary,
            "SELECT temporal.enable('t', combine_interval => interval '0')");

        await ExecAsync(primary, "SET temporal.user_id = 'alice'");
        await ExecAsync(primary, "INSERT INTO t VALUES (1, 'v0')");
        await ExecAsync(primary, "SET temporal.user_id = 'bob'");
        await ExecAsync(primary, "UPDATE t SET name = 'v1' WHERE id = 1");

        // Capture LSN after the writes
        var lsn = await ScalarAsync<string>(primary,
            "SELECT pg_current_wal_lsn()::text");

        // --- Wait for standby to catch up ---
        await _cluster.WaitForStandbyToReachLsnAsync(lsn!);

        // --- Standby: verify one history row inserted by alice ---
        await using var standby = await _cluster.OpenStandbyAsync();

        var histCount = await ScalarAsync<long>(standby,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(1L, histCount);

        var changedBy = await ScalarAsync<string?>(standby,
            "SELECT changed_by FROM t__history WHERE id = 1");
        Assert.Equal("alice", changedBy);
    }

    // -------------------------------------------------------------------------
    // 2. temporal.as_of time-travel works on the hot standby
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AsOfWorksOnHotStandby()
    {
        // --- Primary: set up the table and two versions ---
        await using var primary = await _cluster.OpenPrimaryAsync();

        await ExecAsync(primary, "CREATE EXTENSION IF NOT EXISTS pg_temporal_tables");
        await ExecAsync(primary, """
            CREATE TABLE t (
                id   int  PRIMARY KEY,
                name text
            )
            """);
        await ExecAsync(primary,
            "SELECT temporal.enable('t', combine_interval => interval '0')");

        await ExecAsync(primary, "SET temporal.user_id = 'alice'");
        await ExecAsync(primary, "INSERT INTO t VALUES (1, 'original')");

        // Capture timestamp between the two writes
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var m1 = await ScalarAsync<string>(primary, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(primary, "SET temporal.user_id = 'bob'");
        await ExecAsync(primary, "UPDATE t SET name = 'updated' WHERE id = 1");

        var lsn = await ScalarAsync<string>(primary,
            "SELECT pg_current_wal_lsn()::text");

        // --- Wait for standby ---
        await _cluster.WaitForStandbyToReachLsnAsync(lsn!);

        // --- Standby: time-travel to m1 must return the old value ---
        await using var standby = await _cluster.OpenStandbyAsync();

        // Confirm this is a replica
        var inRecovery = await ScalarAsync<bool>(standby,
            "SELECT pg_is_in_recovery()");
        Assert.True(inRecovery, "standby must be in recovery (hot standby)");

        // Set as_of and query
        await using var cmd = standby.CreateCommand();
        cmd.CommandText = "SELECT set_config('temporal.as_of', $1, false)";
        cmd.Parameters.AddWithValue(m1!);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var historicName = await ScalarAsync<string?>(standby,
            "SELECT name FROM t WHERE id = 1");
        Assert.Equal("original", historicName);

        // Confirm present value (after RESET)
        await ExecAsync(standby, "RESET temporal.as_of");
        var currentName = await ScalarAsync<string?>(standby,
            "SELECT name FROM t WHERE id = 1");
        Assert.Equal("updated", currentName);
    }

    // -------------------------------------------------------------------------
    // 3. Write on standby is rejected (SqlState 25006 — read_only_sql_transaction)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WritesOnStandby_Fail()
    {
        // --- Primary: create and populate the table ---
        await using var primary = await _cluster.OpenPrimaryAsync();

        await ExecAsync(primary, "CREATE EXTENSION IF NOT EXISTS pg_temporal_tables");
        await ExecAsync(primary, """
            CREATE TABLE t (
                id   int  PRIMARY KEY,
                name text
            )
            """);
        await ExecAsync(primary,
            "SELECT temporal.enable('t', combine_interval => interval '0')");

        await ExecAsync(primary, "SET temporal.user_id = 'alice'");
        await ExecAsync(primary, "INSERT INTO t VALUES (1, 'v0')");

        var lsn = await ScalarAsync<string>(primary,
            "SELECT pg_current_wal_lsn()::text");

        await _cluster.WaitForStandbyToReachLsnAsync(lsn!);

        // --- Standby: UPDATE must fail ---
        await using var standby = await _cluster.OpenStandbyAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(standby, "UPDATE t SET name = 'x' WHERE id = 1"));

        Assert.Equal("25006", ex.SqlState);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        if (result is null or DBNull)
            return default;
        return (T)result;
    }
}
