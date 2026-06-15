using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies the managed schema-evolution API: temporal.rename_column(),
/// temporal.drop_column(), temporal.alter_column_type(), plus the event-trigger
/// auto-propagation of raw ALTER TABLE … RENAME COLUMN and the blocking of raw
/// ALTER TABLE … DROP COLUMN while the versions view depends on the column.
/// </summary>
/// <remarks>
/// Every test creates an isolated database via CreateFreshDbWithExtensionAsync().
/// GUC-sensitive tests keep all statements on ONE NpgsqlConnection.
/// </remarks>
public sealed class SchemaEvolutionTests
{
    private readonly PgTemporalFixture _pg;

    public SchemaEvolutionTests(PgTemporalFixture pg) => _pg = pg;

    // ---------------------------------------------------------------------------
    // Helper: count rows in information_schema.columns by table/view name + column
    // ---------------------------------------------------------------------------

    private static async Task<long> ColCountAsync(NpgsqlConnection conn, string tableName, string columnName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*)
            FROM   information_schema.columns
            WHERE  table_schema  = 'public'
              AND  table_name    = $1
              AND  column_name   = $2
            """;
        cmd.Parameters.AddWithValue(tableName);
        cmd.Parameters.AddWithValue(columnName);
        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (long)result!;
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

    // ---------------------------------------------------------------------------
    // 1. temporal.rename_column() propagates rename to base, history, and view
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RenameColumn_Managed_PropagatesToBaseHistoryAndView()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, "CREATE TABLE t (id int PRIMARY KEY, name text, note text)");
        await ExecAsync(conn, "SELECT temporal.enable('t', combine_interval => interval '0')");

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO t (id, name, note) VALUES (1, 'a', 'n1')");
        await ExecAsync(conn, "UPDATE t SET name = 'a2' WHERE id = 1");
        await ExecAsync(conn, "UPDATE t SET note = 'n2' WHERE id = 1");

        await ExecAsync(conn, "SELECT temporal.rename_column('t', 'note', 'memo')");

        // 'memo' must be present on base table, history table, and versions view
        Assert.Equal(1L, await ColCountAsync(conn, "t",           "memo"));
        Assert.Equal(1L, await ColCountAsync(conn, "t__history",  "memo"));
        Assert.Equal(1L, await ColCountAsync(conn, "t__versions", "memo"));

        // 'note' must be absent from all three
        Assert.Equal(0L, await ColCountAsync(conn, "t",           "note"));
        Assert.Equal(0L, await ColCountAsync(conn, "t__history",  "note"));
        Assert.Equal(0L, await ColCountAsync(conn, "t__versions", "note"));

        // The versions view must still be queryable with the new column name
        await ExecAsync(conn, "SELECT memo FROM t__versions");
    }

    // ---------------------------------------------------------------------------
    // 2. Raw ALTER TABLE … RENAME COLUMN is auto-propagated to history by event trigger
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RenameColumn_Raw_AutoPropagatesToHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, "CREATE TABLE t (id int PRIMARY KEY, name text)");
        await ExecAsync(conn, "SELECT temporal.enable('t', combine_interval => interval '0')");

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO t (id, name) VALUES (1, 'a')");
        await ExecAsync(conn, "UPDATE t SET name = 'a2' WHERE id = 1");

        // Raw DDL — the event trigger should propagate this to t__history
        await ExecAsync(conn, "ALTER TABLE t RENAME COLUMN name TO fullname");

        Assert.Equal(1L, await ColCountAsync(conn, "t",          "fullname"));
        Assert.Equal(1L, await ColCountAsync(conn, "t__history", "fullname"));

        Assert.Equal(0L, await ColCountAsync(conn, "t",          "name"));
        Assert.Equal(0L, await ColCountAsync(conn, "t__history", "name"));
    }

    // ---------------------------------------------------------------------------
    // 3. temporal.drop_column() keeps history column by default
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DropColumn_KeepsHistoryColumnByDefault()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, "CREATE TABLE t (id int PRIMARY KEY, name text, tmp text)");
        await ExecAsync(conn, "SELECT temporal.enable('t', combine_interval => interval '0')");

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO t (id, name, tmp) VALUES (1, 'a', 'x')");
        await ExecAsync(conn, "UPDATE t SET tmp = 'x2' WHERE id = 1");

        await ExecAsync(conn, "SELECT temporal.drop_column('t', 'tmp')");

        // 'tmp' must be gone from base table and from the versions view
        Assert.Equal(0L, await ColCountAsync(conn, "t",           "tmp"));
        Assert.Equal(0L, await ColCountAsync(conn, "t__versions", "tmp"));

        // 'tmp' must still be present on the history table (old data preserved)
        Assert.Equal(1L, await ColCountAsync(conn, "t__history",  "tmp"));

        // The versions view must remain queryable
        await ExecAsync(conn, "SELECT * FROM t__versions");
    }

    // ---------------------------------------------------------------------------
    // 4. temporal.drop_column(p_drop_from_history => true) removes from history too
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DropColumn_DropFromHistoryTrue_RemovesFromHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, "CREATE TABLE t (id int PRIMARY KEY, name text, tmp text)");
        await ExecAsync(conn, "SELECT temporal.enable('t', combine_interval => interval '0')");

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO t (id, name, tmp) VALUES (1, 'a', 'x')");
        await ExecAsync(conn, "UPDATE t SET tmp = 'x2' WHERE id = 1");

        await ExecAsync(conn, "SELECT temporal.drop_column('t', 'tmp', p_drop_from_history => true)");

        Assert.Equal(0L, await ColCountAsync(conn, "t",           "tmp"));
        Assert.Equal(0L, await ColCountAsync(conn, "t__versions", "tmp"));
        Assert.Equal(0L, await ColCountAsync(conn, "t__history",  "tmp"));
    }

    // ---------------------------------------------------------------------------
    // 5. temporal.drop_column() rejects a primary-key column
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DropColumn_RejectsPrimaryKey()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, "CREATE TABLE t (id int PRIMARY KEY, name text)");
        await ExecAsync(conn, "SELECT temporal.enable('t', combine_interval => interval '0')");

        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "SELECT temporal.drop_column('t', 'id')"));
    }

    // ---------------------------------------------------------------------------
    // 6. temporal.alter_column_type() propagates type change to base and history
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AlterColumnType_PropagatesToBaseAndHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, "CREATE TABLE t (id int PRIMARY KEY, code text)");
        await ExecAsync(conn, "SELECT temporal.enable('t', combine_interval => interval '0')");

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO t (id, code) VALUES (1, '42')");
        await ExecAsync(conn, "UPDATE t SET code = '99' WHERE id = 1");

        await ExecAsync(conn, "SELECT temporal.alter_column_type('t', 'code', 'varchar(50)')");

        // Check data_type on base table
        var baseType = await ScalarAsync<string>(conn, """
            SELECT data_type
            FROM   information_schema.columns
            WHERE  table_schema = 'public'
              AND  table_name   = 't'
              AND  column_name  = 'code'
            """);
        Assert.Equal("character varying", baseType);

        var baseLen = await ScalarAsync<int?>(conn, """
            SELECT character_maximum_length
            FROM   information_schema.columns
            WHERE  table_schema = 'public'
              AND  table_name   = 't'
              AND  column_name  = 'code'
            """);
        Assert.Equal(50, baseLen);

        // Check data_type on history table
        var histType = await ScalarAsync<string>(conn, """
            SELECT data_type
            FROM   information_schema.columns
            WHERE  table_schema = 'public'
              AND  table_name   = 't__history'
              AND  column_name  = 'code'
            """);
        Assert.Equal("character varying", histType);

        var histLen = await ScalarAsync<int?>(conn, """
            SELECT character_maximum_length
            FROM   information_schema.columns
            WHERE  table_schema = 'public'
              AND  table_name   = 't__history'
              AND  column_name  = 'code'
            """);
        Assert.Equal(50, histLen);
    }

    // ---------------------------------------------------------------------------
    // 7. Raw ALTER TABLE … DROP COLUMN is blocked (versions view dependency)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RawDropColumn_IsBlocked()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, "CREATE TABLE t (id int PRIMARY KEY, name text, tmp text)");
        await ExecAsync(conn, "SELECT temporal.enable('t', combine_interval => interval '0')");

        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "ALTER TABLE t DROP COLUMN tmp"));
    }

    // ---------------------------------------------------------------------------
    // 8. temporal.as_of() still works correctly after a rename
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SchemaChange_AsOfStillWorks()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, "CREATE TABLE t (id int PRIMARY KEY, name text, note text)");
        await ExecAsync(conn, "SELECT temporal.enable('t', combine_interval => interval '0')");

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO t (id, name, note) VALUES (1, 'a', 'n1')");
        await ExecAsync(conn, "UPDATE t SET name = 'a2' WHERE id = 1");

        // Rename 'note' to 'memo' via the managed function
        await ExecAsync(conn, "SELECT temporal.rename_column('t', 'note', 'memo')");

        // The query-level marker temporal.as_of() requires a plan-time value
        // (a literal or bound parameter), so read the timestamp first, then
        // embed it as a literal in the time-travel query.
        var asOf = await ScalarAsync<string>(conn,
            "SELECT min(valid_from)::text FROM t__history WHERE id = 1");

        var historicalName = await ScalarAsync<string>(conn, $"""
            SELECT name
            FROM   t
            WHERE  temporal.as_of('{asOf}'::timestamptz)
              AND  id = 1
            """);

        Assert.Equal("a", historicalName);
    }
}
