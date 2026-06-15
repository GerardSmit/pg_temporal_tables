using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies the temporal.as_of GUC rewrite semantics: transparent time-travel
/// through plain SELECTs, joins, views, CTEs, sublinks; DML and COPY blocking;
/// behaviour before the enable time; and prepared-statement plan-cache reset.
/// </summary>
/// <remarks>
/// GUCs are session state.  Every test that depends on SET/RESET must keep ALL
/// statements on ONE NpgsqlConnection opened from db.DataSource — the same
/// pattern used in <see cref="CombineBucketTests"/>.
/// </remarks>
public sealed class AsOfRewriteTests
{
    private readonly PgTemporalFixture _pg;

    public AsOfRewriteTests(PgTemporalFixture pg) => _pg = pg;

    // ---------------------------------------------------------------------------
    // Shared SQL — standard two-table setup used by most tests
    // ---------------------------------------------------------------------------

    private const string CreateRoles = """
        CREATE TABLE roles (
            id   int  PRIMARY KEY,
            name text
        )
        """;

    private const string CreateUsers = """
        CREATE TABLE users (
            id      int  PRIMARY KEY,
            name    text,
            role_id int
        )
        """;

    private const string EnableRoles =
        "SELECT temporal.enable('roles', combine_interval => interval '0')";

    private const string EnableUsers =
        "SELECT temporal.enable('users', combine_interval => interval '0')";

    // ---------------------------------------------------------------------------
    // Shared helper — build the standard dataset and return the m1 timestamp.
    //
    // All statements run on the SAME connection so GUCs survive across calls.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// On <paramref name="conn"/>: creates and enables roles + users tables,
    /// inserts role(10,'Admin') and user(1,'Alice',10) as 'alice', captures
    /// clock_timestamp() as m1, then updates both rows as 'bob'.
    /// Returns m1 as a string suitable for set_config.
    /// </summary>
    private static async Task<string> SetupStandardDatasetAsync(NpgsqlConnection conn)
    {
        // Tables + temporal enable
        await ExecAsync(conn, CreateRoles);
        await ExecAsync(conn, CreateUsers);
        await ExecAsync(conn, EnableRoles);
        await ExecAsync(conn, EnableUsers);

        // Insert initial data as alice
        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO roles VALUES (10, 'Admin')");
        await ExecAsync(conn, "INSERT INTO users (id, name, role_id) VALUES (1, 'Alice', 10)");

        // Brief pause so clock_timestamp() falls strictly between the two versions
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var m1 = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Update as bob
        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE roles SET name = 'Administrator' WHERE id = 10");
        await ExecAsync(conn, "UPDATE users SET name = 'Alice B' WHERE id = 1");

        return m1!;
    }

    // ---------------------------------------------------------------------------
    // 1. Simple SELECT returns the historic version
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AsOf_SimpleSelect_ReturnsOldVersion()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var m1 = await SetupStandardDatasetAsync(conn);

        // Time-travel to m1
        await SetAsOfAsync(conn, m1);

        var name = await ScalarAsync<string>(conn,
            "SELECT name FROM users WHERE id = 1");
        Assert.Equal("Alice", name);

        // valid_from and changed_by must reflect the original version
        var (changedBy, hasValidFrom) = await QuerySingleAsync(conn,
            "SELECT changed_by, valid_from IS NOT NULL FROM users WHERE id = 1",
            r => (r.GetString(0), r.GetBoolean(1)));

        Assert.Equal("alice", changedBy);
        Assert.True(hasValidFrom);
    }

    // ---------------------------------------------------------------------------
    // 2. JOIN time-travels both tables simultaneously
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AsOf_Join_TimeTravelsBothTables()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var m1 = await SetupStandardDatasetAsync(conn);

        // --- as of m1 ---
        await SetAsOfAsync(conn, m1);

        var (userName, roleName) = await QuerySingleAsync(conn,
            "SELECT u.name, r.name FROM users u JOIN roles r ON r.id = u.role_id WHERE u.id = 1",
            r => (r.GetString(0), r.GetString(1)));

        Assert.Equal("Alice", userName);
        Assert.Equal("Admin", roleName);

        // --- present ---
        await ExecAsync(conn, "RESET temporal.as_of");

        (userName, roleName) = await QuerySingleAsync(conn,
            "SELECT u.name, r.name FROM users u JOIN roles r ON r.id = u.role_id WHERE u.id = 1",
            r => (r.GetString(0), r.GetString(1)));

        Assert.Equal("Alice B", userName);
        Assert.Equal("Administrator", roleName);
    }

    // ---------------------------------------------------------------------------
    // 3. Query through a regular VIEW
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AsOf_ThroughView_ReturnsOldValues()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var m1 = await SetupStandardDatasetAsync(conn);

        await ExecAsync(conn, """
            CREATE VIEW v_users AS
            SELECT u.id, u.name, r.name AS role_name
            FROM users u JOIN roles r ON r.id = u.role_id
            """);

        await SetAsOfAsync(conn, m1);

        var (userName, roleName) = await QuerySingleAsync(conn,
            "SELECT name, role_name FROM v_users WHERE id = 1",
            r => (r.GetString(0), r.GetString(1)));

        Assert.Equal("Alice", userName);
        Assert.Equal("Admin", roleName);
    }

    // ---------------------------------------------------------------------------
    // 4. CTE and sub-link both travel in time
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AsOf_Cte_And_Sublink_ReturnsOldValues()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var m1 = await SetupStandardDatasetAsync(conn);

        await SetAsOfAsync(conn, m1);

        // CTE
        var cteResult = await ScalarAsync<string>(conn,
            "WITH x AS (SELECT name FROM users WHERE id = 1) SELECT name FROM x");
        Assert.Equal("Alice", cteResult);

        // Sublink (scalar sub-select)
        var sublinkResult = await ScalarAsync<string>(conn,
            "SELECT (SELECT name FROM users WHERE id = 1)");
        Assert.Equal("Alice", sublinkResult);
    }

    // ---------------------------------------------------------------------------
    // 5. DML is blocked with SqlState 25006 (read_only_sql_transaction)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AsOf_DmlBlocked_Throws25006()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var m1 = await SetupStandardDatasetAsync(conn);

        await SetAsOfAsync(conn, m1);

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "UPDATE users SET name = 'x' WHERE id = 1"));

        Assert.Equal("25006", ex.SqlState);
    }

    // ---------------------------------------------------------------------------
    // 6. COPY <table> TO is blocked (0A000); COPY (SELECT) TO works
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AsOf_CopyTableBlocked_CopySelectAllowed()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var m1 = await SetupStandardDatasetAsync(conn);

        await SetAsOfAsync(conn, m1);

        // COPY <table> TO must be blocked
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "COPY users TO '/dev/null'"));

        Assert.Equal("0A000", ex.SqlState);

        // COPY (SELECT ...) TO must succeed — open a fresh connection because the
        // previous command left the session in an error state
        await using var conn2 = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await SetAsOfAsync(conn2, m1);

        // Should not throw
        await ExecAsync(conn2, "COPY (SELECT name FROM users) TO '/dev/null'");
    }

    // ---------------------------------------------------------------------------
    // 7. as_of set to a time before temporal.enable() → 0 rows
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AsOf_BeforeEnable_ReturnsZeroRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await SetupStandardDatasetAsync(conn);

        // Set as_of to a fixed timestamp well before the extension existed
        await ExecAsync(conn,
            "SELECT set_config('temporal.as_of', '2000-01-01 00:00:00+00', false)");

        var count = await ScalarAsync<long>(conn, "SELECT count(*) FROM users");
        Assert.Equal(0L, count);
    }

    // ---------------------------------------------------------------------------
    // 8. Prepared statement follows GUC changes (plan cache reset on SET/RESET)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AsOf_PreparedStatement_FollowsGucChanges()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var m1 = await SetupStandardDatasetAsync(conn);

        // Prepare the statement explicitly on this connection
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM users WHERE id = 1";
        await cmd.PrepareAsync(TestContext.Current.CancellationToken);

        // Present: must return current value
        var present1 = (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("Alice B", present1);

        // Time-travel: plan cache must be invalidated; must see the old version
        await SetAsOfAsync(conn, m1);
        var historic = (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("Alice", historic);

        // Back to present: plan cache reset again
        await ExecAsync(conn, "RESET temporal.as_of");
        var present2 = (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("Alice B", present2);
    }

    // ---------------------------------------------------------------------------
    // 9. Column dropped BEFORE temporal.enable() — as_of query still works
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AsOf_DroppedColumn_Works()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // Create table with extra column, drop it, THEN enable temporal
        await ExecAsync(conn, """
            CREATE TABLE items (
                id     int  PRIMARY KEY,
                name   text,
                legacy text
            )
            """);
        await ExecAsync(conn, "ALTER TABLE items DROP COLUMN legacy");
        await ExecAsync(conn, "SELECT temporal.enable('items', combine_interval => interval '0')");

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO items (id, name) VALUES (1, 'widget')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var m1 = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE items SET name = 'gadget' WHERE id = 1");

        // Time-travel must work even though a column was dropped before enable
        await SetAsOfAsync(conn, m1);

        var name = await ScalarAsync<string>(conn, "SELECT name FROM items WHERE id = 1");
        Assert.Equal("widget", name);
    }

    // ---------------------------------------------------------------------------
    // 10. EXPLAIN output contains 'users__history' and 'Append' under as_of
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AsOf_ExplainShowsUnion()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var m1 = await SetupStandardDatasetAsync(conn);

        await SetAsOfAsync(conn, m1);

        // EXPLAIN returns one row per plan line — collect them all
        var lines = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "EXPLAIN (COSTS OFF, FORMAT TEXT) SELECT name FROM users WHERE id = 1", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                lines.Add(reader.GetString(0));
        }
        var explain = string.Join("\n", lines);

        Assert.Contains("users__history", explain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Append", explain, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 11. Setting temporal.as_of to a non-timestamp value is rejected by the GUC
    //     check hook; a valid timestamp still sets cleanly afterwards.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AsOf_InvalidTimestampValue_Rejected()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => ExecAsync(conn, "SET temporal.as_of = 'not-a-timestamp'"));
        Assert.Equal("22023", ex.SqlState); // invalid_parameter_value
        Assert.Contains("temporal.as_of", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The connection is still usable and a valid value sets without error.
        await ExecAsync(conn, "SET temporal.as_of = '2026-01-01'");
        Assert.Contains("2026-01-01", await ScalarAsync<string>(
            conn, "SELECT current_setting('temporal.as_of')"));
        await ExecAsync(conn, "RESET temporal.as_of");
    }

    // ---------------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Sets temporal.as_of to <paramref name="timestampText"/> on
    /// <paramref name="conn"/> using set_config so the value can be passed as a
    /// parameter (SET does not accept parameters).
    /// </summary>
    private static async Task SetAsOfAsync(NpgsqlConnection conn, string timestampText)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('temporal.as_of', $1, false)";
        cmd.Parameters.AddWithValue(timestampText);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

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
