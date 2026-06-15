using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies the temporal viewer API: temporal.changes(), temporal.column_history(),
/// temporal.mod_user() / temporal.mod_date(), versions view aggregation, and the
/// guard that blocks viewer functions while temporal.as_of is active.
/// </summary>
/// <remarks>
/// The smoke_viewer.sql script drives the same scenario end-to-end.
/// Every test creates an isolated database via CreateFreshDbWithExtensionAsync().
/// GUC-sensitive tests keep all statements on ONE NpgsqlConnection.
/// </remarks>
public sealed class ChangeViewerTests
{
    private readonly PgTemporalFixture _pg;

    public ChangeViewerTests(PgTemporalFixture pg) => _pg = pg;

    // ---------------------------------------------------------------------------
    // Shared SQL — users table with excluded updated_at, combine_interval = '0'
    // ---------------------------------------------------------------------------

    private const string CreateUsers = """
        CREATE TABLE users (
            id         int  PRIMARY KEY,
            name       text,
            email      text,
            updated_at timestamptz
        )
        """;

    private const string EnableUsers =
        "SELECT temporal.enable('users', excluded_columns => ARRAY['updated_at']::name[], combine_interval => interval '0')";

    // ---------------------------------------------------------------------------
    // 1. changes() classifies INSERT, UPDATE (name), UPDATE (email), DELETE
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Changes_ClassifiesInsertUpdateDelete()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateUsers);
        await ExecAsync(conn, EnableUsers);

        // alice inserts row 1
        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO users (id, name, email) VALUES (1, 'Alice', 'a@x')");

        // bob updates name then email
        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE users SET name = 'Alice B' WHERE id = 1");
        await ExecAsync(conn, "UPDATE users SET email = 'b@x' WHERE id = 1");

        // carol deletes
        await ExecAsync(conn, "SET temporal.user_id = 'carol'");
        await ExecAsync(conn, "DELETE FROM users WHERE id = 1");

        // Collect all events in chronological order
        var events = await QueryAsync(conn,
            """
            SELECT pk, changed_by, operation,
                   old_row->>'name'    AS old_name,
                   new_row->>'name'    AS new_name,
                   changed_columns
            FROM   temporal.changes('users',
                       now() - interval '1 hour',
                       now() + interval '1 hour')
            ORDER  BY changed_at
            """,
            r => (
                Pk: r.GetString(0),
                ChangedBy: r.IsDBNull(1) ? null : r.GetString(1),
                Operation: r.GetString(2),
                OldName: r.IsDBNull(3) ? null : r.GetString(3),
                NewName: r.IsDBNull(4) ? null : r.GetString(4),
                ChangedColumns: r.IsDBNull(5) ? null : r.GetValue(5) as string[]
            ));

        Assert.Equal(4, events.Count);

        // event 0: INSERT by alice
        Assert.Equal("INSERT", events[0].Operation);
        Assert.Equal("alice", events[0].ChangedBy);
        Assert.Null(events[0].OldName);
        Assert.Equal("Alice", events[0].NewName);

        // event 1: UPDATE name by bob
        Assert.Equal("UPDATE", events[1].Operation);
        Assert.Equal("bob", events[1].ChangedBy);
        Assert.Equal("Alice", events[1].OldName);
        Assert.Equal("Alice B", events[1].NewName);
        Assert.Contains("name", events[1].ChangedColumns ?? []);

        // event 2: UPDATE email by bob — name unchanged
        Assert.Equal("UPDATE", events[2].Operation);
        Assert.Equal("bob", events[2].ChangedBy);
        Assert.Equal("Alice B", events[2].OldName);
        Assert.Contains("email", events[2].ChangedColumns ?? []);

        // event 3: DELETE by carol
        Assert.Equal("DELETE", events[3].Operation);
        Assert.Equal("carol", events[3].ChangedBy);
    }

    // ---------------------------------------------------------------------------
    // 2. changes() pk filter limits rows to the matching primary key
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Changes_PkFilter_LimitsRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateUsers);
        await ExecAsync(conn, EnableUsers);

        await ExecAsync(conn, "SET temporal.user_id = 'dave'");
        await ExecAsync(conn, "INSERT INTO users (id, name) VALUES (1, 'Alpha')");
        await ExecAsync(conn, "INSERT INTO users (id, name) VALUES (2, 'Beta')");
        await ExecAsync(conn, "SET temporal.user_id = 'eve'");
        await ExecAsync(conn, "UPDATE users SET name = 'Alpha2' WHERE id = 1");
        await ExecAsync(conn, "UPDATE users SET name = 'Beta2'  WHERE id = 2");

        // Filter to id=2 only
        var events = await QueryAsync(conn,
            """
            SELECT operation, pk
            FROM   temporal.changes('users',
                       now() - interval '1 hour',
                       now() + interval '1 hour',
                       '{"id": 2}')
            ORDER  BY changed_at
            """,
            r => (Operation: r.GetString(0), Pk: r.GetString(1)));

        // jsonb renders as {"id": 2} — the number is unquoted
        Assert.All(events, e => Assert.Contains(": 2", e.Pk));
        Assert.Equal(2, events.Count); // INSERT + UPDATE for id=2
    }

    // ---------------------------------------------------------------------------
    // 3. changes() time range filter — events before p_from are excluded
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Changes_TimeRange_Filters()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateUsers);
        await ExecAsync(conn, EnableUsers);

        await ExecAsync(conn, "SET temporal.user_id = 'frank'");
        await ExecAsync(conn, "INSERT INTO users (id, name) VALUES (1, 'v0')");

        // Capture a timestamp after the insert
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var mark = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "UPDATE users SET name = 'v1' WHERE id = 1");

        // Query events strictly after mark — INSERT is excluded
        var events = await QueryAsync(conn,
            """
            SELECT operation
            FROM   temporal.changes('users', $1::timestamptz, now() + interval '1 hour')
            ORDER  BY changed_at
            """,
            r => r.GetString(0),
            mark!);

        Assert.DoesNotContain("INSERT", events);
        Assert.Contains("UPDATE", events);
    }

    // ---------------------------------------------------------------------------
    // 4. column_history() returns only distinct consecutive values
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ColumnHistory_DistinctValuesOnly()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateUsers);
        await ExecAsync(conn, EnableUsers);

        // alice inserts
        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO users (id, name, email) VALUES (1, 'Alice', 'a@x')");

        // bob changes email only (name stays 'Alice') — must NOT appear as new name value
        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE users SET email = 'b@x' WHERE id = 1");

        // carol changes name
        await ExecAsync(conn, "SET temporal.user_id = 'carol'");
        await ExecAsync(conn, "UPDATE users SET name = 'Alice C' WHERE id = 1");

        var nameHistory = await QueryAsync(conn,
            "SELECT value #>> '{}' FROM temporal.column_history('users', '{\"id\": 1}', 'name') ORDER BY changed_at",
            r => r.GetString(0));

        // Should be: 'Alice' then 'Alice C' — the intermediate email-only change must not appear
        Assert.Equal(2, nameHistory.Count);
        Assert.Equal("Alice", nameHistory[0]);
        Assert.Equal("Alice C", nameHistory[1]);
    }

    // ---------------------------------------------------------------------------
    // 5. column_history() rejects excluded columns with an exception
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ColumnHistory_ExcludedColumn_Throws()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateUsers);
        await ExecAsync(conn, EnableUsers);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO users (id, name) VALUES (1, 'Alice')");

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn,
                "SELECT * FROM temporal.column_history('users', '{\"id\": 1}', 'updated_at')"));

        // Should raise an exception (P0001 raise_exception or similar)
        Assert.NotNull(ex);
    }

    // ---------------------------------------------------------------------------
    // 6. column_history() accepts the versions view and history table regclass
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ColumnHistory_AcceptsViewAndHistoryRegclass()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateUsers);
        await ExecAsync(conn, EnableUsers);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO users (id, name) VALUES (1, 'Alice')");
        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE users SET name = 'Bob' WHERE id = 1");

        // Both the view and the history table must resolve to the same data
        var viaView = await ScalarAsync<long>(conn,
            "SELECT count(*) FROM temporal.column_history('users__versions', '{\"id\": 1}', 'name')");
        var viaHistory = await ScalarAsync<long>(conn,
            "SELECT count(*) FROM temporal.column_history('users__history', '{\"id\": 1}', 'name')");

        Assert.Equal(viaHistory, viaView);
        Assert.True(viaView >= 1, "expected at least one name history entry");
    }

    // ---------------------------------------------------------------------------
    // 7. mod_user() / mod_date() work on rows from the base, history and view
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ModFuncs_WorkOnBaseHistoryAndView()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateUsers);
        await ExecAsync(conn, EnableUsers);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO users (id, name) VALUES (1, 'Alice')");
        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE users SET name = 'Bob' WHERE id = 1");

        // mod_user on current base row (bob made last change)
        var baseUser = await ScalarAsync<string?>(conn,
            "SELECT temporal.mod_user(u) FROM users u WHERE id = 1");
        Assert.Equal("bob", baseUser);

        var baseHasDate = await ScalarAsync<bool>(conn,
            "SELECT temporal.mod_date(u) IS NOT NULL FROM users u WHERE id = 1");
        Assert.True(baseHasDate);

        // mod_user on the first history row (alice's insert)
        var histUser = await ScalarAsync<string?>(conn,
            "SELECT temporal.mod_user(h) FROM users__history h ORDER BY valid_to LIMIT 1");
        Assert.Equal("alice", histUser);

        // mod_user on the versions view (oldest row first)
        var viewUser = await ScalarAsync<string?>(conn,
            "SELECT temporal.mod_user(v) FROM users__versions v ORDER BY valid_from LIMIT 1");
        Assert.Equal("alice", viewUser);
    }

    // ---------------------------------------------------------------------------
    // 8. Viewer functions raise an exception while temporal.as_of is active
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ViewerBlocked_UnderAsOf()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateUsers);
        await ExecAsync(conn, EnableUsers);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO users (id, name) VALUES (1, 'Alice')");

        // Set as_of — viewer functions must now reject calls
        await ExecAsync(conn, "SELECT set_config('temporal.as_of', '2026-01-01', false)");

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn,
                "SELECT * FROM temporal.changes('users', now() - interval '1 day', now())"));

        Assert.NotNull(ex); // any exception is acceptable — the function must refuse
        await using var conn2 = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await ExecAsync(conn2, "RESET temporal.as_of");
    }

    // ---------------------------------------------------------------------------
    // 8b. mod_date / mod_user reject rows that lack the managed columns or carry
    //     them with the wrong type, instead of returning garbage.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ModFuncs_RejectRowsWithoutManagedColumns()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();

        // mod_date on a row with no valid_from attribute.
        var noValidFrom = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.mod_date(t) FROM (SELECT 1 AS a) t"));
        Assert.Equal("42703", noValidFrom.SqlState); // undefined_column
        Assert.Contains("valid_from", noValidFrom.Message, StringComparison.OrdinalIgnoreCase);

        // mod_user on a row with no changed_by attribute.
        var noChangedBy = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.mod_user(t) FROM (SELECT now() AS valid_from) t"));
        Assert.Equal("42703", noChangedBy.SqlState);
        Assert.Contains("changed_by", noChangedBy.Message, StringComparison.OrdinalIgnoreCase);

        // mod_date on a valid_from of the wrong type.
        var validFromWrongType = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.mod_date(t) FROM (SELECT 'x'::text AS valid_from) t"));
        Assert.Equal("42804", validFromWrongType.SqlState); // datatype_mismatch
        Assert.Contains("valid_from", validFromWrongType.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not of type", validFromWrongType.Message, StringComparison.OrdinalIgnoreCase);

        // mod_user on a changed_by of the wrong type.
        var changedByWrongType = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.mod_user(t) FROM (SELECT 1 AS changed_by) t"));
        Assert.Equal("42804", changedByWrongType.SqlState);
        Assert.Contains("not of type text", changedByWrongType.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 9. Versions view aggregation with filters — date_bin + count
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task VersionsView_AggregationWithFilters()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, CreateUsers);
        await ExecAsync(conn, EnableUsers);

        // alice inserts two rows and updates them
        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO users (id, name) VALUES (1, 'R1')");
        await ExecAsync(conn, "INSERT INTO users (id, name) VALUES (2, 'R2')");
        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE users SET name = 'R1b' WHERE id = 1");
        await ExecAsync(conn, "UPDATE users SET name = 'R2b' WHERE id = 2");
        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "UPDATE users SET name = 'R1c' WHERE id = 1");

        // All alice versions in the last hour: two rows (id=1 twice, id=2 once)
        var (distinctIds, totalRows) = await QuerySingleAsync(conn,
            """
            SELECT count(DISTINCT id), count(*)
            FROM   users__versions
            WHERE  changed_by = 'alice'
              AND  valid_from >= now() - interval '1 hour'
            """,
            r => (r.GetInt64(0), r.GetInt64(1)));

        Assert.Equal(2L, distinctIds);
        Assert.True(totalRows >= 3L,
            $"expected at least 3 alice version rows (insert×2 + update×1), got {totalRows}");
    }

    // ---------------------------------------------------------------------------
    // Private helpers
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

    private static async Task<List<T>> QueryAsync<T>(
        NpgsqlConnection conn,
        string sql,
        Func<NpgsqlDataReader, T> map,
        params string[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < parameters.Length; i++)
            cmd.Parameters.AddWithValue(parameters[i]);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(map(reader));
        return rows;
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
