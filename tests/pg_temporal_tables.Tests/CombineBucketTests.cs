using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies the combine-bucket semantics: insert-collapse, extend-on-update,
/// outside-window new row, cross-user never-combine, NULL-user never-combine,
/// zero-interval disables combining, concurrent conflict (40001), and prune.
/// </summary>
public sealed class CombineBucketTests
{
    private readonly PgTemporalFixture _pg;

    public CombineBucketTests(PgTemporalFixture pg) => _pg = pg;

    // ---------------------------------------------------------------------------
    // Shared SQL fragments
    // ---------------------------------------------------------------------------

    private const string CreateTestTable = """
        CREATE TABLE t (
            id         int  PRIMARY KEY,
            name       text,
            email      text,
            updated_at timestamptz
        )
        """;

    /// <summary>
    /// Enables temporal tracking with the default 2-second combine interval used
    /// by the majority of tests in this class.
    /// </summary>
    private const string EnableWith2s =
        "SELECT temporal.enable('t', combine_interval => interval '2 seconds')";

    // ---------------------------------------------------------------------------
    // 1. Same user within window: insert-collapse then extend
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SameUserWithinWindow_ExtendsHistoryRow()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWith2s);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // INSERT as alice — captures valid_from
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'v0', null, null)");

        // Update#1 as alice within 2s — insert-collapse: history still 0
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v1' WHERE id = 1");
        var histAfterAliceUpdate = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(0L, histAfterAliceUpdate); // insert-collapsed

        // Switch to bob — different user forces a new history row
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v2' WHERE id = 1");

        var histAfterBob1 = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(1L, histAfterBob1);

        // Capture the valid_to of the single history row before bob updates again
        var validToBefore = await ScalarOnConnAsync<DateTime>(conn,
            "SELECT valid_to FROM t__history WHERE id = 1");

        // Second bob update within 2s — must extend (not add) the existing history row
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v3' WHERE id = 1");

        var histAfterBob2 = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(1L, histAfterBob2); // still exactly one history row

        var validToAfter = await ScalarOnConnAsync<DateTime>(conn,
            "SELECT valid_to FROM t__history WHERE id = 1");

        // valid_to must have advanced (extended)
        Assert.True(validToAfter >= validToBefore,
            $"expected valid_to to advance; before={validToBefore:O} after={validToAfter:O}");
    }

    // ---------------------------------------------------------------------------
    // 2. Insert-collapse keeps the original valid_from
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task InsertCollapse_KeepsOriginalValidFrom()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWith2s);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'v0', null, null)");

        // Capture valid_from right after insert
        var validFromAfterInsert = await ScalarOnConnAsync<DateTime>(conn,
            "SELECT valid_from FROM t WHERE id = 1");

        // UPDATE within window — insert-collapse
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v1' WHERE id = 1");

        // valid_from on the current row must be unchanged
        var validFromAfterUpdate = await ScalarOnConnAsync<DateTime>(conn,
            "SELECT valid_from FROM t WHERE id = 1");

        Assert.Equal(validFromAfterInsert, validFromAfterUpdate);

        // Confirm no history row
        var histCount = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(0L, histCount);
    }

    // ---------------------------------------------------------------------------
    // 3. Outside the combine window: a new history row is created
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task OutsideWindow_NewHistoryRow()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        // Use a 1-second window; we will wait 1.5 s to fall outside it
        await db.ExecAsync(
            "SELECT temporal.enable('t', combine_interval => interval '1 second')");

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'v0', null, null)");

        // First update — insert-collapse (still within the 1-second window)
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v1' WHERE id = 1");

        var histAfterCollapse = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(0L, histAfterCollapse);

        // Wait to fall outside the combine window
        await Task.Delay(1_500, TestContext.Current.CancellationToken);

        // Second update — now outside the window; a new history row must appear
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v2' WHERE id = 1");

        var histAfterDelay = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(1L, histAfterDelay);
    }

    // ---------------------------------------------------------------------------
    // 4. Different users never combine, even within the window
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DifferentUser_NeverCombines()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWith2s);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // INSERT alice, UPDATE bob, UPDATE alice, UPDATE bob — all quickly
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'v0', null, null)");

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v1' WHERE id = 1"); // hist: 1

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v2' WHERE id = 1"); // hist: 2

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v3' WHERE id = 1"); // hist: 3

        var histCount = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(3L, histCount);
    }

    // ---------------------------------------------------------------------------
    // 5. NULL user never combines
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NullUser_NeverCombines()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWith2s);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // Ensure no user_id is set on this connection
        await ExecOnConnAsync(conn, "RESET temporal.user_id");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'v0', null, null)");

        // Each update must write a new history row (NULL never combines)
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v1' WHERE id = 1"); // hist: 1
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v2' WHERE id = 1"); // hist: 2

        var histCount = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(2L, histCount);
    }

    // ---------------------------------------------------------------------------
    // 6. combine_interval = '0' disables bucket combining (only same-tx collapses)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CombineZero_Default_EveryUpdateWritesHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(
            "SELECT temporal.enable('t', combine_interval => interval '0')");

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'v0', null, null)");

        // Rapid updates — with zero interval every update writes its own history row
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v1' WHERE id = 1"); // hist: 1
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v2' WHERE id = 1"); // hist: 2

        var histCount = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(2L, histCount);
    }

    [Fact]
    public async Task SetCombineInterval_TakesEffectForSubsequentUpdates()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(
            "SELECT temporal.enable('t', combine_interval => interval '0')");

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'v0', null, null)");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v1' WHERE id = 1");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v2' WHERE id = 1");

        Assert.Equal(2L, await ScalarOnConnAsync<long>(
            conn, "SELECT count(*) FROM t__history WHERE id = 1"));
        var maxValidToBeforeWindow = await ScalarOnConnAsync<DateTime>(
            conn, "SELECT max(valid_to) FROM t__history WHERE id = 1");

        await ExecOnConnAsync(conn, "SELECT temporal.set_combine_interval('t', interval '2 seconds')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v3' WHERE id = 1");

        Assert.Equal(2L, await ScalarOnConnAsync<long>(
            conn, "SELECT count(*) FROM t__history WHERE id = 1"));
        var maxValidToAfterWindow = await ScalarOnConnAsync<DateTime>(
            conn, "SELECT max(valid_to) FROM t__history WHERE id = 1");
        Assert.True(maxValidToAfterWindow > maxValidToBeforeWindow,
            $"expected chain history row to extend; before={maxValidToBeforeWindow:O} after={maxValidToAfterWindow:O}");

        await ExecOnConnAsync(conn, "SELECT temporal.set_combine_interval('t', interval '0')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v4' WHERE id = 1");

        Assert.Equal(3L, await ScalarOnConnAsync<long>(
            conn, "SELECT count(*) FROM t__history WHERE id = 1"));
        Assert.Equal("v4", await ScalarOnConnAsync<string>(
            conn, "SELECT name FROM t WHERE id = 1"));
    }

    // ---------------------------------------------------------------------------
    // 7. Concurrent older transaction raises SqlState 40001
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentOlderTransaction_Throws40001()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWith2s);

        // Insert a row so it exists before either conflicting transaction starts
        await db.ExecAsync("INSERT INTO t VALUES (1, 'initial', null, null)");

        // Open connection A and pin its transaction timestamp by starting a transaction
        await using var connA = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await ExecOnConnAsync(connA, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(connA, "BEGIN");
        // Executing any statement pins transaction_timestamp() for this transaction
        await ExecOnConnAsync(connA, "SELECT 1");

        // Wait briefly so connection B's commit will have a strictly later timestamp
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Connection B: update and commit — its valid_from is now > A's transaction_timestamp
        await using (var connB = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await ExecOnConnAsync(connB, "SET temporal.user_id = 'bob'");
            await ExecOnConnAsync(connB, "UPDATE t SET name = 'by-bob' WHERE id = 1");
        }

        // Connection A: attempt to update the same row — must fail with 40001
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecOnConnAsync(connA, "UPDATE t SET name = 'by-alice' WHERE id = 1"));

        await ExecOnConnAsync(connA, "ROLLBACK");

        Assert.Equal("40001", ex.SqlState);
    }

    // ---------------------------------------------------------------------------
    // 7b. A DELETE against a row a concurrent committed transaction modified
    //     after this transaction started also raises SqlState 40001
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentOlderTransaction_Delete_Throws40001()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWith2s);

        await db.ExecAsync("INSERT INTO t VALUES (1, 'initial', null, null)");

        // Connection A pins its transaction timestamp before B commits.
        await using var connA = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await ExecOnConnAsync(connA, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(connA, "BEGIN");
        await ExecOnConnAsync(connA, "SELECT 1");

        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Connection B updates and commits with a strictly later timestamp.
        await using (var connB = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await ExecOnConnAsync(connB, "SET temporal.user_id = 'bob'");
            await ExecOnConnAsync(connB, "UPDATE t SET name = 'by-bob' WHERE id = 1");
        }

        // Connection A: deleting the same row must fail with 40001.
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecOnConnAsync(connA, "DELETE FROM t WHERE id = 1"));

        await ExecOnConnAsync(connA, "ROLLBACK");

        Assert.Equal("40001", ex.SqlState);
    }

    // ---------------------------------------------------------------------------
    // 8. temporal.prune removes old history rows and returns the deleted count
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Prune_DeletesOldHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync(EnableWith2s);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // Build several history rows by using different users (no combining)
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'v0', null, null)");

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v1' WHERE id = 1");

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'carol'");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'v2' WHERE id = 1");

        var histBefore = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.True(histBefore > 0, "expected at least one history row before prune");

        // Prune with now() removes all history rows whose valid_to < now()
        var pruned = await ScalarOnConnAsync<long>(conn,
            "SELECT temporal.prune('t', now())");
        Assert.True(pruned > 0, $"expected prune to report deleted rows; got {pruned}");

        var histAfter = await ScalarOnConnAsync<long>(conn,
            "SELECT count(*) FROM t__history WHERE id = 1");
        Assert.Equal(0L, histAfter);
    }

    [Fact]
    public async Task Prune_RejectsUntrackedHistoryAndVersionsRelations()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("CREATE TABLE untracked_items (id int PRIMARY KEY)");
        await db.ExecAsync(EnableWith2s);

        foreach (var relation in new[] { "untracked_items", "t__history", "t__versions" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => db.ExecAsync($"SELECT temporal.prune('{relation}', now())"));
            Assert.Equal("42704", ex.SqlState);
            Assert.Contains("not a temporal-tracked table", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
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
