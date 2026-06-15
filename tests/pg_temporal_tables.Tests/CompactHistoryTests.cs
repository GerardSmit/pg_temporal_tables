using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies temporal.compact_history() and the p_compact parameter of
/// temporal.drop_column(): adjacent, contiguous history versions that are
/// identical on all comparable columns are merged into a single surviving row;
/// rows that differ on any non-managed column are left untouched.
/// </summary>
public sealed class CompactHistoryTests
{
    private readonly PgTemporalFixture _pg;

    public CompactHistoryTests(PgTemporalFixture pg) => _pg = pg;

    // ---------------------------------------------------------------------------
    // Shared SQL fragments
    // ---------------------------------------------------------------------------

    private const string CreateTable = """
        CREATE TABLE t (
            id   int  PRIMARY KEY,
            name text,
            tmp  text
        )
        """;

    private const string EnableZero =
        "SELECT temporal.enable('t', combine_interval => interval '0')";

    // ---------------------------------------------------------------------------
    // 1. Rows that differed only on the dropped column merge after compact_history
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Compact_MergesVersionsThatDifferedOnlyInDroppedColumn()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTable);
        await db.ExecAsync(EnableZero);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'a', 'x')");

        // Three updates that change only tmp — each creates a separate history row
        await ExecOnConnAsync(conn, "UPDATE t SET tmp = 'x1' WHERE id = 1");
        await ExecOnConnAsync(conn, "UPDATE t SET tmp = 'x2' WHERE id = 1");
        await ExecOnConnAsync(conn, "UPDATE t SET tmp = 'x3' WHERE id = 1");

        // 3 history rows, all with name='a'
        var histBefore = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(3L, histBefore);

        // Capture the min valid_from and max valid_to before compaction
        var (minValidFrom, maxValidTo) = await QuerySingleOnConnAsync(conn,
            "SELECT min(valid_from), max(valid_to) FROM t__history WHERE id = 1",
            r => (r.GetDateTime(0), r.GetDateTime(1)));

        // Drop tmp WITHOUT compaction (default p_compact => false)
        await ExecOnConnAsync(conn, "SELECT temporal.drop_column('t', 'tmp')");

        // History count must still be 3 — default does NOT compact
        var histAfterDrop = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(3L, histAfterDrop);

        // Now explicitly compact
        var merged = await ScalarOnConnAsync<long>(conn,
            "SELECT temporal.compact_history('t')");

        // 3 identical rows merge to 1 → 2 removed
        Assert.Equal(2L, merged);

        var histAfterCompact = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(1L, histAfterCompact);

        // The surviving row must cover the full original span
        var (survivingFrom, survivingTo) = await QuerySingleOnConnAsync(conn,
            "SELECT valid_from, valid_to FROM t__history WHERE id = 1",
            r => (r.GetDateTime(0), r.GetDateTime(1)));

        Assert.Equal(minValidFrom, survivingFrom);
        Assert.Equal(maxValidTo, survivingTo);
    }

    // ---------------------------------------------------------------------------
    // 2. Rows that differ on a non-dropped column are NOT merged
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Compact_NoMerge_WhenOtherColumnsDiffer()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTable);
        await db.ExecAsync(EnableZero);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'a', 'x')");

        // Each update changes name — history rows have distinct name values: a, b, c
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'b' WHERE id = 1"); // hist: {a,x}
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'c' WHERE id = 1"); // hist: {b,x}
        await ExecOnConnAsync(conn, "UPDATE t SET tmp  = 'y' WHERE id = 1"); // hist: {c,x}

        var histBefore = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(3L, histBefore);

        // Drop tmp (no compact)
        await ExecOnConnAsync(conn, "SELECT temporal.drop_column('t', 'tmp')");

        // Compact — name values differ (a, b, c) so nothing merges
        var merged = await ScalarOnConnAsync<long>(conn,
            "SELECT temporal.compact_history('t')");

        Assert.Equal(0L, merged);

        var histAfterCompact = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(3L, histAfterCompact);
    }

    // ---------------------------------------------------------------------------
    // 3. drop_column default (p_compact false) does NOT compact;
    //    explicit compact_history call afterward DOES compact
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Compact_DefaultOff_OnDropColumn()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTable);
        await db.ExecAsync(EnableZero);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'a', 'x')");

        // Two updates that change only tmp — 2 history rows, both with name='a'
        await ExecOnConnAsync(conn, "UPDATE t SET tmp = 'x1' WHERE id = 1");
        await ExecOnConnAsync(conn, "UPDATE t SET tmp = 'x2' WHERE id = 1");

        var histBefore = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(2L, histBefore);

        // Drop with default p_compact => false
        await ExecOnConnAsync(conn, "SELECT temporal.drop_column('t', 'tmp')");

        // History must still be 2 — NOT compacted by default
        var histAfterDefaultDrop = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(2L, histAfterDefaultDrop);

        // Now manually compact — the 2 identical rows should merge to 1
        var merged = await ScalarOnConnAsync<long>(conn,
            "SELECT temporal.compact_history('t')");

        Assert.Equal(1L, merged);

        var histFinal = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(1L, histFinal);
    }

    // ---------------------------------------------------------------------------
    // 4. drop_column with p_compact => true merges in one step
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Compact_DropColumnWithCompactTrue_Merges()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTable);
        await db.ExecAsync(EnableZero);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'a', 'x')");

        // Two updates that change only tmp — 2 history rows, both with name='a'
        await ExecOnConnAsync(conn, "UPDATE t SET tmp = 'x1' WHERE id = 1");
        await ExecOnConnAsync(conn, "UPDATE t SET tmp = 'x2' WHERE id = 1");

        var histBefore = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(2L, histBefore);

        // Drop with p_compact => true — should drop column AND compact atomically
        await ExecOnConnAsync(conn, "SELECT temporal.drop_column('t', 'tmp', p_compact => true)");

        // The 2 identical (after drop) rows merge into 1
        var histAfter = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(1L, histAfter);
    }

    // ---------------------------------------------------------------------------
    // 5. compact_history rejects an untracked table
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Compact_RejectsUntracked()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();

        // Plain table with no temporal tracking
        await db.ExecAsync("CREATE TABLE u (id int PRIMARY KEY)");

        await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
            await db.ExecAsync("SELECT temporal.compact_history('u')"));
    }

    // ---------------------------------------------------------------------------
    // 6. compact_history only merges within each primary key; distinct PKs
    //    are processed independently
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Compact_PreservesDistinctRowsAcrossPks()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTable);
        await db.ExecAsync(EnableZero);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'a', 'x')");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (2, 'b', 'y')");

        // id=1: two tmp-only updates → 2 history rows (name='a' both)
        await ExecOnConnAsync(conn, "UPDATE t SET tmp = 'x1' WHERE id = 1");
        await ExecOnConnAsync(conn, "UPDATE t SET tmp = 'x2' WHERE id = 1");

        // id=2: one tmp-only update → 1 history row (name='b')
        await ExecOnConnAsync(conn, "UPDATE t SET tmp = 'y1' WHERE id = 2");

        var hist1Before = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        var hist2Before = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 2");
        Assert.Equal(2L, hist1Before);
        Assert.Equal(1L, hist2Before);

        // Drop tmp (no compact)
        await ExecOnConnAsync(conn, "SELECT temporal.drop_column('t', 'tmp')");

        // Compact — id=1's 2 identical rows merge to 1 (1 removed); id=2's single row stays
        var merged = await ScalarOnConnAsync<long>(conn,
            "SELECT temporal.compact_history('t')");

        Assert.Equal(1L, merged);

        var hist1After = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        var hist2After = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 2");

        Assert.Equal(1L, hist1After);
        Assert.Equal(1L, hist2After);
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

    private static async Task<T> QuerySingleOnConnAsync<T>(
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
