using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies excluded-column behaviour: updates that touch only excluded columns
/// are invisible (no history row, stamps unchanged); mixed updates write normally;
/// and set_excluded_columns takes effect dynamically.
/// </summary>
public sealed class ExcludedColumnsTests
{
    private readonly PgTemporalFixture _pg;

    public ExcludedColumnsTests(PgTemporalFixture pg) => _pg = pg;

    // ---------------------------------------------------------------------------
    // Shared SQL fragments
    // ---------------------------------------------------------------------------

    private const string CreateTestTable = """
        CREATE TABLE t (
            id         int          PRIMARY KEY,
            name       text,
            email      text,
            updated_at timestamptz
        )
        """;

    // ---------------------------------------------------------------------------
    // 1. Update of only excluded columns: no history row, stamps unchanged
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExcludedOnlyUpdate_NoHistory_KeepsStamps()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(
            "SELECT temporal.enable('t', excluded_columns => ARRAY['updated_at']::name[])");

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // INSERT as alice — capture valid_from and changed_by
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'Alice', 'a@example.com', now())");

        var (insertValidFrom, insertChangedBy) = await QuerySingleOnConnAsync(conn,
            "SELECT valid_from, changed_by FROM t WHERE id = 1",
            r => (r.GetDateTime(0), r.IsDBNull(1) ? null : r.GetString(1)));

        // UPDATE only the excluded column as bob — must be invisible
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn, "UPDATE t SET updated_at = now() WHERE id = 1");

        // No history row
        var histCount = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(0L, histCount);

        // Stamps on current row are unchanged
        var (currentValidFrom, currentChangedBy) = await QuerySingleOnConnAsync(conn,
            "SELECT valid_from, changed_by FROM t WHERE id = 1",
            r => (r.GetDateTime(0), r.IsDBNull(1) ? null : r.GetString(1)));

        Assert.Equal(insertValidFrom, currentValidFrom);
        Assert.Equal(insertChangedBy, currentChangedBy); // still 'alice', not 'bob'
    }

    // ---------------------------------------------------------------------------
    // 2. Mixed update (excluded + non-excluded) writes a history row
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task MixedUpdate_WritesHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(
            "SELECT temporal.enable('t', excluded_columns => ARRAY['updated_at']::name[])");

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'Alice', 'a@example.com', now())");

        // UPDATE both excluded and non-excluded columns as bob
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn,
            "UPDATE t SET updated_at = now(), name = 'Bob' WHERE id = 1");

        // Must produce a history row
        var histCount = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(1L, histCount);

        // History row holds alice's version
        var histName = await ScalarOnConnAsync<string>(conn,
            "SELECT name FROM t__history WHERE id = 1");
        Assert.Equal("Alice", histName);
    }

    // ---------------------------------------------------------------------------
    // 3. set_excluded_columns takes effect: subsequent excluded-only updates
    //    produce no history row
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SetExcludedColumns_TakesEffect()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        // Enable WITHOUT exclusions first
        await db.ExecAsync("SELECT temporal.enable('t')");

        // Dynamically add updated_at to excluded columns
        await db.ExecAsync(
            "SELECT temporal.set_excluded_columns('t', ARRAY['updated_at']::name[])");

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'Alice', 'a@example.com', now())");

        // UPDATE only updated_at as bob — should now be invisible
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn, "UPDATE t SET updated_at = now() WHERE id = 1");

        var histCount = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(0L, histCount);
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
