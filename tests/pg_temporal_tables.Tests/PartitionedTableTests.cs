using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Phase 3 — declarative-partitioned base tables. Row triggers fire on the leaf
/// partition; the write path resolves the partitioned root so history is written
/// to the root's history table. AS OF time travel expands partitions inside the
/// union, and temporal.drop_history_before() drops whole sub-cutoff partitions of
/// a partitioned history table.
/// </summary>
/// <remarks>
/// GUC-sensitive tests keep all statements on ONE NpgsqlConnection.
/// </remarks>
public sealed class PartitionedTableTests
{
    private readonly PgTemporalFixture _pg;

    public PartitionedTableTests(PgTemporalFixture pg) => _pg = pg;

    private const string CreatePartitioned = """
        CREATE TABLE ev (
            id      int  NOT NULL,
            region  text NOT NULL,
            payload text,
            PRIMARY KEY (id, region)
        ) PARTITION BY LIST (region);
        CREATE TABLE ev_us PARTITION OF ev FOR VALUES IN ('us');
        CREATE TABLE ev_eu PARTITION OF ev FOR VALUES IN ('eu');
        """;

    // ---------------------------------------------------------------------------
    // 1. enable() on a partitioned root creates a (plain) history table + view
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_PartitionedRoot_CreatesHistoryAndView()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreatePartitioned);
        await db.ExecAsync("SELECT temporal.enable('ev', combine_interval => interval '0')");

        Assert.Equal(1L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM temporal.tracked_tables WHERE table_oid = 'ev'::regclass"));
        Assert.True(await db.ScalarAsync<bool>("SELECT to_regclass('public.ev__history') IS NOT NULL"));
        Assert.True(await db.ScalarAsync<bool>("SELECT to_regclass('public.ev__versions') IS NOT NULL"));
        // auto-created history is a plain table
        Assert.Equal("r", await db.ScalarAsync<string>(
            "SELECT relkind::text FROM pg_class WHERE oid = 'ev__history'::regclass"));
    }

    // ---------------------------------------------------------------------------
    // 2. Writes across partitions produce history (leaf -> root resolution)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Writes_AcrossPartitions_ProduceHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, CreatePartitioned);
        await ExecOnConnAsync(conn, "SELECT temporal.enable('ev', combine_interval => interval '0')");

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO ev VALUES (1, 'us', 'v0'), (2, 'eu', 'w0')");
        // INSERT writes no history
        Assert.Equal(0L, await ScalarOnConnAsync<long>(conn, "SELECT count(*) FROM ev__history"));

        await ExecOnConnAsync(conn, "UPDATE ev SET payload = 'v1' WHERE id = 1 AND region = 'us'");
        await ExecOnConnAsync(conn, "UPDATE ev SET payload = 'w1' WHERE id = 2 AND region = 'eu'");

        // each partition's old row landed in the single root history table
        Assert.Equal(2L, await ScalarOnConnAsync<long>(conn, "SELECT count(*) FROM ev__history"));
        Assert.Equal("v0", await ScalarOnConnAsync<string>(conn,
            "SELECT payload FROM ev__history WHERE id = 1 AND region = 'us'"));
        Assert.Equal("w0", await ScalarOnConnAsync<string>(conn,
            "SELECT payload FROM ev__history WHERE id = 2 AND region = 'eu'"));

        // DELETE on a partition stamps deleted_by
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn, "DELETE FROM ev WHERE id = 2 AND region = 'eu'");
        Assert.Equal("bob", await ScalarOnConnAsync<string>(conn,
            "SELECT deleted_by FROM ev__history WHERE id = 2 AND region = 'eu' AND deleted_by IS NOT NULL"));
    }

    // ---------------------------------------------------------------------------
    // 3. A partition created AFTER enable() still versions (trigger auto-cloned)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NewPartitionAfterEnable_StillVersions()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, CreatePartitioned);
        await ExecOnConnAsync(conn, "SELECT temporal.enable('ev', combine_interval => interval '0')");

        // attach a brand-new partition after tracking started
        await ExecOnConnAsync(conn, "CREATE TABLE ev_ap PARTITION OF ev FOR VALUES IN ('ap')");

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO ev VALUES (3, 'ap', 'p0')");
        await ExecOnConnAsync(conn, "UPDATE ev SET payload = 'p1' WHERE id = 3 AND region = 'ap'");

        Assert.Equal(1L, await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM ev__history WHERE region = 'ap'"));
        Assert.Equal("p0", await ScalarOnConnAsync<string>(conn,
            "SELECT payload FROM ev__history WHERE id = 3 AND region = 'ap'"));
    }

    // ---------------------------------------------------------------------------
    // 4. AS OF time travel over a partitioned table
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AsOf_OverPartitionedTable_TimeTravels()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, CreatePartitioned);
        await ExecOnConnAsync(conn, "SELECT temporal.enable('ev', combine_interval => interval '0')");

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO ev VALUES (1, 'us', 'v0')");
        await ExecOnConnAsync(conn, "UPDATE ev SET payload = 'v1' WHERE id = 1 AND region = 'us'");

        // marker needs a plan-time value: read the historical instant, embed as literal
        var asOf = await ScalarOnConnAsync<string>(conn,
            "SELECT min(valid_from)::text FROM ev__history WHERE id = 1");

        var old = await ScalarOnConnAsync<string>(conn,
            $"SELECT payload FROM ev WHERE temporal.as_of('{asOf}'::timestamptz) AND id = 1 AND region = 'us'");
        Assert.Equal("v0", old);

        // present-day still reads the current value
        var current = await ScalarOnConnAsync<string>(conn,
            "SELECT payload FROM ev WHERE id = 1 AND region = 'us'");
        Assert.Equal("v1", current);

        // the rewrite expands the partitioned base inside an Append/union
        var plan = await ScalarOnConnAsync<string>(conn,
            $"EXPLAIN (COSTS OFF, FORMAT TEXT) SELECT payload FROM ev WHERE temporal.as_of('{asOf}'::timestamptz)");
        Assert.Contains("Append", plan, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 5. drop_history_before() drops whole sub-cutoff partitions of a partitioned
    //    history table, then prunes the remainder
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DropHistoryBefore_PartitionedHistory_DropsOldPartitions()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();

        // bring-your-own RANGE(valid_to) partitioned history table
        await db.ExecAsync("""
            CREATE TABLE big (id int PRIMARY KEY, val text);
            CREATE TABLE big_hist (
                id         int,
                val        text,
                valid_from timestamptz,
                changed_by text,
                valid_to   timestamptz NOT NULL,
                deleted_by text
            ) PARTITION BY RANGE (valid_to);
            CREATE TABLE big_hist_2000 PARTITION OF big_hist FOR VALUES FROM ('2000-01-01') TO ('2001-01-01');
            CREATE TABLE big_hist_now  PARTITION OF big_hist FOR VALUES FROM ('2001-01-01') TO ('2100-01-01');
            SELECT temporal.enable('big', history_table => 'big_hist', combine_interval => interval '0');
            """);

        // current-era history lands in big_hist_now
        await db.ExecAsync("INSERT INTO big VALUES (1, 'a')");
        await db.ExecAsync("UPDATE big SET val = 'b' WHERE id = 1");
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM big_hist_now"));

        // drop everything that ended before 2001 — the 2000 partition is wholly old
        await db.ExecAsync("SELECT temporal.drop_history_before('big', '2001-01-01'::timestamptz)");

        Assert.False(await db.ScalarAsync<bool>("SELECT to_regclass('public.big_hist_2000') IS NOT NULL"));
        Assert.True(await db.ScalarAsync<bool>("SELECT to_regclass('public.big_hist_now') IS NOT NULL"));
        // current-era row untouched
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM big_hist_now"));
    }

    // ---------------------------------------------------------------------------
    // 6. drop_history_before() on a plain history table prunes old rows
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DropHistoryBefore_PlainHistory_PrunesOldRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "CREATE TABLE t (id int PRIMARY KEY, name text)");
        await ExecOnConnAsync(conn, "SELECT temporal.enable('t', combine_interval => interval '0')");

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'a')");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'b' WHERE id = 1");
        Assert.Equal(1L, await ScalarOnConnAsync<long>(conn, "SELECT count(*) FROM t__history"));

        // cutoff in the future: the closed history row (valid_to ~ now) is pruned
        var pruned = await ScalarOnConnAsync<long>(conn,
            "SELECT temporal.drop_history_before('t', now() + interval '1 day')");
        Assert.Equal(1L, pruned);
        Assert.Equal(0L, await ScalarOnConnAsync<long>(conn, "SELECT count(*) FROM t__history"));
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
