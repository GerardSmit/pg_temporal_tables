using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Covers the <c>history_table</c> parameter of <c>temporal.enable()</c> — the
/// bring-your-own / reuse-existing history table path (also the recovery flow
/// for any manual schema change: disable, alter both tables, then re-enable
/// passing the preserved history table). Exercises the happy path plus the
/// shape-validation error paths (missing base column, missing valid_to, missing
/// deleted_by) and the auto-generated-name collision error.
/// </summary>
public sealed class HistoryTableParamTests
{
    private readonly PgTemporalFixture _pg;

    public HistoryTableParamTests(PgTemporalFixture pg) => _pg = pg;

    private const string CreateTestTable = """
        CREATE TABLE t (
            id         int          PRIMARY KEY,
            name       text,
            email      text,
            updated_at timestamptz
        )
        """;

    // ---------------------------------------------------------------------------
    // 1. Disable then re-enable with history_table reuses the preserved history
    //    table, keeps old history rows, and resumes tracking.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_WithHistoryTable_ReusesPreservedHistoryAfterDisable()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("SELECT temporal.enable('t', combine_interval => interval '0')");

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // Produce one history row before disabling.
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'v0', 'a@example.com', null)");
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v1' WHERE id = 1");
        Assert.Equal(1L, await ScalarOnConnAsync<long>(conn, "SELECT count(*) FROM t__history WHERE id = 1"));

        // Disable: history table and its data are preserved.
        await ExecOnConnAsync(conn, "SELECT temporal.disable('t')");
        Assert.True(await ScalarOnConnAsync<bool>(conn, "SELECT to_regclass('public.t__history') IS NOT NULL"));
        Assert.Equal(1L, await ScalarOnConnAsync<long>(conn, "SELECT count(*) FROM t__history WHERE id = 1"));

        // Re-enable, reusing the preserved history table.
        await ExecOnConnAsync(conn,
            "SELECT temporal.enable('t', combine_interval => interval '0', history_table => 't__history')");

        // Catalog wired to the reused history table.
        Assert.Equal(1L, await ScalarOnConnAsync<long>(conn,
            """
            SELECT count(*)
            FROM   temporal.tracked_tables
            WHERE  table_oid         = 'public.t'::regclass
              AND  history_table_oid = 'public.t__history'::regclass
            """));

        // Old history row survived the cycle.
        Assert.Equal(1L, await ScalarOnConnAsync<long>(conn, "SELECT count(*) FROM t__history WHERE id = 1"));

        // Tracking resumed: a new update appends to the same history table.
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'carol'");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v2' WHERE id = 1");
        Assert.Equal(2L, await ScalarOnConnAsync<long>(conn, "SELECT count(*) FROM t__history WHERE id = 1"));
        Assert.Equal("v2", await ScalarOnConnAsync<string>(conn, "SELECT name FROM t WHERE id = 1"));
    }

    // ---------------------------------------------------------------------------
    // 2. A manually created, correctly shaped history table is accepted.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_WithManuallyShapedHistoryTable_Works()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        // enable() adds valid_from/changed_by to the base table before validating
        // the history table, so the history table must carry those columns too.
        await db.ExecAsync("""
            CREATE TABLE my_history (
                id         int,
                name       text,
                email      text,
                updated_at timestamptz,
                valid_from timestamptz,
                changed_by text,
                valid_to   timestamptz NOT NULL,
                deleted_by text
            )
            """);

        await db.ExecAsync(
            "SELECT temporal.enable('t', combine_interval => interval '0', history_table => 'my_history')");

        Assert.Equal(1L, await db.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM   temporal.tracked_tables
            WHERE  table_oid         = 'public.t'::regclass
              AND  history_table_oid = 'public.my_history'::regclass
            """));

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'v0', 'a@example.com', null)");
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v1' WHERE id = 1");

        // History was written into the supplied table, holding the old value.
        Assert.Equal("v0", await ScalarOnConnAsync<string>(conn,
            "SELECT name FROM my_history WHERE id = 1 ORDER BY valid_to DESC LIMIT 1"));
    }

    // ---------------------------------------------------------------------------
    // 3. Shape validation: history table missing a base column is rejected.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_HistoryTable_MissingBaseColumn_Throws()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        // 'email' is missing.
        await db.ExecAsync("""
            CREATE TABLE my_history (
                id         int,
                name       text,
                updated_at timestamptz,
                valid_from timestamptz,
                changed_by text,
                valid_to   timestamptz NOT NULL,
                deleted_by text
            )
            """);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('t', history_table => 'my_history')"));
        Assert.Contains("missing column", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("email", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 4. Shape validation: history table missing valid_to is rejected.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_HistoryTable_MissingValidTo_Throws()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        // No valid_to column.
        await db.ExecAsync("""
            CREATE TABLE my_history (
                id         int,
                name       text,
                email      text,
                updated_at timestamptz,
                valid_from timestamptz,
                changed_by text,
                deleted_by text
            )
            """);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('t', history_table => 'my_history')"));
        Assert.Contains("valid_to", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 5. Shape validation: history table missing deleted_by is rejected.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_HistoryTable_MissingDeletedBy_Throws()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        // No deleted_by column.
        await db.ExecAsync("""
            CREATE TABLE my_history (
                id         int,
                name       text,
                email      text,
                updated_at timestamptz,
                valid_from timestamptz,
                changed_by text,
                valid_to   timestamptz NOT NULL
            )
            """);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('t', history_table => 'my_history')"));
        Assert.Contains("deleted_by", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 6. Auto-generated history-table name collision points the caller at the
    //    history_table parameter.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_AutoHistoryNameCollision_Throws()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        // Squat on the name enable() would auto-generate.
        await db.ExecAsync("CREATE TABLE t__history (x int)");

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('t')"));
        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("history_table", ex.Message, StringComparison.OrdinalIgnoreCase);
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
