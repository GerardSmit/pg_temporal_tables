using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies the core versioning semantics: INSERT stamps, UPDATE writes history,
/// DELETE writes history, NULL / require_user attribution, same-transaction
/// collapse, rollback safety, composite PK support, and no-op update suppression.
/// </summary>
public sealed class BasicVersioningTests
{
    private readonly PgTemporalFixture _pg;

    public BasicVersioningTests(PgTemporalFixture pg) => _pg = pg;

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

    private const string EnableTemporal = "SELECT temporal.enable('t')";

    // ---------------------------------------------------------------------------
    // 1. INSERT stamps valid_from / changed_by and writes NO history row
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Insert_StampsColumnsAndWritesNoHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableTemporal);

        // Use a single connection so the GUC persists for both statements.
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'Alice', 'alice@example.com', now())");

        // valid_from must be non-null
        var validFrom = await ScalarOnConnAsync<DateTime?>(conn,
            "SELECT valid_from FROM t WHERE id = 1");
        Assert.NotNull(validFrom);

        // changed_by must be 'alice'
        var changedBy = await ScalarOnConnAsync<string?>(conn,
            "SELECT changed_by FROM t WHERE id = 1");
        Assert.Equal("alice", changedBy);

        // No history rows
        var histCount = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(0L, histCount);
    }

    // ---------------------------------------------------------------------------
    // 2. UPDATE writes one history row (different user prevents combine)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Update_WritesHistoryRow()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableTemporal);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // INSERT as alice
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'OldName', 'alice@example.com', now())");

        // UPDATE as bob — different user guarantees no combine
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'NewName' WHERE id = 1");

        // One history row containing the old data
        var histCount = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(1L, histCount);

        // History row holds the OLD name and was authored by alice
        var (histName, histChangedBy, histValidTo) = await QuerySingleOnConnAsync(conn,
            "SELECT name, changed_by, valid_to FROM t__history WHERE id = 1",
            r => (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetDateTime(2)));

        Assert.Equal("OldName", histName);
        Assert.Equal("alice", histChangedBy);
        Assert.NotEqual(default, histValidTo); // valid_to must be set

        // Current row now belongs to bob
        var currentChangedBy = await ScalarOnConnAsync<string?>(conn,
            "SELECT changed_by FROM t WHERE id = 1");
        Assert.Equal("bob", currentChangedBy);
    }

    // ---------------------------------------------------------------------------
    // 3. DELETE writes a history row with deleted_by
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Delete_WritesHistoryRowWithDeletedBy()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableTemporal);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // INSERT alice, UPDATE bob (creates 1 history row), DELETE carol
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'Alice', 'a@example.com', now())");

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'Bob' WHERE id = 1");

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'carol'");
        await ExecOnConnAsync(conn, "DELETE FROM t WHERE id = 1");

        // 2 history rows total
        var histCount = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(2L, histCount);

        // The latest history row (highest valid_to) was deleted by carol
        var (deletedBy, changedByOnDelete) = await QuerySingleOnConnAsync(conn,
            "SELECT deleted_by, changed_by FROM t__history WHERE id = 1 ORDER BY valid_to DESC LIMIT 1",
            r => (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));

        Assert.Equal("carol", deletedBy);
        Assert.Equal("bob", changedByOnDelete);
    }

    // ---------------------------------------------------------------------------
    // 4. changed_by is NULL when no user_id GUC is set
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ChangedBy_NullWhenUnset()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableTemporal);

        // ExecAsync opens a fresh pooled connection with no GUC set
        await db.ExecAsync("INSERT INTO t VALUES (1, 'Alice', 'a@example.com', now())");

        var changedBy = await db.ScalarAsync<object>(
            "SELECT changed_by FROM t WHERE id = 1");

        Assert.True(changedBy is DBNull || changedBy is null,
            $"expected NULL changed_by but got: {changedBy}");
    }

    // ---------------------------------------------------------------------------
    // 5. require_user blocks writes when user_id is not set
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RequireUser_BlocksUnattributedWrite()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableTemporal);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // Turn on requirement — no user_id set on this connection
        await ExecOnConnAsync(conn, "SET temporal.require_user = on");

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'Alice', 'a@example.com', now())"));

        Assert.Equal("42501", ex.SqlState); // insufficient_privilege
    }

    // ---------------------------------------------------------------------------
    // 5b. require_user blocks an unattributed UPDATE
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RequireUser_BlocksUnattributedUpdate()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableTemporal);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // Seed a row while attributed.
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'Alice', 'a@example.com', now())");

        // Now require attribution but drop the identity.
        await ExecOnConnAsync(conn, "SET temporal.require_user = on");
        await ExecOnConnAsync(conn, "RESET temporal.user_id");

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecOnConnAsync(conn, "UPDATE t SET name = 'Changed' WHERE id = 1"));

        Assert.Equal("42501", ex.SqlState); // insufficient_privilege
    }

    // ---------------------------------------------------------------------------
    // 5c. require_user blocks an unattributed DELETE
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RequireUser_BlocksUnattributedDelete()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableTemporal);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'Alice', 'a@example.com', now())");

        await ExecOnConnAsync(conn, "SET temporal.require_user = on");
        await ExecOnConnAsync(conn, "RESET temporal.user_id");

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecOnConnAsync(conn, "DELETE FROM t WHERE id = 1"));

        Assert.Equal("42501", ex.SqlState); // insufficient_privilege
    }

    // ---------------------------------------------------------------------------
    // 6. INSERT + UPDATE + DELETE in the same transaction leaves no history
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SameTransaction_InsertUpdateDelete_LeavesNoHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableTemporal);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'dave'");

        await ExecOnConnAsync(conn, "BEGIN");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (2, 'a', null, null)");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'b' WHERE id = 2");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'c' WHERE id = 2");
        await ExecOnConnAsync(conn, "DELETE FROM t WHERE id = 2");
        await ExecOnConnAsync(conn, "COMMIT");

        var histCount = await db.ScalarAsync<long>(
            "SELECT count(*) FROM t__history WHERE id = 2");
        Assert.Equal(0L, histCount);
    }

    // ---------------------------------------------------------------------------
    // 7. Rolled-back UPDATE leaves no history and current row is unchanged
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Update_Rollback_LeavesNoHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableTemporal);

        // INSERT committed first
        await using (var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
            await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'Original', 'a@example.com', now())");
        }

        // UPDATE in a transaction that is rolled back
        await using (var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await ExecOnConnAsync(conn, "BEGIN");
            await ExecOnConnAsync(conn, "UPDATE t SET name = 'Changed' WHERE id = 1");
            await ExecOnConnAsync(conn, "ROLLBACK");
        }

        // No history rows
        var histCount = await db.ScalarAsync<long>(
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(0L, histCount);

        // Current row still has original name
        var name = await db.ScalarAsync<string>(
            "SELECT name FROM t WHERE id = 1");
        Assert.Equal("Original", name);
    }

    // ---------------------------------------------------------------------------
    // 8. Composite primary key table: UPDATE writes a history row
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CompositePk_Works()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();

        await db.ExecAsync("""
            CREATE TABLE t_composite (
                a    int,
                b    int,
                name text,
                PRIMARY KEY (a, b)
            )
            """);
        await db.ExecAsync("SELECT temporal.enable('t_composite')");

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t_composite VALUES (1, 2, 'v1')");

        // Different user forces a history row (no combine)
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn, "UPDATE t_composite SET name = 'v2' WHERE a = 1 AND b = 2");

        var histCount = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t_composite__history WHERE a = 1 AND b = 2");
        Assert.Equal(1L, histCount);
    }

    // ---------------------------------------------------------------------------
    // 9. No-op UPDATE on a toasted column writes no history row
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ToastedValue_NoopUpdate_NoHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();

        await db.ExecAsync("""
            CREATE TABLE t_toast (
                id  int PRIMARY KEY,
                big text
            )
            """);
        await db.ExecAsync("SELECT temporal.enable('t_toast')");

        // Insert a value large enough to be TOASTed (> 8 kB)
        var bigValue = new string('x', 1_048_576); // 1 MB

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO t_toast VALUES (1, @v)";
            cmd.Parameters.AddWithValue("v", bigValue);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // UPDATE SET big = big — identical value, no real change
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE t_toast SET big = @v WHERE id = 1";
            cmd.Parameters.AddWithValue("v", bigValue);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var histCount = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t_toast__history WHERE id = 1");
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
