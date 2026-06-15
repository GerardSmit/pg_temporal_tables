using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies Phase 4 PK-lineage tracking: temporal_row_id is assigned once at INSERT
/// and never changed on UPDATE (including PK changes), causing temporal.changes() and
/// temporal.column_history() to follow a row across primary-key mutations as a single
/// continuous chain when track_lineage => true.
/// </summary>
public sealed class PkLineageTests
{
    private readonly PgTemporalFixture _pg;

    public PkLineageTests(PgTemporalFixture pg) => _pg = pg;

    // ---------------------------------------------------------------------------
    // Shared SQL fragments
    // ---------------------------------------------------------------------------

    private const string CreateT = """
        CREATE TABLE t (
            id   int  PRIMARY KEY,
            name text
        )
        """;

    // ---------------------------------------------------------------------------
    // 1. Enable with track_lineage adds an immutable temporal_row_id column
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_WithLineage_AddsImmutableRowId()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateT);
        await db.ExecAsync(
            "SELECT temporal.enable('t', combine_interval => interval '0', track_lineage => true)");

        await db.ExecAsync("INSERT INTO t VALUES (1, 'a')");

        // Column must exist and be non-null
        var isNotNull = await db.ScalarAsync<bool>(
            "SELECT temporal_row_id IS NOT NULL FROM t WHERE id = 1");
        Assert.True(isNotNull);

        // Capture the assigned row id
        var rowId = await db.ScalarAsync<long>(
            "SELECT temporal_row_id FROM t WHERE id = 1");

        // UPDATE name — temporal_row_id must remain unchanged
        await db.ExecAsync("UPDATE t SET name = 'b' WHERE id = 1");
        var rowIdAfterNameUpdate = await db.ScalarAsync<long>(
            "SELECT temporal_row_id FROM t WHERE id = 1");
        Assert.Equal(rowId, rowIdAfterNameUpdate);

        // UPDATE name again
        await db.ExecAsync("UPDATE t SET name = 'c' WHERE id = 1");
        var rowIdAfterSecondUpdate = await db.ScalarAsync<long>(
            "SELECT temporal_row_id FROM t WHERE id = 1");
        Assert.Equal(rowId, rowIdAfterSecondUpdate);
    }

    // ---------------------------------------------------------------------------
    // 2. A PK change appears as a single UPDATE chain when lineage is on
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task PkUpdate_KeepsSingleChain_ViaLineage()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateT);
        await db.ExecAsync(
            "SELECT temporal.enable('t', combine_interval => interval '0', track_lineage => true)");

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'a')");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'b' WHERE id = 1");
        await ExecOnConnAsync(conn, "UPDATE t SET id = 99 WHERE id = 1");   // PK change
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'c' WHERE id = 99");

        // All 4 operations should appear as a single chain — no DELETE+INSERT split
        var events = await QueryOnConnAsync(conn,
            """
            SELECT operation,
                   old_row->>'id'   AS old_id,
                   new_row->>'id'   AS new_id,
                   new_row->>'name' AS new_name
            FROM   temporal.changes('t',
                       now() - interval '1 hour',
                       now() + interval '1 hour')
            ORDER  BY changed_at
            """,
            r => (
                Operation: r.GetString(0),
                OldId: r.IsDBNull(1) ? null : r.GetString(1),
                NewId: r.IsDBNull(2) ? null : r.GetString(2),
                NewName: r.IsDBNull(3) ? null : r.GetString(3)
            ));

        Assert.Equal(4, events.Count);

        // Event 0: INSERT new_name='a'
        Assert.Equal("INSERT", events[0].Operation);
        Assert.Equal("a", events[0].NewName);

        // Events 1-3: all UPDATE (no DELETE or second INSERT)
        Assert.All(events.Skip(1), e => Assert.Equal("UPDATE", e.Operation));

        // One event must show the PK changing from 1 to 99
        var pkChangeEvent = events.Single(e => e.OldId == "1" && e.NewId == "99");
        Assert.Equal("UPDATE", pkChangeEvent.Operation);

        // Only one distinct temporal_row_id in the versions table
        var distinctRowIds = await ScalarOnConnAsync<long>(conn,
            "SELECT count(DISTINCT temporal_row_id) FROM t__versions");
        Assert.Equal(1L, distinctRowIds);
    }

    // ---------------------------------------------------------------------------
    // 3. column_history() follows the row across a PK change when lineage is on
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ColumnHistory_FollowsAcrossPkChange()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateT);
        await db.ExecAsync(
            "SELECT temporal.enable('t', combine_interval => interval '0', track_lineage => true)");

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'a')");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'b' WHERE id = 1");
        await ExecOnConnAsync(conn, "UPDATE t SET id = 99 WHERE id = 1");   // PK change
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'c' WHERE id = 99");

        // Filter by the *current* PK (99) — must see all 3 distinct name values
        // Use value::text so we get the jsonb-encoded string (includes surrounding quotes)
        var nameHistory = await QueryOnConnAsync(conn,
            """
            SELECT value::text
            FROM   temporal.column_history('t', '{"id":99}'::jsonb, 'name')
            ORDER  BY changed_at
            """,
            r => r.GetString(0));

        // jsonb text representation of string values includes the double-quotes
        Assert.Equal(3, nameHistory.Count);
        Assert.Equal("\"a\"", nameHistory[0]);
        Assert.Equal("\"b\"", nameHistory[1]);
        Assert.Equal("\"c\"", nameHistory[2]);
    }

    // ---------------------------------------------------------------------------
    // 4. Without lineage a PK change splits the chain (partitioned by pk)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Default_NoLineage_SplitsChainOnPkUpdate()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateT);
        // Default: no track_lineage
        await db.ExecAsync(
            "SELECT temporal.enable('t', combine_interval => interval '0')");

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'a')");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'b' WHERE id = 1");
        await ExecOnConnAsync(conn, "UPDATE t SET id = 99 WHERE id = 1");   // PK change

        var events = await QueryOnConnAsync(conn,
            """
            SELECT operation,
                   old_row->>'id' AS old_id,
                   new_row->>'id' AS new_id
            FROM   temporal.changes('t',
                       now() - interval '1 hour',
                       now() + interval '1 hour')
            ORDER  BY changed_at
            """,
            r => (
                Operation: r.GetString(0),
                OldId: r.IsDBNull(1) ? null : r.GetString(1),
                NewId: r.IsDBNull(2) ? null : r.GetString(2)
            ));

        // Without lineage the PK change cannot produce an UPDATE that links id 1 → 99
        Assert.All(events, e =>
            Assert.False(e.OldId == "1" && e.NewId == "99",
                "Expected no single UPDATE event linking pk=1 to pk=99 without lineage"));

        // The split manifests as a DELETE for pk=1 or an INSERT for pk=99
        var hasDelete = events.Any(e => e.Operation == "DELETE");
        var hasInsert99 = events.Any(e => e.Operation == "INSERT" && e.NewId == "99");
        Assert.True(hasDelete || hasInsert99,
            "Expected a DELETE of pk=1 or an INSERT of pk=99 to signal the chain split");

        // No temporal_row_id column when track_lineage is false
        var lineageColCount = await ScalarOnConnAsync<long>(conn,
            """
            SELECT count(*)
            FROM   information_schema.columns
            WHERE  table_name = 't'
              AND  column_name = 'temporal_row_id'
            """);
        Assert.Equal(0L, lineageColCount);
    }

    // ---------------------------------------------------------------------------
    // 5. Enable with track_lineage rejects a table that already has temporal_row_id
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_TrackLineage_AcceptsExistingBigintColumn_RejectsWrongType()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();

        // A pre-existing bigint temporal_row_id (e.g. an ORM shadow column) is
        // adopted as the lineage column — enabling succeeds and it gets a value
        // generator.
        await db.ExecAsync("CREATE TABLE ok (id int PRIMARY KEY, temporal_row_id bigint)");
        await db.ExecAsync("SELECT temporal.enable('ok', track_lineage => true)");
        await db.ExecAsync("SET temporal.user_id = 'alice'");
        await db.ExecAsync("INSERT INTO ok (id) VALUES (1)");
        Assert.True(await db.ScalarAsync<bool>("SELECT temporal_row_id IS NOT NULL FROM ok WHERE id = 1"));

        // A wrong-typed temporal_row_id is rejected.
        await db.ExecAsync("CREATE TABLE bad (id int PRIMARY KEY, temporal_row_id text)");
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
            await db.ExecAsync("SELECT temporal.enable('bad', track_lineage => true)"));
        Assert.Contains("bigint", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 6. temporal_row_id cannot be added to the excluded-columns list
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_CannotExcludeLineageColumn()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateT);
        await db.ExecAsync(
            "SELECT temporal.enable('t', track_lineage => true)");

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
            await db.ExecAsync(
                "SELECT temporal.set_excluded_columns('t', ARRAY['temporal_row_id']::name[])"));

        Assert.NotNull(ex);
    }

    // ---------------------------------------------------------------------------
    // 7. restore_deleted() resurrects the SAME lineage (object identity), not a
    //    new one — a deleted row brought back keeps its temporal_row_id.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RestoreDeleted_PreservesOriginalLineage()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecOnConnAsync(conn, "CREATE TABLE t (id int PRIMARY KEY, name text)");
        await ExecOnConnAsync(conn, "SELECT temporal.enable('t', combine_interval => interval '0', track_lineage => true)");

        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t VALUES (1, 'a')");
        await ExecOnConnAsync(conn, "UPDATE t SET name = 'b' WHERE id = 1");

        var originalLineage = await ScalarOnConnAsync<long>(conn,
            "SELECT temporal_row_id FROM t WHERE id = 1");

        await ExecOnConnAsync(conn, "DELETE FROM t WHERE id = 1");
        await ExecOnConnAsync(conn, "SELECT temporal.restore_deleted('t', '{\"id\":1}'::jsonb)");

        // Resurrected row carries the ORIGINAL lineage id, not a fresh one.
        var restoredLineage = await ScalarOnConnAsync<long>(conn,
            "SELECT temporal_row_id FROM t WHERE id = 1");
        Assert.Equal(originalLineage, restoredLineage);

        // Still a single lineage across every version (the deleted gap and the
        // resurrection share the one object identity).
        Assert.Equal(1L, await ScalarOnConnAsync<long>(conn,
            "SELECT count(DISTINCT temporal_row_id) FROM t__versions WHERE temporal_row_id = " + originalLineage));
        Assert.Equal("b", await ScalarOnConnAsync<string>(conn, "SELECT name FROM t WHERE id = 1"));
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

    private static async Task<List<T>> QueryOnConnAsync<T>(
        NpgsqlConnection conn,
        string sql,
        Func<NpgsqlDataReader, T> map)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(map(reader));
        return rows;
    }
}
