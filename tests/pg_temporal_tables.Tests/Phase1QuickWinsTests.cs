using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies the Phase-1 "quick-win" functions:
///   temporal.table_stats(regclass)
///   temporal.restore(p_table, p_pk, p_as_of)
///   temporal.restore_deleted(p_table, p_pk)
///   temporal.set_history_compression(p_table, p_method)
/// </summary>
/// <remarks>
/// Each test creates an isolated database via CreateFreshDbWithExtensionAsync().
/// GUC-sensitive tests keep all statements on ONE NpgsqlConnection.
/// </remarks>
public sealed class Phase1QuickWinsTests
{
    private readonly PgTemporalFixture _pg;

    public Phase1QuickWinsTests(PgTemporalFixture pg) => _pg = pg;

    // ---------------------------------------------------------------------------
    // Shared SQL fragments
    // ---------------------------------------------------------------------------

    private const string CreateT = """
        CREATE TABLE t (
            id   int  PRIMARY KEY,
            name text,
            note text
        )
        """;

    private const string EnableT =
        "SELECT temporal.enable('t', combine_interval => interval '0')";

    // ---------------------------------------------------------------------------
    // temporal.table_stats — 1. counts match known writes
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TableStats_CountsMatchKnownWrites()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateT);
        await ExecAsync(conn, EnableT);

        // Two rows inserted
        await ExecAsync(conn, "INSERT INTO t (id, name, note) VALUES (1, 'a', 'n1')");
        await ExecAsync(conn, "INSERT INTO t (id, name, note) VALUES (2, 'b', 'n2')");

        // id=1 updated twice → 2 history rows for id=1
        await ExecAsync(conn, "UPDATE t SET name = 'a2' WHERE id = 1");
        await ExecAsync(conn, "UPDATE t SET name = 'a3' WHERE id = 1");

        // id=2 updated once → 1 history row for id=2
        await ExecAsync(conn, "UPDATE t SET name = 'b2' WHERE id = 2");

        // Expected: base_rows=2, history_rows=3, version_count=5, avg_chain_length=2.50
        var row = await QuerySingleAsync(conn,
            """
            SELECT base_rows,
                   history_rows,
                   version_count,
                   avg_chain_length,
                   history_size_bytes > 0 AS hist_has_size
            FROM   temporal.table_stats('t')
            """,
            r => (
                BaseRows: r.GetInt64(0),
                HistoryRows: r.GetInt64(1),
                VersionCount: r.GetInt64(2),
                AvgChainLength: r.GetDecimal(3),
                HistHasSize: r.GetBoolean(4)
            ));

        Assert.Equal(2L, row.BaseRows);
        Assert.Equal(3L, row.HistoryRows);
        Assert.Equal(5L, row.VersionCount);
        Assert.Equal(2.50m, row.AvgChainLength);
        Assert.True(row.HistHasSize, "history_size_bytes should be > 0");
    }

    // ---------------------------------------------------------------------------
    // temporal.table_stats — 2. rejects untracked table
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TableStats_RejectsUntracked()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync("CREATE TABLE untracked (id int PRIMARY KEY)");

        await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT * FROM temporal.table_stats('untracked')"));
    }

    // ---------------------------------------------------------------------------
    // temporal.restore — 1. rolls row back to as-of state, chain intact
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Restore_RollsRowBackToAsOf_AndChainIntact()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateT);
        await ExecAsync(conn, EnableT);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");

        // Insert original row
        await ExecAsync(conn, "INSERT INTO t (id, name, note) VALUES (1, 'a', 'n1')");

        // Update twice — each update writes a history row (combine_interval='0')
        await ExecAsync(conn, "UPDATE t SET name = 'a2' WHERE id = 1");
        await ExecAsync(conn, "UPDATE t SET note = 'n1b' WHERE id = 1");

        // 2 history rows should exist now
        var histBefore = await ScalarAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(2L, histBefore);

        // The min(valid_from) in history is the timestamp of the original insert
        // (the first UPDATE moved that version to history). Restoring to that point
        // should bring back name='a', note='n1'.
        await ExecAsync(conn,
            """
            SELECT temporal.restore(
                't',
                '{"id": 1}'::jsonb,
                (SELECT min(valid_from) FROM t__history WHERE id = 1)
            )
            """);

        // Current row should reflect the original values
        var (name, note) = await QuerySingleAsync(conn,
            "SELECT name, note FROM t WHERE id = 1",
            r => (r.GetString(0), r.GetString(1)));

        Assert.Equal("a", name);
        Assert.Equal("n1", note);

        // The restore upserted through the triggers, so the prior current version
        // was archived — history row count must have grown to 3
        var histAfter = await ScalarAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(3L, histAfter);
    }

    // ---------------------------------------------------------------------------
    // temporal.restore — 2. rejected while temporal.as_of is set
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Restore_RejectedWhileAsOfSet()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateT);
        await ExecAsync(conn, EnableT);

        await ExecAsync(conn, "INSERT INTO t (id, name, note) VALUES (1, 'a', 'n1')");

        // Activate the as_of GUC — restore must refuse
        await ExecAsync(conn, "SET temporal.as_of = '2999-01-01'");

        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn,
                """
                SELECT temporal.restore(
                    't',
                    '{"id": 1}'::jsonb,
                    now() - interval '1 hour'
                )
                """));
    }

    // ---------------------------------------------------------------------------
    // temporal.restore_deleted — 1. resurrects the last live version
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RestoreDeleted_ResurrectsLastVersion()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateT);
        await ExecAsync(conn, EnableT);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");

        // Build a small history chain for id=2, then delete it
        await ExecAsync(conn, "INSERT INTO t (id, name, note) VALUES (2, 'b', 'n2')");
        await ExecAsync(conn, "UPDATE t SET name = 'b2' WHERE id = 2");
        await ExecAsync(conn, "DELETE FROM t WHERE id = 2");

        // Confirm the row is gone from the base table
        var presentBefore = await ScalarAsync<long>(conn,
            "SELECT count(*) FROM t WHERE id = 2");
        Assert.Equal(0L, presentBefore);

        // Resurrect
        await ExecAsync(conn,
            "SELECT temporal.restore_deleted('t', '{\"id\": 2}'::jsonb)");

        // Row must be back with the last live values (name='b2', note='n2')
        var (name, note) = await QuerySingleAsync(conn,
            "SELECT name, note FROM t WHERE id = 2",
            r => (r.GetString(0), r.GetString(1)));

        Assert.Equal("b2", name);
        Assert.Equal("n2", note);
    }

    // ---------------------------------------------------------------------------
    // temporal.restore_deleted — 2. throws when the row is still present
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RestoreDeleted_ThrowsWhenStillPresent()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateT);
        await ExecAsync(conn, EnableT);

        await ExecAsync(conn, "INSERT INTO t (id, name, note) VALUES (3, 'c', 'n3')");

        // Row is still alive — restore_deleted must refuse
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn,
                "SELECT temporal.restore_deleted('t', '{\"id\": 3}'::jsonb)"));
    }

    // ---------------------------------------------------------------------------
    // temporal.set_history_compression — 1. lz4 sets attcompression = 'l'
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SetHistoryCompression_Lz4_SetsAttcompression()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateT);
        await db.ExecAsync(EnableT);

        await db.ExecAsync("SELECT temporal.set_history_compression('t', 'lz4')");

        // Every varlena column (name, note) in the history table must report 'l'
        var allLz4 = await db.ScalarAsync<bool>(
            """
            SELECT bool_and(attcompression = 'l')
            FROM   pg_attribute
            WHERE  attrelid = 't__history'::regclass
              AND  attname IN ('name', 'note')
            """);

        Assert.True(allLz4, "expected attcompression='l' on all varlena columns of t__history");
    }

    // ---------------------------------------------------------------------------
    // temporal.set_history_compression — 2. rejects an unknown method
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SetHistoryCompression_RejectsBadMethod()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateT);
        await db.ExecAsync(EnableT);

        await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.set_history_compression('t', 'gzip')"));
    }

    // ---------------------------------------------------------------------------
    // Private helpers — single NpgsqlConnection variants
    // ---------------------------------------------------------------------------

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

    private static async Task<T> QuerySingleAsync<T>(
        NpgsqlConnection conn, string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
            throw new InvalidOperationException($"Query returned no rows: {sql}");
        return map(reader);
    }
}
