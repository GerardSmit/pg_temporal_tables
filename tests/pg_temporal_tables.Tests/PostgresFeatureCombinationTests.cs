using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Exercises pg_temporal_tables together with common PostgreSQL statement
/// shapes, so ordinary SQL remains composable with tracking and AS OF rewrites.
/// </summary>
public sealed class PostgresFeatureCombinationTests
{
    private readonly PgTemporalFixture _pg;

    public PostgresFeatureCombinationTests(PgTemporalFixture pg) => _pg = pg;

    [Fact]
    public async Task AsOf_ComplexSelectShapes_RewriteAllTrackedReferences()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await CreateComplexReadSchemaAsync(conn);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO departments VALUES (1, 'Support')");
        await ExecAsync(conn, "INSERT INTO roles VALUES (10, 1, 'Agent')");
        await ExecAsync(conn, "INSERT INTO users VALUES (1, 10, 'Ada', true), (2, 10, 'Ben', false)");
        await ExecAsync(conn, """
            INSERT INTO tickets VALUES
                (100, 1, 'alpha', 'open', 5),
                (101, 1, 'beta',  'open', 3),
                (102, 2, 'gamma', 'closed', 9)
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE departments SET name = 'Customer Ops' WHERE id = 1");
        await ExecAsync(conn, "UPDATE roles SET name = 'Senior Agent' WHERE id = 10");
        await ExecAsync(conn, "UPDATE users SET name = 'Ada Current' WHERE id = 1");
        await ExecAsync(conn, "UPDATE tickets SET state = 'closed', priority = 1 WHERE id = 100");
        await ExecAsync(conn, "INSERT INTO tickets VALUES (103, 1, 'delta', 'open', 8)");

        await SetAsOfAsync(conn, asOf!);

        var historic = await QuerySingleAsync(conn, ComplexReadQuery,
            r => (
                UserName: r.GetString(0),
                RoleName: r.GetString(1),
                DepartmentName: r.GetString(2),
                OpenCount: r.GetInt64(3),
                MaxPriority: r.GetInt32(4),
                Rank: r.GetInt64(5),
                HasAlpha: r.GetBoolean(6),
                Titles: r.GetString(7),
                AllTicketCount: r.GetInt64(8)));

        Assert.Equal("Ada", historic.UserName);
        Assert.Equal("Agent", historic.RoleName);
        Assert.Equal("Support", historic.DepartmentName);
        Assert.Equal(2L, historic.OpenCount);
        Assert.Equal(5, historic.MaxPriority);
        Assert.Equal(1L, historic.Rank);
        Assert.True(historic.HasAlpha);
        Assert.Equal("alpha,beta", historic.Titles);
        Assert.Equal(2L, historic.AllTicketCount);

        await ExecAsync(conn, "RESET temporal.as_of");
        var present = await QuerySingleAsync(conn, ComplexReadQuery,
            r => (
                UserName: r.GetString(0),
                RoleName: r.GetString(1),
                DepartmentName: r.GetString(2),
                OpenCount: r.GetInt64(3),
                MaxPriority: r.GetInt32(4),
                Titles: r.GetString(7),
                AllTicketCount: r.GetInt64(8)));

        Assert.Equal("Ada Current", present.UserName);
        Assert.Equal("Senior Agent", present.RoleName);
        Assert.Equal("Customer Ops", present.DepartmentName);
        Assert.Equal(2L, present.OpenCount);
        Assert.Equal(8, present.MaxPriority);
        Assert.Equal("beta,delta", present.Titles);
        Assert.Equal(3L, present.AllTicketCount);
    }

    [Fact]
    public async Task AsOf_QueryLevelMarker_TimeTravelsOneStatementOnly()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE marker_roles (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE marker_users (
                id      int PRIMARY KEY,
                role_id int NOT NULL,
                name    text NOT NULL
            );
            SELECT temporal.enable('marker_roles', combine_interval => interval '0');
            SELECT temporal.enable('marker_users', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO marker_roles VALUES (1, 'role-old')");
        await ExecAsync(conn, "INSERT INTO marker_users VALUES (1, 1, 'user-old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOfLiteral = await ScalarAsync<string>(conn,
            "SELECT quote_literal(clock_timestamp()) || '::timestamptz'");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE marker_roles SET name = 'role-current' WHERE id = 1");
        await ExecAsync(conn, "UPDATE marker_users SET name = 'user-current' WHERE id = 1");

        var historic = await QuerySingleAsync(conn, $"""
            SELECT u.name, r.name
            FROM marker_users u
            JOIN marker_roles r ON r.id = u.role_id
            WHERE u.id = 1
              AND temporal.as_of({asOfLiteral})
            """, r => (UserName: r.GetString(0), RoleName: r.GetString(1)));

        Assert.Equal(("user-old", "role-old"), historic);

        var present = await QuerySingleAsync(conn, """
            SELECT u.name, r.name
            FROM marker_users u
            JOIN marker_roles r ON r.id = u.role_id
            WHERE u.id = 1
            """, r => (UserName: r.GetString(0), RoleName: r.GetString(1)));

        Assert.Equal(("user-current", "role-current"), present);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var laterAsOfLiteral = await ScalarAsync<string>(conn,
            "SELECT quote_literal(clock_timestamp()) || '::timestamptz'");

        var conflicting = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, $"""
                SELECT 1
                FROM marker_users
                WHERE temporal.as_of({asOfLiteral})
                  AND temporal.as_of({laterAsOfLiteral})
                """));
        Assert.Equal("0A000", conflicting.SqlState);

        var write = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, $"""
                UPDATE marker_users
                   SET name = 'blocked'
                 WHERE id = 1
                   AND temporal.as_of({asOfLiteral})
                """));
        Assert.Equal("25006", write.SqlState);
    }

    [Fact]
    public async Task AsOf_QueryLevelMarkerRejectsNullAndRuntimeExpressions()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE invalid_marker_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('invalid_marker_items', combine_interval => interval '0');
            INSERT INTO invalid_marker_items VALUES (1, 'present');
            """);

        var nullMarker = await Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(conn, """
            SELECT name
            FROM invalid_marker_items
            WHERE temporal.as_of(NULL::timestamptz)
            """));
        Assert.Equal("22004", nullMarker.SqlState);
        Assert.Contains("cannot be NULL", nullMarker.MessageText, StringComparison.OrdinalIgnoreCase);

        var runtimeMarker = await Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(conn, """
            SELECT name
            FROM invalid_marker_items
            WHERE temporal.as_of(clock_timestamp())
            """));
        Assert.Equal("0A000", runtimeMarker.SqlState);
        Assert.Contains("plan-time", runtimeMarker.MessageText, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("present", await ScalarAsync<string>(conn,
            "SELECT name FROM invalid_marker_items WHERE id = 1"));
    }

    [Fact]
    public async Task AsOf_QueryLevelMarkerAcceptsBoundTimestamptzParameters()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE parameter_marker_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('parameter_marker_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO parameter_marker_items VALUES (1, 'version-one')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var firstAsOf = await ScalarAsync<DateTime>(conn, "SELECT clock_timestamp()");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE parameter_marker_items SET name = 'version-two' WHERE id = 1");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var secondAsOf = await ScalarAsync<DateTime>(conn, "SELECT clock_timestamp()");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'carol'");
        await ExecAsync(conn, "UPDATE parameter_marker_items SET name = 'version-three' WHERE id = 1");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name
            FROM parameter_marker_items
            WHERE id = 1
              AND temporal.as_of($1)
            """;
        var parameter = new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
            Value = firstAsOf
        };
        cmd.Parameters.Add(parameter);

        Assert.Equal("version-one",
            (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);

        parameter.Value = secondAsOf;
        Assert.Equal("version-two",
            (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);

        Assert.Equal("version-three", await ScalarAsync<string>(conn,
            "SELECT name FROM parameter_marker_items WHERE id = 1"));
    }

    [Fact]
    public async Task AsOf_ServerPreparedGenericPlansFollowSessionTimestamp()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE prepared_plan_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('prepared_plan_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO prepared_plan_items VALUES (1, 'old-one'), (2, 'old-two')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE prepared_plan_items SET name = 'current-one' WHERE id = 1;
            DELETE FROM prepared_plan_items WHERE id = 2;
            INSERT INTO prepared_plan_items VALUES (3, 'current-three');
            """);

        await ExecAsync(conn, "SET plan_cache_mode = force_generic_plan");
        await ExecAsync(conn, """
            PREPARE fetch_prepared_plan_item(int) AS
            SELECT name
            FROM prepared_plan_items
            WHERE id = $1
            """);

        try
        {
            Assert.Equal("current-one", await ScalarAsync<string>(conn,
                "EXECUTE fetch_prepared_plan_item(1)"));

            await SetAsOfAsync(conn, asOf!);
            Assert.Equal("old-one", await ScalarAsync<string>(conn,
                "EXECUTE fetch_prepared_plan_item(1)"));
            Assert.Equal("old-two", await ScalarAsync<string>(conn,
                "EXECUTE fetch_prepared_plan_item(2)"));

            await ExecAsync(conn, "RESET temporal.as_of");
            Assert.Equal("current-one", await ScalarAsync<string>(conn,
                "EXECUTE fetch_prepared_plan_item(1)"));
            Assert.Equal("current-three", await ScalarAsync<string>(conn,
                "EXECUTE fetch_prepared_plan_item(3)"));
        }
        finally
        {
            await ExecAsync(conn, "DEALLOCATE fetch_prepared_plan_item");
            await ExecAsync(conn, "RESET plan_cache_mode");
        }
    }

    [Fact]
    public async Task AsOf_QueryLevelMarkerCanFeedUntrackedWriteStatements()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE marker_source_items (
                id   int PRIMARY KEY,
                name text NOT NULL,
                qty  int NOT NULL
            );
            CREATE TABLE marker_exports (
                id   int PRIMARY KEY,
                name text NOT NULL,
                qty  int NOT NULL
            );
            CREATE TABLE marker_merge_exports (
                id   int PRIMARY KEY,
                name text NOT NULL,
                qty  int NOT NULL
            );
            SELECT temporal.enable('marker_source_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO marker_source_items VALUES
                (1, 'old-one', 10),
                (2, 'old-two', 20);
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOfLiteral = await ScalarAsync<string>(conn,
            "SELECT quote_literal(clock_timestamp()) || '::timestamptz'");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE marker_source_items SET name = 'current-one', qty = 110 WHERE id = 1;
            DELETE FROM marker_source_items WHERE id = 2;
            INSERT INTO marker_source_items VALUES (3, 'current-three', 30);
            """);

        await ExecAsync(conn, $"""
            INSERT INTO marker_exports (id, name, qty)
            SELECT id, name, qty
            FROM marker_source_items
            WHERE temporal.as_of({asOfLiteral})
            ORDER BY id
            """);

        await ExecAsync(conn, $"""
            UPDATE marker_exports AS e
               SET name = s.name || '-marker',
                   qty = s.qty + 1
            FROM marker_source_items AS s
            WHERE s.id = e.id
              AND e.id = 1
              AND temporal.as_of({asOfLiteral})
            """);

        await ExecAsync(conn, $"""
            DELETE FROM marker_exports AS e
            USING marker_source_items AS s
            WHERE s.id = e.id
              AND s.id = 2
              AND temporal.as_of({asOfLiteral})
            """);

        Assert.Equal("1:old-one-marker:11", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM marker_exports
            """));

        await ExecAsync(conn, "INSERT INTO marker_merge_exports VALUES (1, 'seed', 0)");
        await ExecAsync(conn, $"""
            MERGE INTO marker_merge_exports AS m
            USING (
                SELECT id, name, qty
                FROM marker_source_items
                WHERE temporal.as_of({asOfLiteral})
            ) AS s
            ON m.id = s.id
            WHEN MATCHED THEN
                UPDATE SET name = s.name || '-merged', qty = s.qty
            WHEN NOT MATCHED THEN
                INSERT (id, name, qty) VALUES (s.id, s.name, s.qty)
            """);

        Assert.Equal("1:old-one-merged:10,2:old-two:20", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM marker_merge_exports
            """));
    }

    [Fact]
    public async Task AsOf_CursorsKeepHistoricalSnapshotAcrossFetches()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE cursor_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('cursor_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO cursor_items VALUES (1, 'old-one'), (2, 'old-two')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE cursor_items SET name = 'current-one' WHERE id = 1;
            DELETE FROM cursor_items WHERE id = 2;
            INSERT INTO cursor_items VALUES (3, 'current-three');
            """);

        await ExecAsync(conn, "BEGIN");
        try
        {
            await SetAsOfAsync(conn, asOf!);
            await ExecAsync(conn, """
                DECLARE historic_cursor CURSOR FOR
                SELECT id, name
                FROM cursor_items
                ORDER BY id
                """);
            await ExecAsync(conn, "RESET temporal.as_of");

            var rows = await QueryAsync(conn,
                "FETCH ALL FROM historic_cursor",
                r => (Id: r.GetInt32(0), Name: r.GetString(1)));

            Assert.Equal([(1, "old-one"), (2, "old-two")], rows);
            await ExecAsync(conn, "CLOSE historic_cursor");
            await ExecAsync(conn, "COMMIT");
        }
        catch
        {
            await ExecAsync(conn, "ROLLBACK");
            throw;
        }

        Assert.Equal("1:current-one,3:current-three", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM cursor_items
            """));
    }

    [Fact]
    public async Task Dml_CursorWhereCurrentOf_IsVersioned()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE current_of_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('current_of_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO current_of_items VALUES (1, 'old-one'), (2, 'old-two')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "BEGIN");
        try
        {
            await ExecAsync(conn, """
                DECLARE current_of_cursor CURSOR FOR
                SELECT id
                FROM current_of_items
                ORDER BY id
                FOR UPDATE
                """);
            Assert.Equal(1, await ScalarAsync<int>(conn,
                "FETCH NEXT FROM current_of_cursor"));
            await ExecAsync(conn, """
                UPDATE current_of_items
                   SET name = 'current-one'
                 WHERE CURRENT OF current_of_cursor
                """);
            Assert.Equal(2, await ScalarAsync<int>(conn,
                "FETCH NEXT FROM current_of_cursor"));
            await ExecAsync(conn, """
                DELETE FROM current_of_items
                 WHERE CURRENT OF current_of_cursor
                """);
            await ExecAsync(conn, "CLOSE current_of_cursor");
            await ExecAsync(conn, "COMMIT");
        }
        catch
        {
            await ExecAsync(conn, "ROLLBACK");
            throw;
        }

        Assert.Equal("1:current-one:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by, ',' ORDER BY id)
            FROM current_of_items
            """));
        Assert.Equal("1:old-one,2:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM current_of_items__history
            """));
        Assert.Equal("bob", await ScalarAsync<string>(conn, """
            SELECT deleted_by
            FROM current_of_items__history
            WHERE id = 2
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-one,2:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM current_of_items
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current-one", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM current_of_items
            """));
    }

    [Fact]
    public async Task Dml_SavepointRollbackRemovesTemporalHistoryRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE savepoint_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('savepoint_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO savepoint_items VALUES (1, 'old-one'), (2, 'old-two')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "BEGIN");
        try
        {
            await ExecAsync(conn, "SAVEPOINT rolled_back_changes");
            await ExecAsync(conn, """
                UPDATE savepoint_items SET name = 'rolled-back-one' WHERE id = 1;
                DELETE FROM savepoint_items WHERE id = 2;
                INSERT INTO savepoint_items VALUES (3, 'rolled-back-three');
                """);

            Assert.Equal("1:old-one:,2:old-two:bob", await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || name || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
                FROM savepoint_items__history
                """));
            Assert.Equal("1:rolled-back-one,3:rolled-back-three", await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
                FROM savepoint_items
                """));

            await ExecAsync(conn, "ROLLBACK TO SAVEPOINT rolled_back_changes");
            await ExecAsync(conn, "RELEASE SAVEPOINT rolled_back_changes");

            Assert.Equal(0L, await ScalarAsync<long>(conn,
                "SELECT count(*) FROM savepoint_items__history"));
            Assert.Equal("1:old-one,2:old-two", await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
                FROM savepoint_items
                """));

            await ExecAsync(conn, "SAVEPOINT kept_changes");
            await ExecAsync(conn, """
                UPDATE savepoint_items SET name = 'kept-one' WHERE id = 1;
                DELETE FROM savepoint_items WHERE id = 2;
                INSERT INTO savepoint_items VALUES (3, 'kept-three');
                """);
            await ExecAsync(conn, "RELEASE SAVEPOINT kept_changes");
            await ExecAsync(conn, "COMMIT");
        }
        catch
        {
            await ExecAsync(conn, "ROLLBACK");
            throw;
        }

        Assert.Equal("1:old-one:alice:,2:old-two:alice:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
            FROM savepoint_items__history
            """));
        Assert.Equal("1:kept-one:bob,3:kept-three:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by, ',' ORDER BY id)
            FROM savepoint_items
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-one:alice,2:old-two:alice", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by, ',' ORDER BY id)
            FROM savepoint_items
            """));
    }

    [Fact]
    public async Task Dml_PrimaryKeyUpdatesSplitHistoryChain()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE pk_move_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('pk_move_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO pk_move_items VALUES (1, 'old-key')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE pk_move_items SET id = 10, name = 'new-key' WHERE id = 1");

        Assert.Equal("10:new-key:bob", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || changed_by
            FROM pk_move_items
            """));
        Assert.Equal("1:old-key:alice", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || changed_by
            FROM pk_move_items__history
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-key", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name
            FROM pk_move_items
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("10:new-key", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name
            FROM pk_move_items
            """));
    }

    [Fact]
    public async Task AsOf_ObjectCreationAndCopySelectUseHistoricalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE object_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('object_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO object_items VALUES (1, 'old-one'), (2, 'old-two')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        var asOfLiteral = await ScalarAsync<string>(conn,
            "SELECT quote_literal(clock_timestamp()) || '::timestamptz'");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE object_items SET name = 'current-one' WHERE id = 1;
            DELETE FROM object_items WHERE id = 2;
            INSERT INTO object_items VALUES (3, 'current-three');
            """);

        await ExecAsync(conn, $"""
            CREATE VIEW fixed_object_items AS
            SELECT id, name
            FROM object_items
            WHERE temporal.as_of({asOfLiteral})
            """);

        Assert.Equal("1:old-one,2:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM fixed_object_items
            """));

        await SetAsOfAsync(conn, asOf!);
        await ExecAsync(conn, """
            CREATE MATERIALIZED VIEW object_items_snapshot AS
            SELECT id, name
            FROM object_items
            """);
        await ExecAsync(conn, """
            CREATE TABLE object_items_ctas AS
            SELECT id, name
            FROM object_items
            ORDER BY id
            """);
        await ExecAsync(conn, """
            CREATE UNLOGGED TABLE object_items_unlogged_ctas AS
            SELECT id, name
            FROM object_items
            ORDER BY id
            """);

        var exported = await CopyTextAsync(conn, """
            COPY (
                SELECT id, name
                FROM object_items
                ORDER BY id
            ) TO STDOUT WITH (FORMAT csv)
            """);
        Assert.Equal("1,old-one\n2,old-two", exported.Replace("\r\n", "\n").TrimEnd());

        await ExecAsync(conn, "RESET temporal.as_of");

        Assert.Equal("1:old-one,2:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM object_items_snapshot
            """));
        Assert.Equal("1:old-one,2:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM object_items_ctas
            """));
        Assert.Equal("1:old-one,2:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM object_items_unlogged_ctas
            """));

        await ExecAsync(conn, "REFRESH MATERIALIZED VIEW object_items_snapshot");

        Assert.Equal("1:current-one,3:current-three", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM object_items_snapshot
            """));

        Assert.Equal("1:old-one,2:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM fixed_object_items
            """));
    }

    [Fact]
    public async Task AsOf_ExplainPlansAndAnalyzeExecutesHistoricalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE explain_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE explain_seen (
                seq  int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                id   int NOT NULL,
                name text NOT NULL
            );
            SELECT temporal.enable('explain_items', combine_interval => interval '0');

            CREATE FUNCTION remember_explain_item(p_id int, p_name text)
            RETURNS boolean
            LANGUAGE plpgsql
            VOLATILE
            AS $$
            BEGIN
                INSERT INTO explain_seen (id, name) VALUES (p_id, p_name);
                RETURN true;
            END
            $$;
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO explain_items VALUES (1, 'old-one'), (2, 'old-two')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE explain_items SET name = 'current-one' WHERE id = 1;
            DELETE FROM explain_items WHERE id = 2;
            INSERT INTO explain_items VALUES (3, 'current-three');
            """);

        await SetAsOfAsync(conn, asOf!);

        var planRows = await QueryAsync(conn, """
            EXPLAIN (VERBOSE, COSTS OFF)
            SELECT id, name
            FROM explain_items
            WHERE id = 2
            """, r => r.GetString(0));
        var plan = string.Join('\n', planRows);

        Assert.Contains("explain_items__history", plan);
        Assert.Equal(0L, await ScalarAsync<long>(conn, "SELECT count(*) FROM explain_seen"));

        await ExecAsync(conn, """
            EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF, SUMMARY OFF)
            SELECT id
            FROM explain_items
            WHERE remember_explain_item(id, name)
            ORDER BY id
            """);

        Assert.Equal("1:old-one,2:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM explain_seen
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current-one,3:current-three", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM explain_items
            """));
    }

    [Fact]
    public async Task AsOf_SecurityBarrierViewsAndSelectIntoUseHistoricalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE barrier_items (
                id        int PRIMARY KEY,
                tenant_id int NOT NULL,
                name      text NOT NULL,
                score     int NOT NULL
            );
            SELECT temporal.enable('barrier_items', combine_interval => interval '0');

            CREATE VIEW barrier_visible_items
            WITH (security_barrier = true)
            AS
            SELECT id, tenant_id, name, score
            FROM barrier_items
            WHERE tenant_id = current_setting('app.tenant_id')::int;
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO barrier_items VALUES
                (1, 10, 'old-one', 10),
                (2, 10, 'old-two', 20),
                (3, 20, 'other-tenant-old', 30);
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE barrier_items SET name = 'current-one', score = 110 WHERE id = 1;
            DELETE FROM barrier_items WHERE id = 2;
            UPDATE barrier_items SET name = 'other-tenant-current', score = 130 WHERE id = 3;
            INSERT INTO barrier_items VALUES (4, 10, 'current-four', 40);
            """);

        await ExecAsync(conn, "SET app.tenant_id = '10'");
        await SetAsOfAsync(conn, asOf!);
        await ExecAsync(conn, """
            SELECT id, name, score
            INTO TEMP TABLE barrier_snapshot
            FROM barrier_visible_items
            WHERE id = ANY (ARRAY[1, 2, 4])
            ORDER BY id
            """);

        Assert.Equal("1:old-one:10,2:old-two:20", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || score::text, ',' ORDER BY id)
            FROM barrier_snapshot
            """));
        Assert.Equal(0L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM barrier_visible_items WHERE tenant_id = 20"));

        await ExecAsync(conn, "RESET temporal.as_of");

        Assert.Equal("1:current-one:110,4:current-four:40", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || score::text, ',' ORDER BY id)
            FROM barrier_visible_items
            """));
        Assert.Equal("1:old-one,2:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM barrier_snapshot
            """));
    }

    [Fact]
    public async Task AsOf_SecurityInvokerViewsRequireInvokerPrivilegesAndUseHistoricalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'temporal_feature_invoker_reader') THEN
                    CREATE ROLE temporal_feature_invoker_reader;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'temporal_feature_invoker_view_only') THEN
                    CREATE ROLE temporal_feature_invoker_view_only;
                END IF;
            END
            $$;

            CREATE TABLE invoker_view_items (
                id        int PRIMARY KEY,
                tenant_id int NOT NULL,
                name      text NOT NULL
            );
            GRANT USAGE ON SCHEMA public TO temporal_feature_invoker_reader, temporal_feature_invoker_view_only;
            GRANT USAGE ON SCHEMA temporal TO temporal_feature_invoker_reader, temporal_feature_invoker_view_only;
            GRANT SELECT ON invoker_view_items TO temporal_feature_invoker_reader;
            SELECT temporal.enable('invoker_view_items', combine_interval => interval '0');

            CREATE VIEW invoker_visible_items
            WITH (security_invoker = true)
            AS
            SELECT id, tenant_id, name
            FROM invoker_view_items
            WHERE tenant_id = current_setting('app.tenant_id')::int;
            GRANT SELECT ON invoker_visible_items TO temporal_feature_invoker_reader, temporal_feature_invoker_view_only;
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO invoker_view_items VALUES
                (1, 10, 'tenant-10-old'),
                (2, 20, 'tenant-20-old');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE invoker_view_items SET name = name || '-current';
            INSERT INTO invoker_view_items VALUES (3, 10, 'tenant-10-current-only');
            """);

        await ExecAsync(conn, "SET ROLE temporal_feature_invoker_reader");
        try
        {
            await ExecAsync(conn, "SET app.tenant_id = '10'");
            await SetAsOfAsync(conn, asOf!);
            Assert.Equal("1:tenant-10-old", await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
                FROM invoker_visible_items
                """));

            await ExecAsync(conn, "RESET temporal.as_of");
            Assert.Equal("1:tenant-10-old-current,3:tenant-10-current-only", await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
                FROM invoker_visible_items
                """));
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }

        await ExecAsync(conn, "SET ROLE temporal_feature_invoker_view_only");
        try
        {
            await ExecAsync(conn, "SET app.tenant_id = '10'");
            await SetAsOfAsync(conn, asOf!);
            var denied = await Assert.ThrowsAsync<PostgresException>(async () =>
                await ExecAsync(conn, "SELECT count(*) FROM invoker_visible_items"));
            Assert.Equal("42501", denied.SqlState);
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }
    }

    [Fact]
    public async Task AsOf_PostgresFdwJoinsTrackedHistoryWithForeignRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, $"""
            CREATE EXTENSION postgres_fdw;

            CREATE TABLE remote_customers (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE fdw_orders (
                id          int PRIMARY KEY,
                customer_id int NOT NULL,
                amount      int NOT NULL
            );
            SELECT temporal.enable('fdw_orders', combine_interval => interval '0');

            CREATE SERVER loopback_server
                FOREIGN DATA WRAPPER postgres_fdw
                OPTIONS (host '127.0.0.1', port '5432', dbname '{db.DbName}');
            CREATE USER MAPPING FOR CURRENT_USER
                SERVER loopback_server
                OPTIONS (user 'postgres', password 'postgres');
            CREATE FOREIGN TABLE foreign_customers (
                id   int,
                name text
            )
            SERVER loopback_server
            OPTIONS (schema_name 'public', table_name 'remote_customers');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO remote_customers VALUES (1, 'customer-old');
            INSERT INTO fdw_orders VALUES (100, 1, 10);
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE fdw_orders SET amount = 99 WHERE id = 100;
            UPDATE remote_customers SET name = 'customer-current' WHERE id = 1;
            """);

        await SetAsOfAsync(conn, asOf!);

        var historicJoin = await QuerySingleAsync(conn, """
            SELECT o.amount, c.name
            FROM fdw_orders o
            JOIN foreign_customers c ON c.id = o.customer_id
            WHERE o.id = 100
            """, r => (Amount: r.GetInt32(0), CustomerName: r.GetString(1)));

        Assert.Equal((10, "customer-current"), historicJoin);

        await ExecAsync(conn, "RESET temporal.as_of");

        var presentJoin = await QuerySingleAsync(conn, """
            SELECT o.amount, c.name
            FROM fdw_orders o
            JOIN foreign_customers c ON c.id = o.customer_id
            WHERE o.id = 100
            """, r => (Amount: r.GetInt32(0), CustomerName: r.GetString(1)));

        Assert.Equal((99, "customer-current"), presentJoin);
    }

    [Fact]
    public async Task AsOf_UntrackedPartitionedTablesJoinAndCaptureHistoricalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE partitioned_source_items (
                id     int PRIMARY KEY,
                bucket int NOT NULL,
                name   text NOT NULL
            );
            CREATE TABLE partition_filters (
                source_id int NOT NULL,
                bucket    int NOT NULL,
                label     text NOT NULL
            ) PARTITION BY RANGE (bucket);
            CREATE TABLE partition_filters_low
                PARTITION OF partition_filters FOR VALUES FROM (0) TO (100);
            CREATE TABLE partition_filters_high
                PARTITION OF partition_filters FOR VALUES FROM (100) TO (200);

            CREATE TABLE partition_exports (
                source_id int NOT NULL,
                bucket    int NOT NULL,
                name      text NOT NULL,
                label     text NOT NULL
            ) PARTITION BY RANGE (bucket);
            CREATE TABLE partition_exports_low
                PARTITION OF partition_exports FOR VALUES FROM (0) TO (100);
            CREATE TABLE partition_exports_high
                PARTITION OF partition_exports FOR VALUES FROM (100) TO (200);

            SELECT temporal.enable('partitioned_source_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO partitioned_source_items VALUES
                (1, 10, 'old-low'),
                (2, 110, 'old-high');
            INSERT INTO partition_filters VALUES
                (1, 10, 'initial-low-filter'),
                (2, 110, 'initial-high-filter');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE partitioned_source_items SET name = 'current-low' WHERE id = 1;
            DELETE FROM partitioned_source_items WHERE id = 2;
            INSERT INTO partitioned_source_items VALUES (3, 10, 'current-only');
            UPDATE partition_filters SET label = 'present-low-filter' WHERE source_id = 1;
            UPDATE partition_filters SET label = 'present-high-filter' WHERE source_id = 2;
            INSERT INTO partition_filters VALUES (3, 10, 'present-current-filter');
            """);

        await SetAsOfAsync(conn, asOf!);

        Assert.Equal("1:old-low:present-low-filter,2:old-high:present-high-filter",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(s.id::text || ':' || s.name || ':' || f.label, ',' ORDER BY s.id)
                FROM partitioned_source_items s
                JOIN partition_filters f ON f.source_id = s.id
                """));

        await ExecAsync(conn, """
            INSERT INTO partition_exports (source_id, bucket, name, label)
            SELECT s.id, s.bucket, s.name, f.label
            FROM partitioned_source_items s
            JOIN partition_filters f ON f.source_id = s.id
            ORDER BY s.id
            """);

        Assert.Equal("partition_exports_low:1:10:old-low:present-low-filter,partition_exports_high:2:110:old-high:present-high-filter",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(tableoid::regclass::text || ':' ||
                                  source_id::text || ':' ||
                                  bucket::text || ':' ||
                                  name || ':' ||
                                  label,
                                  ',' ORDER BY bucket)
                FROM partition_exports
                """));

        await ExecAsync(conn, "RESET temporal.as_of");

        Assert.Equal("1:current-low:present-low-filter,3:current-only:present-current-filter",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(s.id::text || ':' || s.name || ':' || f.label, ',' ORDER BY s.id)
                FROM partitioned_source_items s
                JOIN partition_filters f ON f.source_id = s.id
                """));
    }

    [Fact]
    public async Task AsOf_SetLocalAndSavepointRollbackControlHistoricalReads()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE local_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('local_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO local_items VALUES (1, 'old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE local_items SET name = 'current' WHERE id = 1");

        await ExecAsync(conn, "BEGIN");
        try
        {
            await SetLocalAsOfAsync(conn, asOf!);
            Assert.Equal("old", await ScalarAsync<string>(conn,
                "SELECT name FROM local_items WHERE id = 1"));
            await ExecAsync(conn, "COMMIT");
        }
        catch
        {
            await ExecAsync(conn, "ROLLBACK");
            throw;
        }

        Assert.Equal("current", await ScalarAsync<string>(conn,
            "SELECT name FROM local_items WHERE id = 1"));

        await ExecAsync(conn, "BEGIN");
        try
        {
            await ExecAsync(conn, "SAVEPOINT before_as_of");
            await SetLocalAsOfAsync(conn, asOf!);
            Assert.Equal("old", await ScalarAsync<string>(conn,
                "SELECT name FROM local_items WHERE id = 1"));
            await ExecAsync(conn, "ROLLBACK TO SAVEPOINT before_as_of");
            Assert.Equal("current", await ScalarAsync<string>(conn,
                "SELECT name FROM local_items WHERE id = 1"));
            await ExecAsync(conn, "COMMIT");
        }
        catch
        {
            await ExecAsync(conn, "ROLLBACK");
            throw;
        }
    }

    [Fact]
    public async Task AsOf_TransactionIsolationLevelsKeepPostgresSnapshotSemantics()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var setup = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var reader = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var writer = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(setup, """
            CREATE TABLE isolation_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('isolation_items', combine_interval => interval '0');
            """);

        await ExecAsync(writer, "SET temporal.user_id = 'alice'");
        await ExecAsync(writer, "INSERT INTO isolation_items VALUES (1, 'rc-old'), (2, 'rr-old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var rcAsOf = await ScalarAsync<string>(writer, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(reader, "BEGIN ISOLATION LEVEL READ COMMITTED");
        try
        {
            Assert.Equal("rc-old", await ScalarAsync<string>(reader,
                "SELECT name FROM isolation_items WHERE id = 1"));

            await ExecAsync(writer, "SET temporal.user_id = 'bob'");
            await ExecAsync(writer, "UPDATE isolation_items SET name = 'rc-current' WHERE id = 1");

            Assert.Equal("rc-current", await ScalarAsync<string>(reader,
                "SELECT name FROM isolation_items WHERE id = 1"));

            await SetLocalAsOfAsync(reader, rcAsOf!);
            Assert.Equal("rc-old", await ScalarAsync<string>(reader,
                "SELECT name FROM isolation_items WHERE id = 1"));

            await ExecAsync(reader, "RESET temporal.as_of");
            Assert.Equal("rc-current", await ScalarAsync<string>(reader,
                "SELECT name FROM isolation_items WHERE id = 1"));

            await ExecAsync(reader, "COMMIT");
        }
        catch
        {
            await ExecAsync(reader, "ROLLBACK");
            throw;
        }

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var rrAsOfBeforeUpdate = await ScalarAsync<string>(writer, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(reader, "BEGIN ISOLATION LEVEL REPEATABLE READ");
        try
        {
            Assert.Equal("rr-old", await ScalarAsync<string>(reader,
                "SELECT name FROM isolation_items WHERE id = 2"));

            await ExecAsync(writer, "SET temporal.user_id = 'carol'");
            await ExecAsync(writer, "UPDATE isolation_items SET name = 'rr-current' WHERE id = 2");
            var rrAsOfAfterUpdate = await ScalarAsync<string>(writer, "SELECT clock_timestamp()::text");

            Assert.Equal("rr-old", await ScalarAsync<string>(reader,
                "SELECT name FROM isolation_items WHERE id = 2"));

            await SetLocalAsOfAsync(reader, rrAsOfAfterUpdate!);
            Assert.Equal("rr-old", await ScalarAsync<string>(reader,
                "SELECT name FROM isolation_items WHERE id = 2"));

            await ExecAsync(reader, "COMMIT");

            await SetAsOfAsync(setup, rrAsOfBeforeUpdate!);
            Assert.Equal("rr-old", await ScalarAsync<string>(setup,
                "SELECT name FROM isolation_items WHERE id = 2"));

            await SetAsOfAsync(setup, rrAsOfAfterUpdate!);
            Assert.Equal("rr-current", await ScalarAsync<string>(setup,
                "SELECT name FROM isolation_items WHERE id = 2"));
        }
        catch
        {
            await ExecAsync(reader, "ROLLBACK");
            throw;
        }
    }

    [Fact]
    public async Task AsOf_SqlFunctionsAndMaterializedViewsUseHistoricalSnapshot()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE routine_items (
                id    int PRIMARY KEY,
                name  text NOT NULL,
                score int NOT NULL
            );
            SELECT temporal.enable('routine_items', combine_interval => interval '0');

            CREATE FUNCTION routine_item_name(p_id int)
            RETURNS text
            LANGUAGE sql
            STABLE
            AS $$
                SELECT name
                FROM routine_items
                WHERE id = p_id
            $$;

            CREATE MATERIALIZED VIEW routine_item_snapshot AS
            SELECT id, name, score
            FROM routine_items
            WITH NO DATA;
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO routine_items VALUES (1, 'old-name', 10)");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE routine_items SET name = 'current-name', score = 20 WHERE id = 1");

        Assert.Equal("current-name", await ScalarAsync<string>(conn,
            "SELECT routine_item_name(1)"));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("old-name", await ScalarAsync<string>(conn,
            "SELECT routine_item_name(1)"));

        await ExecAsync(conn, "REFRESH MATERIALIZED VIEW routine_item_snapshot");
        await ExecAsync(conn, "RESET temporal.as_of");

        var historicalSnapshot = await QuerySingleAsync(conn,
            "SELECT name, score FROM routine_item_snapshot WHERE id = 1",
            r => (Name: r.GetString(0), Score: r.GetInt32(1)));
        Assert.Equal(("old-name", 10), historicalSnapshot);

        await ExecAsync(conn, "REFRESH MATERIALIZED VIEW routine_item_snapshot");
        var presentSnapshot = await QuerySingleAsync(conn,
            "SELECT name, score FROM routine_item_snapshot WHERE id = 1",
            r => (Name: r.GetString(0), Score: r.GetInt32(1)));
        Assert.Equal(("current-name", 20), presentSnapshot);

        await ExecAsync(conn, "CREATE UNIQUE INDEX routine_item_snapshot_id_idx ON routine_item_snapshot (id)");

        await SetAsOfAsync(conn, asOf!);
        await ExecAsync(conn, "REFRESH MATERIALIZED VIEW CONCURRENTLY routine_item_snapshot");
        await ExecAsync(conn, "RESET temporal.as_of");

        var historicalConcurrentSnapshot = await QuerySingleAsync(conn,
            "SELECT name, score FROM routine_item_snapshot WHERE id = 1",
            r => (Name: r.GetString(0), Score: r.GetInt32(1)));
        Assert.Equal(("old-name", 10), historicalConcurrentSnapshot);

        await ExecAsync(conn, "REFRESH MATERIALIZED VIEW CONCURRENTLY routine_item_snapshot");
        var presentConcurrentSnapshot = await QuerySingleAsync(conn,
            "SELECT name, score FROM routine_item_snapshot WHERE id = 1",
            r => (Name: r.GetString(0), Score: r.GetInt32(1)));
        Assert.Equal(("current-name", 20), presentConcurrentSnapshot);
    }

    [Fact]
    public async Task AsOf_UserDefinedAggregatesUseHistoricalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE aggregate_items (
                id       int PRIMARY KEY,
                category text NOT NULL,
                label    text NOT NULL,
                qty      int NOT NULL
            );
            SELECT temporal.enable('aggregate_items', combine_interval => interval '0');

            CREATE FUNCTION temporal_label_state(state text, val text)
            RETURNS text
            LANGUAGE sql
            IMMUTABLE
            AS $$
                SELECT CASE
                           WHEN val IS NULL THEN state
                           WHEN state IS NULL OR state = '' THEN val
                           ELSE state || '|' || val
                       END
            $$;

            CREATE AGGREGATE temporal_label_concat(text) (
                SFUNC = temporal_label_state,
                STYPE = text
            );
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO aggregate_items VALUES
                (1, 'core', 'old-alpha', 10),
                (2, 'core', 'old-beta', 20),
                (3, 'edge', 'old-gamma', 30);
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE aggregate_items SET label = 'current-alpha', qty = 110 WHERE id = 1;
            DELETE FROM aggregate_items WHERE id = 2;
            INSERT INTO aggregate_items VALUES (4, 'core', 'current-delta', 40);
            """);

        await SetAsOfAsync(conn, asOf!);
        var historical = await QuerySingleAsync(conn, """
            SELECT temporal_label_concat(label ORDER BY id),
                   temporal_label_concat(label ORDER BY id) FILTER (WHERE qty >= 20),
                   sum(qty)
            FROM aggregate_items
            WHERE category = 'core'
            """, r => (AllLabels: r.GetString(0), FilteredLabels: r.GetString(1), TotalQty: r.GetInt64(2)));
        Assert.Equal(("old-alpha|old-beta", "old-beta", 30L), historical);

        await ExecAsync(conn, "RESET temporal.as_of");
        var current = await QuerySingleAsync(conn, """
            SELECT temporal_label_concat(label ORDER BY id),
                   temporal_label_concat(label ORDER BY id) FILTER (WHERE qty >= 40),
                   sum(qty)
            FROM aggregate_items
            WHERE category = 'core'
            """, r => (AllLabels: r.GetString(0), FilteredLabels: r.GetString(1), TotalQty: r.GetInt64(2)));
        Assert.Equal(("current-alpha|current-delta", "current-alpha|current-delta", 150L), current);
    }

    [Fact]
    public async Task AsOf_JoinVariantsSetOperationsAndRecursiveCte_TimeTravel()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE left_items (
                id    int PRIMARY KEY,
                label text NOT NULL
            );
            CREATE TABLE right_items (
                id      int PRIMARY KEY,
                left_id int NOT NULL,
                label   text NOT NULL
            );
            CREATE TABLE lookup_items (
                id    int PRIMARY KEY,
                label text NOT NULL
            );
            CREATE TABLE set_a (
                id    int PRIMARY KEY,
                label text NOT NULL
            );
            CREATE TABLE set_b (
                id    int PRIMARY KEY,
                label text NOT NULL
            );
            CREATE TABLE syntax_left (
                id        int PRIMARY KEY,
                label     text NOT NULL,
                left_note text NOT NULL
            );
            CREATE TABLE syntax_right (
                id         int PRIMARY KEY,
                label      text NOT NULL,
                right_note text NOT NULL
            );
            CREATE TABLE graph_edges (
                parent_id  int NOT NULL,
                child_id   int NOT NULL,
                edge_label text NOT NULL,
                PRIMARY KEY (parent_id, child_id)
            );
            SELECT temporal.enable('left_items', combine_interval => interval '0');
            SELECT temporal.enable('right_items', combine_interval => interval '0');
            SELECT temporal.enable('lookup_items', combine_interval => interval '0');
            SELECT temporal.enable('set_a', combine_interval => interval '0');
            SELECT temporal.enable('set_b', combine_interval => interval '0');
            SELECT temporal.enable('syntax_left', combine_interval => interval '0');
            SELECT temporal.enable('syntax_right', combine_interval => interval '0');
            SELECT temporal.enable('graph_edges', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO left_items VALUES
                (1, 'left-old'),
                (2, 'left-only-old');
            INSERT INTO right_items VALUES
                (10, 1,  'right-old'),
                (20, 99, 'right-only-old');
            INSERT INTO lookup_items VALUES (1, 'lookup-old');
            INSERT INTO set_a VALUES
                (1, 'a-only-old'),
                (2, 'common-old');
            INSERT INTO set_b VALUES
                (1, 'common-old'),
                (2, 'b-only-old');
            INSERT INTO syntax_left VALUES
                (1, 'syntax-old', 'left-old'),
                (2, 'syntax-left-only-old', 'left-only-old');
            INSERT INTO syntax_right VALUES
                (1, 'syntax-old', 'right-old'),
                (3, 'syntax-right-only-old', 'right-only-old');
            INSERT INTO graph_edges VALUES
                (1, 2, 'edge-old-12'),
                (2, 3, 'edge-old-23'),
                (3, 1, 'edge-old-31');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE left_items SET label = label || '-current';
            UPDATE right_items SET label = label || '-current';
            UPDATE lookup_items SET label = label || '-current';
            UPDATE set_a SET label = label || '-current';
            UPDATE set_b SET label = label || '-current';
            UPDATE syntax_left SET label = label || '-current', left_note = left_note || '-current';
            UPDATE syntax_right SET label = label || '-current', right_note = right_note || '-current';
            UPDATE graph_edges SET edge_label = edge_label || '-current';
            DELETE FROM graph_edges WHERE parent_id = 2 AND child_id = 3;
            INSERT INTO left_items VALUES (3, 'left-new-current');
            INSERT INTO right_items VALUES (30, 3, 'right-new-current');
            INSERT INTO set_a VALUES (3, 'a-new-current');
            INSERT INTO set_b VALUES (3, 'b-new-current');
            INSERT INTO syntax_left VALUES (4, 'syntax-left-new-current', 'left-new-current');
            INSERT INTO syntax_right VALUES (4, 'syntax-left-new-current', 'right-new-current');
            INSERT INTO graph_edges VALUES (3, 4, 'edge-new-current');
            """);

        await SetAsOfAsync(conn, asOf!);

        var innerJoin = await QuerySingleAsync(conn, """
            SELECT l.label, r.label
            FROM left_items l
            INNER JOIN right_items r ON r.left_id = l.id
            WHERE l.id = 1
            """, r => (Left: r.GetString(0), Right: r.GetString(1)));
        Assert.Equal(("left-old", "right-old"), innerJoin);

        var leftJoin = await QuerySingleAsync(conn, """
            SELECT l.label, r.label IS NULL
            FROM left_items l
            LEFT JOIN right_items r ON r.left_id = l.id
            WHERE l.id = 2
            """, r => (Left: r.GetString(0), RightIsNull: r.GetBoolean(1)));
        Assert.Equal(("left-only-old", true), leftJoin);

        var rightJoin = await QuerySingleAsync(conn, """
            SELECT l.label IS NULL, r.label
            FROM left_items l
            RIGHT JOIN right_items r ON r.left_id = l.id
            WHERE r.id = 20
            """, r => (LeftIsNull: r.GetBoolean(0), Right: r.GetString(1)));
        Assert.Equal((true, "right-only-old"), rightJoin);

        var fullJoin = await ScalarAsync<string>(conn, """
            SELECT string_agg(
                       coalesce(l.label, '<none>') || '/' || coalesce(r.label, '<none>'),
                       ';' ORDER BY coalesce(l.id, r.left_id), r.id)
            FROM left_items l
            FULL JOIN right_items r ON r.left_id = l.id
            """);
        Assert.Equal("left-old/right-old;left-only-old/<none>;<none>/right-only-old", fullJoin);

        var crossJoin = await QuerySingleAsync(conn, """
            SELECT l.label, x.label
            FROM left_items l
            CROSS JOIN lookup_items x
            WHERE l.id = 1 AND x.id = 1
            """, r => (Left: r.GetString(0), Lookup: r.GetString(1)));
        Assert.Equal(("left-old", "lookup-old"), crossJoin);

        var tableSyntax = await QueryAsync(conn,
            "TABLE syntax_left ORDER BY id",
            r => $"{r.GetInt32(0)}:{r.GetString(1)}:{r.GetString(2)}");
        Assert.Equal(["1:syntax-old:left-old", "2:syntax-left-only-old:left-only-old"], tableSyntax);

        var onlySyntax = await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || label, ',' ORDER BY id)
            FROM ONLY syntax_left
            """);
        Assert.Equal("1:syntax-old,2:syntax-left-only-old", onlySyntax);

        var usingJoin = await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || label || ':' || left_note || ':' || right_note, ',' ORDER BY id)
            FROM syntax_left
            JOIN syntax_right USING (id, label)
            """);
        Assert.Equal("1:syntax-old:left-old:right-old", usingJoin);

        var naturalJoin = await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || label || ':' || left_note || ':' || right_note, ',' ORDER BY id)
            FROM syntax_left
            NATURAL JOIN syntax_right
            """);
        Assert.Equal("1:syntax-old:left-old:right-old", naturalJoin);

        var valuesJoin = await ScalarAsync<string>(conn, """
            SELECT string_agg(l.id::text || ':' || l.label || ':' || v.marker, ',' ORDER BY l.id)
            FROM syntax_left l
            JOIN (VALUES (1, 'from-values'), (4, 'current-only')) AS v(id, marker) ON v.id = l.id
            """);
        Assert.Equal("1:syntax-old:from-values", valuesJoin);

        var union = await QueryAsync(conn, """
            SELECT label FROM set_a
            UNION
            SELECT label FROM set_b
            ORDER BY label
            """, r => r.GetString(0));
        Assert.Equal(["a-only-old", "b-only-old", "common-old"], union);

        var intersect = await QueryAsync(conn, """
            SELECT label FROM set_a
            INTERSECT
            SELECT label FROM set_b
            ORDER BY label
            """, r => r.GetString(0));
        Assert.Equal(["common-old"], intersect);

        var except = await QueryAsync(conn, """
            SELECT label FROM set_a
            EXCEPT
            SELECT label FROM set_b
            ORDER BY label
            """, r => r.GetString(0));
        Assert.Equal(["a-only-old"], except);

        var recursive = await ScalarAsync<string>(conn, """
            WITH RECURSIVE walk(id, label, depth) AS (
                SELECT id, label, 1
                FROM left_items
                WHERE id = 1
                UNION ALL
                SELECT l.id, l.label, w.depth + 1
                FROM walk w
                JOIN left_items l ON l.id = w.id + 1
                WHERE w.depth < 2
            )
            SELECT string_agg(label, ',' ORDER BY id)
            FROM walk
            """);
        Assert.Equal("left-old,left-only-old", recursive);

        var recursiveSearchAndCycle = await ScalarAsync<string>(conn, """
            WITH RECURSIVE walk(parent_id, child_id, edge_label, depth) AS (
                SELECT parent_id, child_id, edge_label, 1
                FROM graph_edges
                WHERE parent_id = 1
                UNION ALL
                SELECT e.parent_id, e.child_id, e.edge_label, w.depth + 1
                FROM walk w
                JOIN graph_edges e ON e.parent_id = w.child_id
                WHERE w.depth < 4
            )
            SEARCH DEPTH FIRST BY child_id SET dfs_order
            CYCLE child_id SET is_cycle USING cycle_path
            SELECT string_agg(
                       parent_id::text || '>' || child_id::text || ':' || edge_label || ':' || is_cycle::text,
                       ',' ORDER BY depth, parent_id, child_id)
            FROM walk
            """);
        Assert.Equal(
            "1>2:edge-old-12:false,2>3:edge-old-23:false,3>1:edge-old-31:false,1>2:edge-old-12:true",
            recursiveSearchAndCycle);
    }

    [Fact]
    public async Task AsOf_SchemaQuotedJsonArrayValuesGroupingAndSamplingBehave()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE SCHEMA "Feature Schema";
            CREATE TABLE "Feature Schema"."Mixed Case Items" (
                "Id"        int PRIMARY KEY,
                "Tenant Id" int NOT NULL,
                title       text NOT NULL,
                payload     jsonb NOT NULL,
                tags        text[] NOT NULL,
                amount      numeric NOT NULL
            );
            SELECT temporal.enable('"Feature Schema"."Mixed Case Items"', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO "Feature Schema"."Mixed Case Items"
                ("Id", "Tenant Id", title, payload, tags, amount)
            VALUES
                (1, 7, 'old-one', '{"state":"old","kind":"invoice"}', ARRAY['blue','paid'], 10),
                (2, 7, 'old-two', '{"state":"old","kind":"invoice"}', ARRAY['red'], 20);
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE "Feature Schema"."Mixed Case Items"
               SET title = title || '-current',
                   payload = jsonb_set(payload, '{state}', '"current"'),
                   tags = tags || ARRAY['current']::text[],
                   amount = amount + 100;

            INSERT INTO "Feature Schema"."Mixed Case Items"
                ("Id", "Tenant Id", title, payload, tags, amount)
            VALUES
                (3, 8, 'new-current', '{"state":"current","kind":"invoice"}', ARRAY['green'], 30);
            """);

        await SetAsOfAsync(conn, asOf!);

        var historic = await QuerySingleAsync(conn, """
            WITH requested(id) AS (
                VALUES (1), (2), (3)
            ),
            historic AS (
                SELECT m."Id", m."Tenant Id", m.title, m.payload, m.tags, m.amount
                FROM "Feature Schema"."Mixed Case Items" m
                JOIN requested r ON r.id = m."Id"
                WHERE m.payload @> '{"state":"old"}'::jsonb
            ),
            top_per_tenant AS (
                SELECT DISTINCT ON ("Tenant Id") "Tenant Id", amount
                FROM historic
                ORDER BY "Tenant Id", amount DESC
            ),
            grouped AS (
                SELECT "Tenant Id", count(*) AS total
                FROM historic
                GROUP BY GROUPING SETS (("Tenant Id"), ())
            )
            SELECT
                (SELECT string_agg(title, ',' ORDER BY "Id") FROM historic),
                (SELECT count(*) FROM historic WHERE tags @> ARRAY['blue']::text[]),
                (SELECT payload->>'state' FROM historic WHERE "Id" = 1),
                (SELECT string_agg("Tenant Id"::text || ':' || amount::text, ',' ORDER BY "Tenant Id") FROM top_per_tenant),
                (SELECT string_agg(coalesce("Tenant Id"::text, 'all') || ':' || total::text, ',' ORDER BY "Tenant Id" NULLS LAST) FROM grouped)
            """, r => (
                Titles: r.GetString(0),
                BlueCount: r.GetInt64(1),
                State: r.GetString(2),
                TopPerTenant: r.GetString(3),
                Grouped: r.GetString(4)));

        Assert.Equal("old-one,old-two", historic.Titles);
        Assert.Equal(1L, historic.BlueCount);
        Assert.Equal("old", historic.State);
        Assert.Equal("7:20", historic.TopPerTenant);
        Assert.Equal("7:2,all:2", historic.Grouped);

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, """
                SELECT count(*)
                FROM "Feature Schema"."Mixed Case Items" TABLESAMPLE BERNOULLI (100)
                """));
        Assert.Equal("0A000", ex.SqlState);

        await ExecAsync(conn, "RESET temporal.as_of");
        var presentSampleCount = await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM "Feature Schema"."Mixed Case Items" TABLESAMPLE BERNOULLI (100)
            """);
        Assert.Equal(3L, presentSampleCount);
    }

    [Fact]
    public async Task AsOf_SearchPathResolvesSameNamedTrackedTablesPerStatement()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE SCHEMA tenant_a;
            CREATE SCHEMA tenant_b;
            CREATE TABLE tenant_a.items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE tenant_b.items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('tenant_a.items', combine_interval => interval '0');
            SELECT temporal.enable('tenant_b.items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO tenant_a.items VALUES (1, 'tenant-a-old'), (2, 'tenant-a-second-old');
            INSERT INTO tenant_b.items VALUES (1, 'tenant-b-old'), (3, 'tenant-b-third-old');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE tenant_a.items SET name = 'tenant-a-current' WHERE id = 1;
            DELETE FROM tenant_a.items WHERE id = 2;
            INSERT INTO tenant_a.items VALUES (4, 'tenant-a-current-only');

            UPDATE tenant_b.items SET name = 'tenant-b-current' WHERE id = 1;
            DELETE FROM tenant_b.items WHERE id = 3;
            INSERT INTO tenant_b.items VALUES (5, 'tenant-b-current-only');
            """);

        await SetAsOfAsync(conn, asOf!);
        await ExecAsync(conn, "SET search_path = tenant_a, public");
        Assert.Equal("1:tenant-a-old,2:tenant-a-second-old", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM items
            """));
        Assert.Equal("tenant-a-old|tenant-b-old", await ScalarAsync<string>(conn, """
            SELECT a.name || '|' || b.name
            FROM items a
            JOIN tenant_b.items b USING (id)
            WHERE a.id = 1
            """));

        await ExecAsync(conn, "SET search_path = tenant_b, public");
        Assert.Equal("1:tenant-b-old,3:tenant-b-third-old", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM items
            """));
        Assert.Equal("tenant-b-old|tenant-a-old", await ScalarAsync<string>(conn, """
            SELECT b.name || '|' || a.name
            FROM items b
            JOIN tenant_a.items a USING (id)
            WHERE b.id = 1
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:tenant-b-current,5:tenant-b-current-only", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM items
            """));

        await ExecAsync(conn, "SET search_path = tenant_a, public");
        Assert.Equal("1:tenant-a-current,4:tenant-a-current-only", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM items
            """));
    }

    [Fact]
    public async Task AsOf_AnalyticsJsonPathUnnestAndRollupUseHistoricalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE analytics_items (
                id        int PRIMARY KEY,
                tenant_id int NOT NULL,
                amount    numeric NOT NULL,
                tags      text[] NOT NULL,
                payload   jsonb NOT NULL
            );
            SELECT temporal.enable('analytics_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO analytics_items VALUES
                (1, 10, 10, ARRAY['red','vip']::text[], '{"state":"old","score":1}'),
                (2, 10, 30, ARRAY['blue']::text[],      '{"state":"old","score":3}'),
                (3, 20, 50, ARRAY['red']::text[],       '{"state":"old","score":5}');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE analytics_items
               SET amount = amount + 100,
                   tags = tags || ARRAY['current']::text[],
                   payload = jsonb_set(payload, '{state}', '"current"');
            DELETE FROM analytics_items WHERE id = 2;
            INSERT INTO analytics_items VALUES
                (4, 20, 70, ARRAY['green','current']::text[], '{"state":"current","score":7}');
            """);

        await SetAsOfAsync(conn, asOf!);

        var historicAnalytics = await QuerySingleAsync(conn, """
            WITH historic AS (
                SELECT id,
                       tenant_id,
                       amount,
                       tags,
                       payload,
                       lag(amount) OVER (PARTITION BY tenant_id ORDER BY amount) AS previous_amount
                FROM analytics_items
                WHERE jsonb_path_exists(payload, '$ ? (@.state == "old")')
            ),
            tagged AS (
                SELECT h.id, u.tag, u.ord
                FROM historic h
                CROSS JOIN LATERAL unnest(h.tags) WITH ORDINALITY AS u(tag, ord)
            ),
            tag_counts AS (
                SELECT tag, count(*) AS total
                FROM tagged
                GROUP BY tag
            ),
            rollup AS (
                SELECT tenant_id,
                       count(*) FILTER (WHERE amount >= 30) AS high_count,
                       sum(amount) AS total_amount
                FROM historic
                GROUP BY ROLLUP (tenant_id)
            )
            SELECT
                (SELECT string_agg(id::text || ':' || coalesce(previous_amount::text, 'none'), ',' ORDER BY id)
                 FROM historic),
                (SELECT string_agg(tag || ':' || total::text, ',' ORDER BY tag)
                 FROM tag_counts),
                (SELECT string_agg(
                            coalesce(tenant_id::text, 'all') || ':' ||
                            high_count::text || ':' ||
                            total_amount::text,
                            ',' ORDER BY tenant_id NULLS LAST)
                 FROM rollup),
                (SELECT percentile_disc(0.5) WITHIN GROUP (ORDER BY amount)::numeric::text
                 FROM historic),
                (SELECT string_agg(id::text, ',' ORDER BY id)
                 FROM historic)
            """, r => (
                WindowRows: r.GetString(0),
                TagCounts: r.GetString(1),
                RollupRows: r.GetString(2),
                Median: r.GetString(3),
                HistoricIds: r.GetString(4)));

        Assert.Equal("1:none,2:10,3:none", historicAnalytics.WindowRows);
        Assert.Equal("blue:1,red:2,vip:1", historicAnalytics.TagCounts);
        Assert.Equal("10:1:40,20:1:50,all:2:90", historicAnalytics.RollupRows);
        Assert.Equal("30", historicAnalytics.Median);
        Assert.Equal("1,2,3", historicAnalytics.HistoricIds);

        var advancedAnalytics = await QuerySingleAsync(conn, """
            WITH historic AS (
                SELECT id,
                       tenant_id,
                       amount,
                       tags
                FROM analytics_items
                WHERE jsonb_path_exists(payload, '$ ? (@.state == "old")')
            ),
            row_sources AS (
                SELECT h.id,
                       f.tag,
                       f.pos,
                       f.ord
                FROM historic h
                CROSS JOIN LATERAL ROWS FROM (
                    unnest(h.tags),
                    generate_series(1, cardinality(h.tags))
                ) WITH ORDINALITY AS f(tag, pos, ord)
            ),
            running AS (
                SELECT id,
                       sum(amount) OVER tenant_running AS running_amount
                FROM historic
                WINDOW tenant_running AS (
                    PARTITION BY tenant_id
                    ORDER BY id
                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                )
            ),
            classified AS (
                SELECT tenant_id,
                       amount >= 30 AS high_amount
                FROM historic
            ),
            cube_rows AS (
                SELECT tenant_id,
                       high_amount,
                       GROUPING(tenant_id) AS tenant_grouped,
                       GROUPING(high_amount) AS high_grouped,
                       count(*) AS total
                FROM classified
                GROUP BY CUBE (tenant_id, high_amount)
            )
            SELECT
                (SELECT string_agg(id::text || ':' || tag || ':' || pos::text || ':' || ord::text, ',' ORDER BY id, ord)
                 FROM row_sources),
                (SELECT string_agg(id::text || ':' || running_amount::text, ',' ORDER BY id)
                 FROM running),
                (SELECT string_agg(
                            coalesce(tenant_id::text, 'all') || ':' ||
                            coalesce(high_amount::text, 'all') || ':' ||
                            tenant_grouped::text || high_grouped::text || ':' ||
                            total::text,
                            ',' ORDER BY tenant_id NULLS LAST, high_amount NULLS LAST, tenant_grouped, high_grouped)
                 FROM cube_rows)
            """, r => (
                RowsFrom: r.GetString(0),
                Running: r.GetString(1),
                Cube: r.GetString(2)));

        Assert.Equal("1:red:1:1,1:vip:2:2,2:blue:1:1,3:red:1:1", advancedAnalytics.RowsFrom);
        Assert.Equal("1:10,2:40,3:50", advancedAnalytics.Running);
        Assert.Equal(
            "10:false:00:1,10:true:00:1,10:all:01:2," +
            "20:true:00:1,20:all:01:1," +
            "all:false:10:1,all:true:10:2,all:all:11:3",
            advancedAnalytics.Cube);

        await ExecAsync(conn, "RESET temporal.as_of");

        Assert.Equal(0L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM analytics_items
            WHERE jsonb_path_exists(payload, '$ ? (@.state == "old")')
            """));
        Assert.Equal("1:110,3:150,4:70", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || amount::text, ',' ORDER BY id)
            FROM analytics_items
            """));
    }

    [Fact]
    public async Task Dml_JsonbPathOpsGinIndex_WorkWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE jsonb_index_items (
                id      int PRIMARY KEY,
                payload jsonb NOT NULL
            );
            CREATE INDEX jsonb_index_items_payload_path_idx
                ON jsonb_index_items USING gin (payload jsonb_path_ops);
            SELECT temporal.enable('jsonb_index_items', include_indexes => true, combine_interval => interval '0');
            """);

        var mirrorDefinition = await QuerySingleAsync(conn, """
            SELECT pg_get_indexdef(history_index_oid)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'jsonb_index_items'::regclass
              AND base_index_oid = 'jsonb_index_items_payload_path_idx'::regclass
            """, r => r.GetString(0));
        Assert.Contains("USING gin", mirrorDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("jsonb_path_ops", mirrorDefinition, StringComparison.OrdinalIgnoreCase);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO jsonb_index_items VALUES
                (1, '{"state":"old","tenant":10,"tags":["red","audit"],"meta":{"score":1}}'),
                (2, '{"state":"old","tenant":20,"tags":["blue"],"meta":{"score":2}}');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE jsonb_index_items
               SET payload = jsonb_set(payload, '{state}', '"current"')
             WHERE id = 1;
            DELETE FROM jsonb_index_items WHERE id = 2;
            INSERT INTO jsonb_index_items VALUES
                (3, '{"state":"current","tenant":10,"tags":["green"],"meta":{"score":3}}');
            """);

        Assert.Equal("1:old:alice,2:old:alice", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || (payload ->> 'state') || ':' || changed_by, ',' ORDER BY id)
            FROM jsonb_index_items__history
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old:10,2:old:20", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || (payload ->> 'state') || ':' || (payload ->> 'tenant'), ',' ORDER BY id)
            FROM jsonb_index_items
            WHERE payload @> '{"state":"old"}'::jsonb
              AND jsonb_path_exists(payload, '$.tags[*] ? (@ == "red" || @ == "blue")')
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current:10,3:current:10", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || (payload ->> 'state') || ':' || (payload ->> 'tenant'), ',' ORDER BY id)
            FROM jsonb_index_items
            WHERE payload @> '{"state":"current","tenant":10}'::jsonb
            """));
    }

    [Fact]
    public async Task AsOf_PredicatesHavingOrderingAndFetchUseHistoricalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE predicate_items (
                id       int PRIMARY KEY,
                group_id int NOT NULL,
                label    text NOT NULL,
                score    int,
                marker   text NOT NULL
            );
            SELECT temporal.enable('predicate_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO predicate_items VALUES
                (1, 1, 'alpha', 10, 'old'),
                (2, 1, 'Beta', NULL, 'old'),
                (3, 2, 'gamma', 30, 'old'),
                (4, 2, 'delta', 40, 'old');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE predicate_items
               SET label = label || '-current',
                   score = coalesce(score, 0) + 100,
                   marker = 'current';
            DELETE FROM predicate_items WHERE id = 2;
            INSERT INTO predicate_items VALUES (5, 1, 'epsilon-current', 5, 'current');
            """);

        await SetAsOfAsync(conn, asOf!);

        var historicRows = await ScalarAsync<string>(conn, """
            WITH filtered AS (
                SELECT *
                FROM predicate_items p
                WHERE p.id = ANY (ARRAY[1, 2, 3, 5])
                  AND p.id < ALL (ARRAY[9, 10])
                  AND p.label COLLATE "C" SIMILAR TO '(alpha|Beta|gamma)%'
                  AND p.score IS DISTINCT FROM 999
                  AND EXISTS (
                      SELECT 1
                      FROM predicate_items e
                      WHERE e.group_id = p.group_id
                        AND e.marker = 'old'
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM predicate_items n
                      WHERE n.id = p.id
                        AND n.marker = 'current'
                  )
            ),
            qualified_groups AS (
                SELECT group_id
                FROM filtered
                GROUP BY group_id
                HAVING count(*) FILTER (WHERE score IS NULL) = 1
                    OR max(score) >= 30
            )
            SELECT string_agg(id::text || ':' || label || ':' || coalesce(score::text, 'null'), ',' ORDER BY score DESC NULLS LAST, label COLLATE "C")
            FROM (
                SELECT f.id, f.label, f.score
                FROM filtered f
                JOIN qualified_groups q USING (group_id)
                ORDER BY f.score DESC NULLS LAST, f.label COLLATE "C"
                OFFSET 0 ROWS FETCH FIRST 3 ROWS ONLY
            ) page
            """);

        Assert.Equal("3:gamma:30,1:alpha:10,2:Beta:null", historicRows);

        await ExecAsync(conn, "RESET temporal.as_of");

        Assert.Equal(0L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM predicate_items WHERE marker = 'old'"));
        Assert.Equal("1:alpha-current:110,3:gamma-current:130,4:delta-current:140,5:epsilon-current:5",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || label || ':' || score::text, ',' ORDER BY id)
                FROM predicate_items
                """));
    }

    [Fact]
    public async Task Dml_PostgresSpecificTypesAndExpressionIndexes_WorkWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TYPE task_state AS ENUM ('open', 'closed');
            CREATE DOMAIN positive_amount AS numeric CHECK (VALUE >= 0);
            CREATE TYPE postal_address AS (
                city text,
                zip  text
            );

            CREATE TABLE typed_items (
                id            int PRIMARY KEY,
                state         task_state NOT NULL,
                budget        positive_amount NOT NULL,
                active_period tstzrange NOT NULL,
                address       postal_address NOT NULL
            );

            CREATE INDEX typed_items_city_idx
                ON typed_items ((lower((address).city)));
            CREATE INDEX typed_items_open_budget_idx
                ON typed_items (budget)
                WHERE state = 'open';

            SELECT temporal.enable('typed_items', include_indexes => true, combine_interval => interval '0');
            """);

        var mirroredIndexCount = await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'typed_items'::regclass
              AND base_index_oid IN ('typed_items_city_idx'::regclass, 'typed_items_open_budget_idx'::regclass)
            """);
        Assert.Equal(2L, mirroredIndexCount);

        var mirroredDefinitions = await QueryAsync(conn, """
            SELECT pg_get_indexdef(history_index_oid)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'typed_items'::regclass
              AND base_index_oid IN ('typed_items_city_idx'::regclass, 'typed_items_open_budget_idx'::regclass)
            ORDER BY pg_get_indexdef(history_index_oid)
            """, r => r.GetString(0));
        Assert.Contains(mirroredDefinitions, d => d.Contains("lower", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mirroredDefinitions, d => d.Contains("WHERE", StringComparison.OrdinalIgnoreCase));

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO typed_items
                (id, state, budget, active_period, address)
            VALUES
                (1, 'open', 10, tstzrange('2026-01-01+00', '2026-02-01+00', '[)'),
                 ROW('Old City', '1000')::postal_address)
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE typed_items
               SET state = 'closed',
                   budget = 20,
                   active_period = tstzrange('2026-03-01+00', '2026-04-01+00', '[)'),
                   address = ROW('Current City', '2000')::postal_address
             WHERE id = 1
            """);

        await SetAsOfAsync(conn, asOf!);
        var historic = await QuerySingleAsync(conn, """
            SELECT state::text,
                   budget::numeric,
                   lower(active_period)::date::text,
                   upper(active_period)::date::text,
                   (address).city,
                   (address).zip
            FROM typed_items
            WHERE id = 1
            """, r => (
                State: r.GetString(0),
                Budget: r.GetDecimal(1),
                Lower: r.GetString(2),
                Upper: r.GetString(3),
                City: r.GetString(4),
                Zip: r.GetString(5)));

        Assert.Equal(("open", 10m, "2026-01-01", "2026-02-01", "Old City", "1000"), historic);

        await ExecAsync(conn, "RESET temporal.as_of");
        var present = await QuerySingleAsync(conn, """
            SELECT state::text,
                   budget::numeric,
                   lower(active_period)::date::text,
                   upper(active_period)::date::text,
                   (address).city,
                   (address).zip
            FROM typed_items
            WHERE lower((address).city) = 'current city'
            """, r => (
                State: r.GetString(0),
                Budget: r.GetDecimal(1),
                Lower: r.GetString(2),
                Upper: r.GetString(3),
                City: r.GetString(4),
                Zip: r.GetString(5)));

        Assert.Equal(("closed", 20m, "2026-03-01", "2026-04-01", "Current City", "2000"), present);
    }

    [Fact]
    public async Task Dml_MultirangeTypesAndGistIndexes_WorkWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE multirange_items (
                id          int PRIMARY KEY,
                label       text NOT NULL,
                slots       int4multirange NOT NULL,
                active_days datemultirange NOT NULL
            );
            CREATE INDEX multirange_items_slots_gist_idx
                ON multirange_items USING gist (slots);
            CREATE INDEX multirange_items_active_days_gist_idx
                ON multirange_items USING gist (active_days);
            SELECT temporal.enable('multirange_items', include_indexes => true, combine_interval => interval '0');
            """);

        var mirroredDefinitions = await QueryAsync(conn, """
            SELECT pg_get_indexdef(history_index_oid)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'multirange_items'::regclass
              AND base_index_oid IN (
                  'multirange_items_slots_gist_idx'::regclass,
                  'multirange_items_active_days_gist_idx'::regclass
              )
            ORDER BY pg_get_indexdef(history_index_oid)
            """, r => r.GetString(0));
        Assert.Equal(2, mirroredDefinitions.Count);
        Assert.All(mirroredDefinitions, d => Assert.Contains("USING gist", d, StringComparison.OrdinalIgnoreCase));

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO multirange_items VALUES
                (1,
                 'old-window',
                 '{[1,4),[10,12)}'::int4multirange,
                 '{[2026-01-01,2026-01-03),[2026-01-10,2026-01-12)}'::datemultirange);
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE multirange_items
               SET label = 'current-window',
                   slots = '{[40,45)}'::int4multirange,
                   active_days = '{[2026-02-01,2026-02-03)}'::datemultirange
             WHERE id = 1;
            INSERT INTO multirange_items VALUES
                (2,
                 'current-only',
                 '{[90,95)}'::int4multirange,
                 '{[2026-03-01,2026-03-03)}'::datemultirange);
            """);

        var history = await QuerySingleAsync(conn, """
            SELECT label,
                   slots::text,
                   active_days::text,
                   slots @> 2,
                   active_days @> DATE '2026-01-02',
                   changed_by
            FROM multirange_items__history
            WHERE id = 1
            """, r => (
                Label: r.GetString(0),
                Slots: r.GetString(1),
                ActiveDays: r.GetString(2),
                ContainsSlot: r.GetBoolean(3),
                ContainsDay: r.GetBoolean(4),
                ChangedBy: r.GetString(5)));
        Assert.Equal(("old-window", "{[1,4),[10,12)}", "{[2026-01-01,2026-01-03),[2026-01-10,2026-01-12)}", true, true, "alice"), history);

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-window:{[1,4),[10,12)}:{[2026-01-01,2026-01-03),[2026-01-10,2026-01-12)}",
            await ScalarAsync<string>(conn, """
                SELECT id::text || ':' || label || ':' || slots::text || ':' || active_days::text
                FROM multirange_items
                WHERE slots && '{[10,11)}'::int4multirange
                  AND active_days @> DATE '2026-01-10'
                """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current-window:{[40,45)},2:current-only:{[90,95)}", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || label || ':' || slots::text, ',' ORDER BY id)
            FROM multirange_items
            WHERE active_days && '{[2026-02-01,2026-03-02)}'::datemultirange
            """));
    }

    [Fact]
    public async Task Dml_ToastedTextByteaAndJsonbValues_AreVersionedAndReadableAsOf()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE toasted_items (
                id      int PRIMARY KEY,
                note    text NOT NULL,
                payload jsonb NOT NULL,
                bytes   bytea NOT NULL
            );
            SELECT temporal.enable('toasted_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO toasted_items
            VALUES (
                1,
                repeat('old-', 1500),
                jsonb_build_object('state', 'old', 'blob', repeat('json-old-', 700)),
                decode(repeat('ab', 5000), 'hex')
            );
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE toasted_items
               SET note = repeat('cur-', 1500),
                   payload = jsonb_build_object('state', 'current', 'blob', repeat('json-current-', 600)),
                   bytes = decode(repeat('cd', 5000), 'hex')
             WHERE id = 1
            """);

        var historyCopy = await QuerySingleAsync(conn, """
            SELECT length(note),
                   left(note, 4),
                   payload ->> 'state',
                   length(payload ->> 'blob'),
                   length(bytes),
                   encode(substring(bytes from 1 for 2), 'hex'),
                   changed_by,
                   deleted_by IS NULL
            FROM toasted_items__history
            WHERE id = 1
            """, r => (
                NoteLength: r.GetInt32(0),
                NotePrefix: r.GetString(1),
                PayloadState: r.GetString(2),
                PayloadBlobLength: r.GetInt32(3),
                ByteLength: r.GetInt32(4),
                BytePrefix: r.GetString(5),
                ChangedBy: r.GetString(6),
                DeletedByIsNull: r.GetBoolean(7)));
        Assert.Equal((6000, "old-", "old", 6300, 5000, "abab", "alice", true), historyCopy);

        await SetAsOfAsync(conn, asOf!);
        var historic = await QuerySingleAsync(conn, """
            SELECT length(note),
                   left(note, 4),
                   payload ->> 'state',
                   length(payload ->> 'blob'),
                   length(bytes),
                   encode(substring(bytes from 1 for 2), 'hex')
            FROM toasted_items
            WHERE id = 1
            """, r => (
                NoteLength: r.GetInt32(0),
                NotePrefix: r.GetString(1),
                PayloadState: r.GetString(2),
                PayloadBlobLength: r.GetInt32(3),
                ByteLength: r.GetInt32(4),
                BytePrefix: r.GetString(5)));
        Assert.Equal((6000, "old-", "old", 6300, 5000, "abab"), historic);

        await ExecAsync(conn, "RESET temporal.as_of");
        var current = await QuerySingleAsync(conn, """
            SELECT length(note),
                   left(note, 4),
                   payload ->> 'state',
                   length(payload ->> 'blob'),
                   length(bytes),
                   encode(substring(bytes from 1 for 2), 'hex')
            FROM toasted_items
            WHERE id = 1
            """, r => (
                NoteLength: r.GetInt32(0),
                NotePrefix: r.GetString(1),
                PayloadState: r.GetString(2),
                PayloadBlobLength: r.GetInt32(3),
                ByteLength: r.GetInt32(4),
                BytePrefix: r.GetString(5)));
        Assert.Equal((6000, "cur-", "current", 7800, 5000, "cdcd"), current);
    }

    [Fact]
    public async Task Dml_ExtensionBackedCitextTypeAndUniqueIndex_WorkWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE EXTENSION citext;
            CREATE TABLE citext_items (
                id           int PRIMARY KEY,
                email        citext NOT NULL UNIQUE,
                display_name text NOT NULL
            );
            SELECT temporal.enable('citext_items', include_indexes => true, combine_interval => interval '0');
            """);

        Assert.Equal(1L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'citext_items'::regclass
              AND base_index_oid = 'citext_items_email_key'::regclass
            """));

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO citext_items VALUES (1, 'Alice@Example.com', 'old-alice')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        var upserted = await QuerySingleAsync(conn, """
            INSERT INTO citext_items (id, email, display_name)
            VALUES (2, 'alice@example.COM', 'current-alice')
            ON CONFLICT (email) DO UPDATE
                SET display_name = EXCLUDED.display_name
            RETURNING id, email::text, display_name, changed_by
            """, r => (
                Id: r.GetInt32(0),
                Email: r.GetString(1),
                DisplayName: r.GetString(2),
                ChangedBy: r.GetString(3)));
        Assert.Equal((1, "Alice@Example.com", "current-alice", "bob"), upserted);

        await ExecAsync(conn, "INSERT INTO citext_items VALUES (3, 'bob@example.com', 'current-bob')");

        var duplicate = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "INSERT INTO citext_items VALUES (4, 'ALICE@example.com', 'duplicate')"));
        Assert.Equal("23505", duplicate.SqlState);

        Assert.Equal("1:Alice@Example.com:old-alice:alice:", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || email::text || ':' || display_name || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY valid_to)
            FROM citext_items__history
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:Alice@Example.com:old-alice", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || email::text || ':' || display_name
            FROM citext_items
            WHERE email = 'ALICE@example.com'
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:Alice@Example.com:current-alice:bob,3:bob@example.com:current-bob:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || email::text || ':' || display_name || ':' || changed_by, ',' ORDER BY id)
            FROM citext_items
            WHERE email IN ('alice@example.com', 'BOB@example.com')
            """));
    }

    [Fact]
    public async Task Dml_ExtensionBackedHstoreTypeAndGinIndex_WorkWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE EXTENSION hstore;
            CREATE TABLE hstore_items (
                id    int PRIMARY KEY,
                attrs hstore NOT NULL,
                note  text NOT NULL
            );
            CREATE INDEX hstore_items_attrs_gin_idx
                ON hstore_items USING gin (attrs);
            SELECT temporal.enable('hstore_items', include_indexes => true, combine_interval => interval '0');
            """);

        var mirror = await QuerySingleAsync(conn, """
            SELECT pg_get_indexdef(history_index_oid)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'hstore_items'::regclass
              AND base_index_oid = 'hstore_items_attrs_gin_idx'::regclass
            """, r => r.GetString(0));
        Assert.Contains("USING gin", mirror, StringComparison.OrdinalIgnoreCase);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO hstore_items VALUES
                (1, 'state=>old, color=>blue, owner=>alice'::hstore, 'old-note');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE hstore_items
               SET attrs = 'state=>current, color=>green, owner=>bob'::hstore,
                   note = 'current-note'
             WHERE id = 1;
            INSERT INTO hstore_items VALUES
                (2, 'state=>current, color=>red, owner=>bob'::hstore, 'current-only');
            """);

        var history = await QuerySingleAsync(conn, """
            SELECT attrs -> 'state',
                   attrs ? 'color',
                   attrs @> 'owner=>alice'::hstore,
                   note,
                   changed_by
            FROM hstore_items__history
            WHERE id = 1
            """, r => (
                State: r.GetString(0),
                HasColor: r.GetBoolean(1),
                HasOldOwner: r.GetBoolean(2),
                Note: r.GetString(3),
                ChangedBy: r.GetString(4)));
        Assert.Equal(("old", true, true, "old-note", "alice"), history);

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old:blue:old-note", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || (attrs -> 'state') || ':' || (attrs -> 'color') || ':' || note, ',' ORDER BY id)
            FROM hstore_items
            WHERE attrs ? 'owner'
              AND attrs @> 'state=>old'::hstore
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current:green:current-note,2:current:red:current-only", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || (attrs -> 'state') || ':' || (attrs -> 'color') || ':' || note, ',' ORDER BY id)
            FROM hstore_items
            WHERE attrs @> 'owner=>bob'::hstore
            """));
    }

    [Fact]
    public async Task Dml_ExtensionBackedLtreeTypeAndGistIndex_WorkWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE EXTENSION ltree;
            CREATE TABLE ltree_items (
                id    int PRIMARY KEY,
                path  ltree NOT NULL,
                label text NOT NULL
            );
            CREATE INDEX ltree_items_path_gist_idx
                ON ltree_items USING gist (path);
            SELECT temporal.enable('ltree_items', include_indexes => true, combine_interval => interval '0');
            """);

        var mirrorDefinition = await QuerySingleAsync(conn, """
            SELECT pg_get_indexdef(history_index_oid)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'ltree_items'::regclass
              AND base_index_oid = 'ltree_items_path_gist_idx'::regclass
            """, r => r.GetString(0));
        Assert.Contains("USING gist", mirrorDefinition, StringComparison.OrdinalIgnoreCase);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO ltree_items VALUES
                (1, 'Top.Old.Alpha'::ltree, 'old-alpha'),
                (2, 'Top.Old.Beta'::ltree, 'old-beta');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE ltree_items
               SET path = 'Top.Current.Alpha'::ltree,
                   label = 'current-alpha'
             WHERE id = 1;
            DELETE FROM ltree_items WHERE id = 2;
            INSERT INTO ltree_items VALUES (3, 'Top.Current.Gamma'::ltree, 'current-gamma');
            """);

        Assert.Equal("1:Top.Old.Alpha:old-alpha:alice:,2:Top.Old.Beta:old-beta:alice:bob",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || path::text || ':' || label || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
                FROM ltree_items__history
                """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:Top.Old.Alpha:old-alpha,2:Top.Old.Beta:old-beta", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || path::text || ':' || label, ',' ORDER BY id)
            FROM ltree_items
            WHERE path <@ 'Top.Old'::ltree
              AND path ~ 'Top.Old.*'::lquery
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:Top.Current.Alpha:current-alpha,3:Top.Current.Gamma:current-gamma", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || path::text || ':' || label, ',' ORDER BY id)
            FROM ltree_items
            WHERE path <@ 'Top.Current'::ltree
            """));
    }

    [Fact]
    public async Task Dml_ExtensionBackedPgTrgmOperatorClass_WorkWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE EXTENSION pg_trgm;
            CREATE TABLE trigram_items (
                id    int PRIMARY KEY,
                title text NOT NULL,
                body  text NOT NULL
            );
            CREATE INDEX trigram_items_title_trgm_idx
                ON trigram_items USING gin (title gin_trgm_ops);
            SELECT temporal.enable('trigram_items', include_indexes => true, combine_interval => interval '0');
            """);

        var mirrorDefinition = await QuerySingleAsync(conn, """
            SELECT pg_get_indexdef(history_index_oid)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'trigram_items'::regclass
              AND base_index_oid = 'trigram_items_title_trgm_idx'::regclass
            """, r => r.GetString(0));
        Assert.Contains("USING gin", mirrorDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gin_trgm_ops", mirrorDefinition, StringComparison.OrdinalIgnoreCase);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO trigram_items VALUES
                (1, 'temporal archive alpha', 'old searchable content'),
                (2, 'temporal archive beta', 'old beta content');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE trigram_items
               SET title = 'current ledger alpha',
                   body = 'current searchable content'
             WHERE id = 1;
            DELETE FROM trigram_items WHERE id = 2;
            INSERT INTO trigram_items VALUES (3, 'current ledger gamma', 'current gamma content');
            """);

        Assert.Equal("1:temporal archive alpha:alice,2:temporal archive beta:alice", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || title || ':' || changed_by, ',' ORDER BY id)
            FROM trigram_items__history
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:temporal archive alpha,2:temporal archive beta", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || title, ',' ORDER BY similarity(title, 'temporal archive') DESC, id)
            FROM trigram_items
            WHERE title % 'temporal archive'
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current ledger alpha,3:current ledger gamma", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || title, ',' ORDER BY similarity(title, 'current ledger') DESC, id)
            FROM trigram_items
            WHERE title % 'current ledger'
            """));
    }

    [Fact]
    public async Task Dml_ExtensionBackedPgvectorTypeAndHnswIndex_WorkWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE EXTENSION vector;
            CREATE TABLE vector_items (
                id        int PRIMARY KEY,
                embedding vector(3) NOT NULL,
                label     text NOT NULL
            );
            CREATE INDEX vector_items_embedding_hnsw_idx
                ON vector_items USING hnsw (embedding vector_l2_ops);
            SELECT temporal.enable('vector_items', include_indexes => true, combine_interval => interval '0');
            """);

        var mirrorDefinition = await QuerySingleAsync(conn, """
            SELECT pg_get_indexdef(history_index_oid)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'vector_items'::regclass
              AND base_index_oid = 'vector_items_embedding_hnsw_idx'::regclass
            """, r => r.GetString(0));
        Assert.Contains("USING hnsw", mirrorDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vector_l2_ops", mirrorDefinition, StringComparison.OrdinalIgnoreCase);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO vector_items VALUES
                (1, '[1,0,0]'::vector, 'old-alpha'),
                (2, '[0,1,0]'::vector, 'old-beta');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE vector_items
               SET embedding = '[0.8,0.2,0]'::vector,
                   label = 'current-alpha'
             WHERE id = 1;
            DELETE FROM vector_items WHERE id = 2;
            INSERT INTO vector_items VALUES (3, '[0,0,1]'::vector, 'current-gamma');
            """);

        Assert.Equal("1:[1,0,0]:old-alpha:alice:,2:[0,1,0]:old-beta:alice:bob",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || embedding::text || ':' || label || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
                FROM vector_items__history
                """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-alpha,2:old-beta", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || label, ',' ORDER BY distance, id)
            FROM (
                SELECT id, label, embedding <-> '[1,0,0]'::vector AS distance
                FROM vector_items
                ORDER BY embedding <-> '[1,0,0]'::vector, id
                LIMIT 2
            ) s
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current-alpha,3:current-gamma", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || label, ',' ORDER BY distance, id)
            FROM (
                SELECT id, label, embedding <-> '[1,0,0]'::vector AS distance
                FROM vector_items
                ORDER BY embedding <-> '[1,0,0]'::vector, id
                LIMIT 2
            ) s
            """));
    }

    [Fact]
    public async Task AsOf_XmlUuidAndNetworkTypesUseHistoricalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE document_items (
                id     uuid PRIMARY KEY,
                ip     inet NOT NULL,
                subnet cidr NOT NULL,
                doc    xml NOT NULL
            );
            SELECT temporal.enable('document_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO document_items VALUES
                ('11111111-1111-1111-1111-111111111111',
                 inet '192.168.1.10',
                 cidr '192.168.1.0/24',
                 xmlparse(document '<item state="old"><name>alpha</name><tag>x</tag></item>')),
                ('22222222-2222-2222-2222-222222222222',
                 inet '10.0.0.5',
                 cidr '10.0.0.0/24',
                 xmlparse(document '<item state="old"><name>beta</name><tag>y</tag></item>'));
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE document_items
               SET ip = inet '172.16.0.25',
                   subnet = cidr '172.16.0.0/16',
                   doc = xmlparse(document '<item state="current"><name>alpha-current</name><tag>z</tag></item>')
             WHERE id = '11111111-1111-1111-1111-111111111111';
            DELETE FROM document_items
             WHERE id = '22222222-2222-2222-2222-222222222222';
            """);

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("11111111-1111-1111-1111-111111111111:192.168.1.10:192.168.1.0/24:old:alpha,22222222-2222-2222-2222-222222222222:10.0.0.5:10.0.0.0/24:old:beta",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(
                           id::text || ':' ||
                           host(ip) || ':' ||
                           subnet::text || ':' ||
                           ((xpath('string(/item/@state)', doc))[1])::text || ':' ||
                           ((xpath('string(/item/name/text())', doc))[1])::text,
                           ',' ORDER BY id)
                FROM document_items
                WHERE ip << inet '192.168.0.0/16'
                   OR ip << inet '10.0.0.0/8'
                """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("11111111-1111-1111-1111-111111111111:172.16.0.25:172.16.0.0/16:current:alpha-current",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(
                           id::text || ':' ||
                           host(ip) || ':' ||
                           subnet::text || ':' ||
                           ((xpath('string(/item/@state)', doc))[1])::text || ':' ||
                           ((xpath('string(/item/name/text())', doc))[1])::text,
                           ',' ORDER BY id)
                FROM document_items
                WHERE subnet >>= inet '172.16.0.25'
                """));

        Assert.Equal("22222222-2222-2222-2222-222222222222:bob", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || deleted_by
            FROM document_items__history
            WHERE id = '22222222-2222-2222-2222-222222222222'
            """));
    }

    [Fact]
    public async Task Dml_GeometricBitAndMacaddrTypes_WorkWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE geometric_items (
                id       int PRIMARY KEY,
                label    text NOT NULL,
                location point NOT NULL,
                bounds   box NOT NULL,
                device   macaddr8 NOT NULL,
                flags    bit(4) NOT NULL,
                mask     bit varying(8) NOT NULL
            );
            CREATE INDEX geometric_items_location_gist_idx
                ON geometric_items USING gist (location);
            SELECT temporal.enable('geometric_items', include_indexes => true, combine_interval => interval '0');
            """);

        var mirrorDefinition = await QuerySingleAsync(conn, """
            SELECT pg_get_indexdef(history_index_oid)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'geometric_items'::regclass
              AND base_index_oid = 'geometric_items_location_gist_idx'::regclass
            """, r => r.GetString(0));
        Assert.Contains("USING gist", mirrorDefinition, StringComparison.OrdinalIgnoreCase);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO geometric_items VALUES
                (1,
                 'old-shape',
                 point '(1,2)',
                 box '((0,0),(3,3))',
                 macaddr8 '08:00:2b:01:02:03:04:05',
                 B'1010',
                 B'10101100');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE geometric_items
               SET label = 'current-shape',
                   location = point '(10,20)',
                   bounds = box '((9,19),(11,21))',
                   device = macaddr8 '08:00:2b:ff:fe:06:07:08',
                   flags = B'0101',
                   mask = B'01010101'
             WHERE id = 1;
            INSERT INTO geometric_items VALUES
                (2,
                 'current-only',
                 point '(30,40)',
                 box '((29,39),(31,41))',
                 macaddr8 '08:00:2b:ff:fe:09:0a:0b',
                 B'1111',
                 B'11110000');
            """);

        var history = await QuerySingleAsync(conn, """
            SELECT label,
                   location[0]::int,
                   location[1]::int,
                   area(bounds)::int,
                   device::text,
                   flags::text,
                   mask::text,
                   changed_by
            FROM geometric_items__history
            WHERE id = 1
            """, r => (
                Label: r.GetString(0),
                X: r.GetInt32(1),
                Y: r.GetInt32(2),
                Area: r.GetInt32(3),
                Device: r.GetString(4),
                Flags: r.GetString(5),
                Mask: r.GetString(6),
                ChangedBy: r.GetString(7)));
        Assert.Equal(("old-shape", 1, 2, 9, "08:00:2b:01:02:03:04:05", "1010", "10101100", "alice"), history);

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-shape:1,2:9:1010:10101100", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' ||
                   label || ':' ||
                   location[0]::int::text || ',' ||
                   location[1]::int::text || ':' ||
                   area(bounds)::int::text || ':' ||
                   flags::text || ':' ||
                   mask::text
            FROM geometric_items
            WHERE location[0] = 1
              AND location[1] = 2
              AND (flags & B'1000') = B'1000'
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current-shape:10,20:4:0101:01010101,2:current-only:30,40:4:1111:11110000",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' ||
                                  label || ':' ||
                                  location[0]::int::text || ',' ||
                                  location[1]::int::text || ':' ||
                                  area(bounds)::int::text || ':' ||
                                  flags::text || ':' ||
                                  mask::text,
                                  ',' ORDER BY id)
                FROM geometric_items
                WHERE device >= macaddr8 '08:00:2b:ff:fe:00:00:00'
                """));
    }

    [Fact]
    public async Task Dml_FullTextSearchAndGinIndexes_WorkWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE search_docs (
                id            int PRIMARY KEY,
                title         text NOT NULL,
                body          text NOT NULL,
                search_vector tsvector GENERATED ALWAYS AS (
                    to_tsvector('english', title || ' ' || body)
                ) STORED
            );
            CREATE INDEX search_docs_vector_idx
                ON search_docs USING gin (search_vector);
            SELECT temporal.enable('search_docs', include_indexes => true, combine_interval => interval '0');
            CREATE INDEX search_docs_expr_idx
                ON search_docs USING gin (
                    (to_tsvector('english', title || ' ' || body))
                );
            """);

        var mirroredDefinitions = await QueryAsync(conn, """
            SELECT pg_get_indexdef(history_index_oid)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'search_docs'::regclass
              AND base_index_oid IN ('search_docs_vector_idx'::regclass, 'search_docs_expr_idx'::regclass)
            ORDER BY pg_get_indexdef(history_index_oid)
            """, r => r.GetString(0));

        Assert.Equal(2, mirroredDefinitions.Count);
        Assert.All(mirroredDefinitions, d => Assert.Contains("USING gin", d, StringComparison.OrdinalIgnoreCase));

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO search_docs (id, title, body) VALUES
                (1, 'Audit trail', 'Temporal history keeps every audit entry'),
                (2, 'Compliance report', 'Audit evidence remains searchable');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE search_docs
               SET title = 'Metrics dashboard',
                   body = 'Current operational metrics'
             WHERE id = 1;
            DELETE FROM search_docs WHERE id = 2;
            INSERT INTO search_docs (id, title, body)
            VALUES (3, 'Current guide', 'Operational runbook');
            """);

        await SetAsOfAsync(conn, asOf!);

        var historicMatches = await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || title, ',' ORDER BY id)
            FROM search_docs
            WHERE search_vector @@ plainto_tsquery('english', 'audit')
            """);
        Assert.Equal("1:Audit trail,2:Compliance report", historicMatches);

        var historicHeadline = await ScalarAsync<string>(conn, """
            SELECT ts_headline('english', body, plainto_tsquery('english', 'history'))
            FROM search_docs
            WHERE id = 1
            """);
        Assert.Equal("Temporal <b>history</b> keeps every audit entry", historicHeadline);

        await ExecAsync(conn, "RESET temporal.as_of");

        Assert.Equal(0L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM search_docs
            WHERE search_vector @@ plainto_tsquery('english', 'audit')
            """));
        Assert.Equal("1:Metrics dashboard,3:Current guide", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || title, ',' ORDER BY id)
            FROM search_docs
            """));
    }

    [Fact]
    public async Task AsOf_RowLevelSecurityFiltersHistoricalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'temporal_feature_reader') THEN
                    CREATE ROLE temporal_feature_reader;
                END IF;
            END
            $$;

            CREATE TABLE secure_docs (
                id        int PRIMARY KEY,
                tenant_id int NOT NULL,
                body      text NOT NULL
            );
            GRANT USAGE ON SCHEMA temporal TO temporal_feature_reader;
            GRANT SELECT ON secure_docs TO temporal_feature_reader;
            SELECT temporal.enable('secure_docs', combine_interval => interval '0');
            ALTER TABLE secure_docs ENABLE ROW LEVEL SECURITY;
            CREATE POLICY secure_docs_tenant_read
                ON secure_docs
                FOR SELECT
                TO temporal_feature_reader
                USING (tenant_id = current_setting('app.tenant_id')::int);
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO secure_docs VALUES
                (1, 10, 'tenant-10-old'),
                (2, 20, 'tenant-20-old');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE secure_docs SET body = body || '-current';
            INSERT INTO secure_docs VALUES (3, 10, 'tenant-10-new-current');
            """);

        await ExecAsync(conn, "SET ROLE temporal_feature_reader");
        await ExecAsync(conn, "SET app.tenant_id = '10'");
        await SetAsOfAsync(conn, asOf!);

        var tenant10Historic = await ScalarAsync<string>(conn,
            "SELECT string_agg(body, ',' ORDER BY id) FROM secure_docs");
        Assert.Equal("tenant-10-old", tenant10Historic);

        await ExecAsync(conn, "SET app.tenant_id = '20'");
        var tenant20Historic = await ScalarAsync<string>(conn,
            "SELECT string_agg(body, ',' ORDER BY id) FROM secure_docs");
        Assert.Equal("tenant-20-old", tenant20Historic);

        await ExecAsync(conn, "RESET temporal.as_of");
        await ExecAsync(conn, "SET app.tenant_id = '10'");
        var tenant10Present = await ScalarAsync<string>(conn,
            "SELECT string_agg(body, ',' ORDER BY id) FROM secure_docs");
        Assert.Equal("tenant-10-old-current,tenant-10-new-current", tenant10Present);

        await ExecAsync(conn, "RESET ROLE");
    }

    [Fact]
    public async Task AsOf_RestrictiveRowLevelSecurityPoliciesFilterHistoricalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'temporal_feature_restrictive_reader') THEN
                    CREATE ROLE temporal_feature_restrictive_reader;
                END IF;
            END
            $$;

            CREATE TABLE restrictive_docs (
                id        int PRIMARY KEY,
                tenant_id int NOT NULL,
                archived  boolean NOT NULL,
                body      text NOT NULL
            );
            GRANT USAGE ON SCHEMA temporal TO temporal_feature_restrictive_reader;
            GRANT SELECT ON restrictive_docs TO temporal_feature_restrictive_reader;
            SELECT temporal.enable('restrictive_docs', combine_interval => interval '0');
            ALTER TABLE restrictive_docs ENABLE ROW LEVEL SECURITY;
            ALTER TABLE restrictive_docs FORCE ROW LEVEL SECURITY;
            CREATE POLICY restrictive_docs_tenant_read
                ON restrictive_docs
                AS PERMISSIVE
                FOR SELECT
                TO temporal_feature_restrictive_reader
                USING (tenant_id = current_setting('app.tenant_id')::int);
            CREATE POLICY restrictive_docs_not_archived
                ON restrictive_docs
                AS RESTRICTIVE
                FOR SELECT
                TO temporal_feature_restrictive_reader
                USING (NOT archived);
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO restrictive_docs VALUES
                (1, 10, false, 'tenant-10-old-visible'),
                (2, 10, true,  'tenant-10-old-archived'),
                (3, 20, false, 'tenant-20-old-visible');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE restrictive_docs
               SET body = 'tenant-10-current-visible'
             WHERE id = 1;
            UPDATE restrictive_docs
               SET archived = false,
                   body = 'tenant-10-current-unarchived'
             WHERE id = 2;
            DELETE FROM restrictive_docs WHERE id = 3;
            INSERT INTO restrictive_docs VALUES (4, 10, false, 'tenant-10-current-only');
            """);

        await ExecAsync(conn, "SET ROLE temporal_feature_restrictive_reader");
        await ExecAsync(conn, "SET app.tenant_id = '10'");
        await SetAsOfAsync(conn, asOf!);

        Assert.Equal("1:tenant-10-old-visible", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || body, ',' ORDER BY id)
            FROM restrictive_docs
            """));

        await ExecAsync(conn, "SET app.tenant_id = '20'");
        Assert.Equal("3:tenant-20-old-visible", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || body, ',' ORDER BY id)
            FROM restrictive_docs
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        await ExecAsync(conn, "SET app.tenant_id = '10'");
        Assert.Equal("1:tenant-10-current-visible,2:tenant-10-current-unarchived,4:tenant-10-current-only",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || body, ',' ORDER BY id)
                FROM restrictive_docs
                """));

        await ExecAsync(conn, "SET app.tenant_id = '20'");
        Assert.Null(await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || body, ',' ORDER BY id)
            FROM restrictive_docs
            """));

        await ExecAsync(conn, "RESET ROLE");
    }

    [Fact]
    public async Task Dml_RowLevelSecurityWritePoliciesAreVersionedAndRejectedWritesLeaveNoHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'temporal_feature_rls_writer') THEN
                    CREATE ROLE temporal_feature_rls_writer;
                END IF;
            END
            $$;

            CREATE TABLE rls_write_docs (
                id        int PRIMARY KEY,
                tenant_id int NOT NULL,
                archived  boolean NOT NULL DEFAULT false,
                body      text NOT NULL
            );
            GRANT USAGE ON SCHEMA public TO temporal_feature_rls_writer;
            GRANT USAGE ON SCHEMA temporal TO temporal_feature_rls_writer;
            GRANT SELECT, INSERT, UPDATE, DELETE ON rls_write_docs TO temporal_feature_rls_writer;
            SELECT temporal.enable('rls_write_docs', combine_interval => interval '0');
            ALTER TABLE rls_write_docs ENABLE ROW LEVEL SECURITY;

            CREATE POLICY rls_write_docs_select
                ON rls_write_docs
                FOR SELECT
                TO temporal_feature_rls_writer
                USING (tenant_id = current_setting('app.tenant_id')::int);

            CREATE POLICY rls_write_docs_insert
                ON rls_write_docs
                FOR INSERT
                TO temporal_feature_rls_writer
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::int);

            CREATE POLICY rls_write_docs_update
                ON rls_write_docs
                FOR UPDATE
                TO temporal_feature_rls_writer
                USING (tenant_id = current_setting('app.tenant_id')::int)
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::int AND NOT archived);

            CREATE POLICY rls_write_docs_delete
                ON rls_write_docs
                FOR DELETE
                TO temporal_feature_rls_writer
                USING (tenant_id = current_setting('app.tenant_id')::int);
            """);

        Assert.False(await ScalarAsync<bool>(conn,
            "SELECT has_table_privilege('temporal_feature_rls_writer', 'rls_write_docs__history', 'INSERT')"));

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO rls_write_docs VALUES
                (1, 10, false, 'tenant-10-old'),
                (2, 20, false, 'tenant-20-old');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET ROLE temporal_feature_rls_writer");
        try
        {
            await ExecAsync(conn, "SET app.tenant_id = '10'");
            await ExecAsync(conn, "SET temporal.user_id = 'rls-writer'");

            var rejectedInsert = await Assert.ThrowsAsync<PostgresException>(async () =>
                await ExecAsync(conn, "INSERT INTO rls_write_docs VALUES (4, 20, false, 'wrong-tenant')"));
            Assert.Equal("42501", rejectedInsert.SqlState);

            var rejectedTenantChange = await Assert.ThrowsAsync<PostgresException>(async () =>
                await ExecAsync(conn, "UPDATE rls_write_docs SET tenant_id = 20 WHERE id = 1"));
            Assert.Equal("42501", rejectedTenantChange.SqlState);

            var rejectedArchive = await Assert.ThrowsAsync<PostgresException>(async () =>
                await ExecAsync(conn, "UPDATE rls_write_docs SET archived = true WHERE id = 1"));
            Assert.Equal("42501", rejectedArchive.SqlState);
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }

        Assert.Equal(0L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM rls_write_docs__history"));
        Assert.Equal("1:10:false:tenant-10-old,2:20:false:tenant-20-old", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || tenant_id::text || ':' || archived::text || ':' || body, ',' ORDER BY id)
            FROM rls_write_docs
            """));
        Assert.Equal(0L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM rls_write_docs WHERE id = 4"));

        await ExecAsync(conn, "SET ROLE temporal_feature_rls_writer");
        try
        {
            await ExecAsync(conn, "SET app.tenant_id = '10'");
            await ExecAsync(conn, "SET temporal.user_id = 'rls-writer'");
            await ExecAsync(conn, "UPDATE rls_write_docs SET body = 'tenant-10-current' WHERE id = 1");
            await ExecAsync(conn, "INSERT INTO rls_write_docs VALUES (3, 10, false, 'tenant-10-current-only')");
            await ExecAsync(conn, "DELETE FROM rls_write_docs WHERE id = 3");

            Assert.Equal("1:tenant-10-current", await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || body, ',' ORDER BY id)
                FROM rls_write_docs
                """));

            await SetAsOfAsync(conn, asOf!);
            Assert.Equal("1:tenant-10-old", await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || body, ',' ORDER BY id)
                FROM rls_write_docs
                """));
            await ExecAsync(conn, "RESET temporal.as_of");
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }

        Assert.Equal("1:tenant-10-old:alice:,3:tenant-10-current-only:rls-writer:rls-writer",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || body || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
                FROM rls_write_docs__history
                """));
        Assert.Equal("1:10:false:tenant-10-current:rls-writer,2:20:false:tenant-20-old:alice",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || tenant_id::text || ':' || archived::text || ':' || body || ':' || changed_by, ',' ORDER BY id)
                FROM rls_write_docs
                """));
    }

    [Fact]
    public async Task AsOf_SelectGrantsCoverHistoryAndRebuiltVersionsView()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'temporal_feature_grantee') THEN
                    CREATE ROLE temporal_feature_grantee;
                END IF;
            END
            $$;

            CREATE TABLE grant_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            GRANT USAGE ON SCHEMA public TO temporal_feature_grantee;
            GRANT USAGE ON SCHEMA temporal TO temporal_feature_grantee;
            GRANT SELECT ON grant_items TO temporal_feature_grantee;
            SELECT temporal.enable('grant_items', combine_interval => interval '0');
            """);

        Assert.True(await ScalarAsync<bool>(conn,
            "SELECT has_table_privilege('temporal_feature_grantee', 'grant_items__history', 'SELECT')"));
        Assert.True(await ScalarAsync<bool>(conn,
            "SELECT has_table_privilege('temporal_feature_grantee', 'grant_items__versions', 'SELECT')"));

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO grant_items VALUES (1, 'old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "ALTER TABLE grant_items ADD COLUMN label text NOT NULL DEFAULT 'general'");
        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE grant_items SET name = 'current', label = 'visible' WHERE id = 1");

        Assert.True(await ScalarAsync<bool>(conn,
            "SELECT has_table_privilege('temporal_feature_grantee', 'grant_items__versions', 'SELECT')"));

        await ExecAsync(conn, "SET ROLE temporal_feature_grantee");
        try
        {
            await SetAsOfAsync(conn, asOf!);
            Assert.Equal("old:general", await ScalarAsync<string>(conn, """
                SELECT name || ':' || label
                FROM grant_items
                WHERE id = 1
                """));

            await ExecAsync(conn, "RESET temporal.as_of");
            Assert.Equal("old:general", await ScalarAsync<string>(conn, """
                SELECT name || ':' || label
                FROM grant_items__history
                WHERE id = 1
                """));
            Assert.Equal("old:general,current:visible", await ScalarAsync<string>(conn, """
                SELECT string_agg(name || ':' || label, ',' ORDER BY valid_from)
                FROM grant_items__versions
                WHERE id = 1
                """));
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }
    }

    [Fact]
    public async Task AsOf_ColumnLevelSelectGrantsApplyToHistoricalRewrites()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'temporal_feature_column_reader') THEN
                    CREATE ROLE temporal_feature_column_reader;
                END IF;
            END
            $$;

            CREATE TABLE column_grant_docs (
                id     int PRIMARY KEY,
                title  text NOT NULL,
                secret text NOT NULL
            );
            GRANT USAGE ON SCHEMA public TO temporal_feature_column_reader;
            GRANT USAGE ON SCHEMA temporal TO temporal_feature_column_reader;
            GRANT SELECT (id, title) ON column_grant_docs TO temporal_feature_column_reader;
            SELECT temporal.enable('column_grant_docs', combine_interval => interval '0');
            """);

        Assert.True(await ScalarAsync<bool>(conn,
            "SELECT has_column_privilege('temporal_feature_column_reader', 'column_grant_docs', 'title', 'SELECT')"));
        Assert.False(await ScalarAsync<bool>(conn,
            "SELECT has_column_privilege('temporal_feature_column_reader', 'column_grant_docs', 'secret', 'SELECT')"));

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO column_grant_docs VALUES (1, 'old-title', 'old-secret')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE column_grant_docs SET title = 'current-title', secret = 'current-secret' WHERE id = 1");

        await ExecAsync(conn, "SET ROLE temporal_feature_column_reader");
        try
        {
            Assert.Equal("1:current-title", await ScalarAsync<string>(conn, """
                SELECT id::text || ':' || title
                FROM column_grant_docs
                WHERE id = 1
                """));

            await SetAsOfAsync(conn, asOf!);
            Assert.Equal("1:old-title", await ScalarAsync<string>(conn, """
                SELECT id::text || ':' || title
                FROM column_grant_docs
                WHERE id = 1
                """));

            var secretDenied = await Assert.ThrowsAsync<PostgresException>(async () =>
                await ScalarAsync<string>(conn, """
                    SELECT secret
                    FROM column_grant_docs
                    WHERE id = 1
                    """));
            Assert.Equal("42501", secretDenied.SqlState);

            await ExecAsync(conn, "RESET temporal.as_of");
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }
    }

    [Fact]
    public async Task AsOf_SecurityDefinerFunctionsUseHistoricalRowsWithoutCallerSelect()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'temporal_feature_app') THEN
                    CREATE ROLE temporal_feature_app;
                END IF;
            END
            $$;

            CREATE TABLE definer_docs (
                id    int PRIMARY KEY,
                title text NOT NULL
            );
            SELECT temporal.enable('definer_docs', combine_interval => interval '0');

            CREATE FUNCTION definer_doc_titles()
            RETURNS text
            LANGUAGE sql
            STABLE
            SECURITY DEFINER
            SET search_path = public, pg_temp
            AS $$
                SELECT string_agg(title, ',' ORDER BY id)
                FROM definer_docs
            $$;

            REVOKE ALL ON FUNCTION definer_doc_titles() FROM PUBLIC;
            GRANT USAGE ON SCHEMA public TO temporal_feature_app;
            GRANT USAGE ON SCHEMA temporal TO temporal_feature_app;
            GRANT EXECUTE ON FUNCTION definer_doc_titles() TO temporal_feature_app;
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO definer_docs VALUES (1, 'old-one'), (2, 'old-two')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE definer_docs SET title = 'current-one' WHERE id = 1;
            DELETE FROM definer_docs WHERE id = 2;
            INSERT INTO definer_docs VALUES (3, 'current-three');
            """);

        await SetAsOfAsync(conn, asOf!);
        await ExecAsync(conn, "SET ROLE temporal_feature_app");
        try
        {
            var directSelect = await Assert.ThrowsAsync<PostgresException>(async () =>
                await ScalarAsync<string>(conn, "SELECT string_agg(title, ',' ORDER BY id) FROM definer_docs"));
            Assert.Equal("42501", directSelect.SqlState);

            Assert.Equal("old-one,old-two", await ScalarAsync<string>(conn,
                "SELECT definer_doc_titles()"));

            await ExecAsync(conn, "RESET temporal.as_of");
            Assert.Equal("current-one,current-three", await ScalarAsync<string>(conn,
                "SELECT definer_doc_titles()"));
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }
    }

    [Fact]
    public async Task Dml_ConstraintsDefaultsIdentityGeneratedColumnsAndUserTriggersCoexist()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE categories (
                id   int PRIMARY KEY,
                name text NOT NULL UNIQUE
            );
            CREATE TABLE constrained_orders (
                id          int GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                category_id int NOT NULL REFERENCES categories(id),
                quantity    int NOT NULL DEFAULT 1 CHECK (quantity > 0),
                unit_price  numeric NOT NULL CHECK (unit_price >= 0),
                total       numeric GENERATED ALWAYS AS (quantity * unit_price) STORED,
                note        text NOT NULL DEFAULT 'new',
                touched     int NOT NULL DEFAULT 0
            );
            CREATE TABLE default_only_items (
                id      int GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                name    text NOT NULL DEFAULT 'default-name',
                counter int NOT NULL DEFAULT 5
            );
            CREATE FUNCTION bump_order_touched()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                NEW.touched := coalesce(NEW.touched, 0) + 1;
                RETURN NEW;
            END
            $$;
            CREATE TRIGGER z_bump_order_touched
                BEFORE INSERT OR UPDATE ON constrained_orders
                FOR EACH ROW EXECUTE FUNCTION bump_order_touched();
            SELECT temporal.enable('constrained_orders', combine_interval => interval '0');
            SELECT temporal.enable('default_only_items', combine_interval => interval '0');
            INSERT INTO categories VALUES (1, 'hardware');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        var defaultInserted = await QuerySingleAsync(conn, """
            INSERT INTO default_only_items DEFAULT VALUES
            RETURNING id, name, counter, changed_by
            """, r => (
                Id: r.GetInt32(0),
                Name: r.GetString(1),
                Counter: r.GetInt32(2),
                ChangedBy: r.GetString(3)));

        Assert.Equal((1, "default-name", 5, "alice"), defaultInserted);

        var inserted = await QuerySingleAsync(conn, """
            INSERT INTO constrained_orders (category_id, quantity, unit_price)
            VALUES (1, 2, 3.50)
            RETURNING id, quantity, unit_price, total, note, touched, changed_by
            """, r => (
                Id: r.GetInt32(0),
                Quantity: r.GetInt32(1),
                UnitPrice: r.GetDecimal(2),
                Total: r.GetDecimal(3),
                Note: r.GetString(4),
                Touched: r.GetInt32(5),
                ChangedBy: r.GetString(6)));

        Assert.Equal(1, inserted.Id);
        Assert.Equal(2, inserted.Quantity);
        Assert.Equal(3.50m, inserted.UnitPrice);
        Assert.Equal(7.00m, inserted.Total);
        Assert.Equal("new", inserted.Note);
        Assert.Equal(1, inserted.Touched);
        Assert.Equal("alice", inserted.ChangedBy);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE default_only_items SET counter = 6 WHERE id = 1");

        var updated = await QuerySingleAsync(conn, """
            UPDATE constrained_orders
               SET quantity = 4,
                   note = 'updated'
             WHERE id = 1
            RETURNING quantity, unit_price, total, note, touched, changed_by
            """, r => (
                Quantity: r.GetInt32(0),
                UnitPrice: r.GetDecimal(1),
                Total: r.GetDecimal(2),
                Note: r.GetString(3),
                Touched: r.GetInt32(4),
                ChangedBy: r.GetString(5)));

        Assert.Equal(4, updated.Quantity);
        Assert.Equal(3.50m, updated.UnitPrice);
        Assert.Equal(14.00m, updated.Total);
        Assert.Equal("updated", updated.Note);
        Assert.Equal(2, updated.Touched);
        Assert.Equal("bob", updated.ChangedBy);

        var history = await QuerySingleAsync(conn, """
            SELECT quantity, unit_price, total, note, touched, changed_by
            FROM constrained_orders__history
            WHERE id = 1
            """, r => (
                Quantity: r.GetInt32(0),
                UnitPrice: r.GetDecimal(1),
                Total: r.GetDecimal(2),
                Note: r.GetString(3),
                Touched: r.GetInt32(4),
                ChangedBy: r.GetString(5)));

        Assert.Equal(2, history.Quantity);
        Assert.Equal(3.50m, history.UnitPrice);
        Assert.Equal(7.00m, history.Total);
        Assert.Equal("new", history.Note);
        Assert.Equal(1, history.Touched);
        Assert.Equal("alice", history.ChangedBy);

        await SetAsOfAsync(conn, asOf!);
        var historicAsOf = await QuerySingleAsync(conn, """
            SELECT quantity, unit_price, total, note, touched, changed_by
            FROM constrained_orders
            WHERE id = 1
            """, r => (
                Quantity: r.GetInt32(0),
                UnitPrice: r.GetDecimal(1),
                Total: r.GetDecimal(2),
                Note: r.GetString(3),
                Touched: r.GetInt32(4),
                ChangedBy: r.GetString(5)));

        Assert.Equal(history, historicAsOf);
        var defaultHistoricAsOf = await QuerySingleAsync(conn, """
            SELECT name, counter, changed_by
            FROM default_only_items
            WHERE id = 1
            """, r => (
                Name: r.GetString(0),
                Counter: r.GetInt32(1),
                ChangedBy: r.GetString(2)));
        Assert.Equal(("default-name", 5, "alice"), defaultHistoricAsOf);

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("default-name:6:bob", await ScalarAsync<string>(conn, """
            SELECT name || ':' || counter::text || ':' || changed_by
            FROM default_only_items
            WHERE id = 1
            """));

        var fkViolation = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, """
                INSERT INTO constrained_orders (category_id, quantity, unit_price)
                VALUES (999, 1, 1)
                """));
        Assert.Equal("23503", fkViolation.SqlState);

        var checkViolation = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, """
                INSERT INTO constrained_orders (category_id, quantity, unit_price)
                VALUES (1, 0, 1)
                """));
        Assert.Equal("23514", checkViolation.SqlState);
    }

    [Fact]
    public async Task Dml_SerialAndExplicitSequencesCoexistWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE SEQUENCE sequence_batch_seq START 100;
            CREATE TABLE sequence_items (
                id       serial PRIMARY KEY,
                batch_no int NOT NULL DEFAULT nextval('sequence_batch_seq'),
                name     text NOT NULL
            );
            SELECT temporal.enable('sequence_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        var first = await QuerySingleAsync(conn, """
            INSERT INTO sequence_items (name)
            VALUES ('old-one')
            RETURNING id, batch_no, name, changed_by
            """, r => (
                Id: r.GetInt32(0),
                BatchNo: r.GetInt32(1),
                Name: r.GetString(2),
                ChangedBy: r.GetString(3)));
        Assert.Equal((1, 100, "old-one", "alice"), first);

        var second = await QuerySingleAsync(conn, """
            INSERT INTO sequence_items (id, batch_no, name)
            VALUES (nextval('sequence_items_id_seq'), nextval('sequence_batch_seq'), 'old-two')
            RETURNING id, batch_no, name, changed_by
            """, r => (
                Id: r.GetInt32(0),
                BatchNo: r.GetInt32(1),
                Name: r.GetString(2),
                ChangedBy: r.GetString(3)));
        Assert.Equal((2, 101, "old-two", "alice"), second);

        Assert.Equal((2L, 101L), await QuerySingleAsync(conn, """
            SELECT currval('sequence_items_id_seq'), currval('sequence_batch_seq')
            """, r => (IdSeq: r.GetInt64(0), BatchSeq: r.GetInt64(1))));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE sequence_items SET name = 'current-one' WHERE id = 1;
            DELETE FROM sequence_items WHERE id = 2;
            SELECT setval('sequence_batch_seq', 199, false);
            INSERT INTO sequence_items (name) VALUES ('current-three');
            """);

        Assert.Equal((3L, 199L), await QuerySingleAsync(conn, """
            SELECT currval('sequence_items_id_seq'), currval('sequence_batch_seq')
            """, r => (IdSeq: r.GetInt64(0), BatchSeq: r.GetInt64(1))));

        Assert.Equal("1:100:current-one,3:199:current-three", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || batch_no::text || ':' || name, ',' ORDER BY id)
            FROM sequence_items
            """));

        Assert.Equal("1:100:old-one:,2:101:old-two:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || batch_no::text || ':' || name || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
            FROM sequence_items__history
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:100:old-one,2:101:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || batch_no::text || ':' || name, ',' ORDER BY id)
            FROM sequence_items
            """));
        Assert.Equal(199L, await ScalarAsync<long>(conn,
            "SELECT currval('sequence_batch_seq')"));
    }

    [Fact]
    public async Task Dml_ForeignKeyReferentialActionsAreVersioned()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE cascade_parents (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE cascade_children (
                id        int PRIMARY KEY,
                parent_id int REFERENCES cascade_parents(id)
                          ON UPDATE CASCADE
                          ON DELETE SET NULL,
                name      text NOT NULL
            );
            SELECT temporal.enable('cascade_parents', combine_interval => interval '0');
            SELECT temporal.enable('cascade_children', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO cascade_parents VALUES (1, 'parent-old');
            INSERT INTO cascade_children VALUES (10, 1, 'child-old');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var beforeCascade = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE cascade_parents SET id = 2, name = 'parent-current' WHERE id = 1");

        Assert.Equal("2:parent-current:bob", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || changed_by
            FROM cascade_parents
            """));
        Assert.Equal("10:2:child-old:bob", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name || ':' || changed_by
            FROM cascade_children
            """));
        Assert.Equal("1:parent-old:", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || coalesce(deleted_by, '')
            FROM cascade_parents__history
            """));
        Assert.Equal("10:1:child-old:", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name || ':' || coalesce(deleted_by, '')
            FROM cascade_children__history
            """));

        await SetAsOfAsync(conn, beforeCascade!);
        Assert.Equal("1:parent-old", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name
            FROM cascade_parents
            """));
        Assert.Equal("10:1:child-old", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name
            FROM cascade_children
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var beforeDelete = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'carol'");
        await ExecAsync(conn, "DELETE FROM cascade_parents WHERE id = 2");

        Assert.Equal(0L, await ScalarAsync<long>(conn, "SELECT count(*) FROM cascade_parents"));
        Assert.Equal("10::child-old:carol", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || coalesce(parent_id::text, '') || ':' || name || ':' || changed_by
            FROM cascade_children
            """));
        Assert.Equal("2:parent-current:carol", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || deleted_by
            FROM cascade_parents__history
            WHERE id = 2
            """));
        Assert.Equal("10:2:child-old:", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name || ':' || coalesce(deleted_by, '')
            FROM cascade_children__history
            WHERE parent_id = 2
            """));

        await SetAsOfAsync(conn, beforeDelete!);
        Assert.Equal("2:parent-current", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name
            FROM cascade_parents
            """));
        Assert.Equal("10:2:child-old", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name
            FROM cascade_children
            """));
    }

    [Fact]
    public async Task Dml_CompositeForeignKeyReferentialActionsAreVersioned()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE composite_fk_parents (
                tenant_id int NOT NULL,
                id        int NOT NULL,
                name      text NOT NULL,
                PRIMARY KEY (tenant_id, id)
            );
            CREATE TABLE composite_fk_children (
                id               int PRIMARY KEY,
                parent_tenant_id int,
                parent_id        int,
                name             text NOT NULL,
                FOREIGN KEY (parent_tenant_id, parent_id)
                    REFERENCES composite_fk_parents (tenant_id, id)
                    ON UPDATE CASCADE
                    ON DELETE SET NULL
            );
            SELECT temporal.enable('composite_fk_parents', combine_interval => interval '0');
            SELECT temporal.enable('composite_fk_children', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO composite_fk_parents VALUES (7, 1, 'parent-old');
            INSERT INTO composite_fk_children VALUES (100, 7, 1, 'child-old');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var beforeCascade = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE composite_fk_parents
               SET tenant_id = 8,
                   id = 2,
                   name = 'parent-current'
             WHERE tenant_id = 7 AND id = 1
            """);

        Assert.Equal("8:2:parent-current:bob", await ScalarAsync<string>(conn, """
            SELECT tenant_id::text || ':' || id::text || ':' || name || ':' || changed_by
            FROM composite_fk_parents
            """));
        Assert.Equal("100:8:2:child-old:bob", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_tenant_id::text || ':' || parent_id::text || ':' || name || ':' || changed_by
            FROM composite_fk_children
            """));
        Assert.Equal("7:1:parent-old:", await ScalarAsync<string>(conn, """
            SELECT tenant_id::text || ':' || id::text || ':' || name || ':' || coalesce(deleted_by, '')
            FROM composite_fk_parents__history
            """));
        Assert.Equal("100:7:1:child-old:", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_tenant_id::text || ':' || parent_id::text || ':' || name || ':' || coalesce(deleted_by, '')
            FROM composite_fk_children__history
            """));

        await SetAsOfAsync(conn, beforeCascade!);
        Assert.Equal("7:1:parent-old|100:7:1:child-old", await ScalarAsync<string>(conn, """
            SELECT p.tenant_id::text || ':' || p.id::text || ':' || p.name || '|' ||
                   c.id::text || ':' || c.parent_tenant_id::text || ':' || c.parent_id::text || ':' || c.name
            FROM composite_fk_parents p
            JOIN composite_fk_children c
              ON c.parent_tenant_id = p.tenant_id
             AND c.parent_id = p.id
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var beforeDelete = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'carol'");
        await ExecAsync(conn, "DELETE FROM composite_fk_parents WHERE tenant_id = 8 AND id = 2");

        Assert.Equal(0L, await ScalarAsync<long>(conn, "SELECT count(*) FROM composite_fk_parents"));
        Assert.Equal("100:::child-old:carol", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' ||
                   coalesce(parent_tenant_id::text, '') || ':' ||
                   coalesce(parent_id::text, '') || ':' ||
                   name || ':' ||
                   changed_by
            FROM composite_fk_children
            """));
        Assert.Equal("8:2:parent-current:carol", await ScalarAsync<string>(conn, """
            SELECT tenant_id::text || ':' || id::text || ':' || name || ':' || deleted_by
            FROM composite_fk_parents__history
            WHERE tenant_id = 8 AND id = 2
            """));
        Assert.Equal("100:8:2:child-old:", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_tenant_id::text || ':' || parent_id::text || ':' || name || ':' || coalesce(deleted_by, '')
            FROM composite_fk_children__history
            WHERE parent_tenant_id = 8 AND parent_id = 2
            """));

        await SetAsOfAsync(conn, beforeDelete!);
        Assert.Equal("8:2:parent-current|100:8:2:child-old", await ScalarAsync<string>(conn, """
            SELECT p.tenant_id::text || ':' || p.id::text || ':' || p.name || '|' ||
                   c.id::text || ':' || c.parent_tenant_id::text || ':' || c.parent_id::text || ':' || c.name
            FROM composite_fk_parents p
            JOIN composite_fk_children c
              ON c.parent_tenant_id = p.tenant_id
             AND c.parent_id = p.id
            """));
    }

    [Fact]
    public async Task Dml_OnDeleteCascadeReferentialActionsAreVersioned()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE cascade_delete_parents (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE cascade_delete_children (
                id        int PRIMARY KEY,
                parent_id int NOT NULL REFERENCES cascade_delete_parents(id) ON DELETE CASCADE,
                name      text NOT NULL
            );
            SELECT temporal.enable('cascade_delete_parents', combine_interval => interval '0');
            SELECT temporal.enable('cascade_delete_children', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO cascade_delete_parents VALUES (1, 'parent-one'), (2, 'parent-two');
            INSERT INTO cascade_delete_children VALUES
                (10, 1, 'child-one-a'),
                (11, 1, 'child-one-b'),
                (20, 2, 'child-two');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var beforeDelete = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "DELETE FROM cascade_delete_parents WHERE id = 1");

        Assert.Equal("2:parent-two", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name
            FROM cascade_delete_parents
            """));
        Assert.Equal("20:2:child-two", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name
            FROM cascade_delete_children
            """));
        Assert.Equal("1:parent-one:bob", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || deleted_by
            FROM cascade_delete_parents__history
            WHERE id = 1
            """));
        Assert.Equal("10:1:child-one-a:bob,11:1:child-one-b:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || parent_id::text || ':' || name || ':' || deleted_by, ',' ORDER BY id)
            FROM cascade_delete_children__history
            WHERE parent_id = 1
            """));

        await SetAsOfAsync(conn, beforeDelete!);
        Assert.Equal("1:parent-one:10:child-one-a,1:parent-one:11:child-one-b,2:parent-two:20:child-two",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(
                           p.id::text || ':' || p.name || ':' || c.id::text || ':' || c.name,
                           ',' ORDER BY p.id, c.id)
                FROM cascade_delete_parents p
                JOIN cascade_delete_children c ON c.parent_id = p.id
                """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("2:parent-two:20:child-two", await ScalarAsync<string>(conn, """
            SELECT p.id::text || ':' || p.name || ':' || c.id::text || ':' || c.name
            FROM cascade_delete_parents p
            JOIN cascade_delete_children c ON c.parent_id = p.id
            """));
    }

    [Fact]
    public async Task Dml_OnDeleteSetDefaultReferentialActionsAreVersioned()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE default_delete_parents (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE default_delete_children (
                id        int PRIMARY KEY,
                parent_id int NOT NULL DEFAULT 0 REFERENCES default_delete_parents(id) ON DELETE SET DEFAULT,
                name      text NOT NULL
            );
            SELECT temporal.enable('default_delete_parents', combine_interval => interval '0');
            SELECT temporal.enable('default_delete_children', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO default_delete_parents VALUES
                (0, 'fallback-parent'),
                (1, 'parent-old');
            INSERT INTO default_delete_children VALUES (10, 1, 'child-old');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var beforeDelete = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "DELETE FROM default_delete_parents WHERE id = 1");

        Assert.Equal("0:fallback-parent", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name
            FROM default_delete_parents
            """));
        Assert.Equal("10:0:child-old:bob", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name || ':' || changed_by
            FROM default_delete_children
            """));
        Assert.Equal("1:parent-old:bob", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || deleted_by
            FROM default_delete_parents__history
            WHERE id = 1
            """));
        Assert.Equal("10:1:child-old:", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name || ':' || coalesce(deleted_by, '')
            FROM default_delete_children__history
            WHERE id = 10
            """));

        await SetAsOfAsync(conn, beforeDelete!);
        Assert.Equal("1:parent-old|10:1:child-old", await ScalarAsync<string>(conn, """
            SELECT p.id::text || ':' || p.name || '|' ||
                   c.id::text || ':' || c.parent_id::text || ':' || c.name
            FROM default_delete_parents p
            JOIN default_delete_children c ON c.parent_id = p.id
            WHERE p.id = 1
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("0:fallback-parent|10:0:child-old", await ScalarAsync<string>(conn, """
            SELECT p.id::text || ':' || p.name || '|' ||
                   c.id::text || ':' || c.parent_id::text || ':' || c.name
            FROM default_delete_parents p
            JOIN default_delete_children c ON c.parent_id = p.id
            """));
    }

    [Fact]
    public async Task Dml_OnDeleteRestrictRollsBackTemporalHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE restrict_parents (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE restrict_children (
                id        int PRIMARY KEY,
                parent_id int NOT NULL REFERENCES restrict_parents(id) ON DELETE RESTRICT,
                name      text NOT NULL
            );
            SELECT temporal.enable('restrict_parents', combine_interval => interval '0');
            SELECT temporal.enable('restrict_children', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO restrict_parents VALUES (1, 'parent-old');
            INSERT INTO restrict_children VALUES (10, 1, 'child-old');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var beforeUpdate = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE restrict_parents SET name = 'parent-current' WHERE id = 1");

        var restricted = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "DELETE FROM restrict_parents WHERE id = 1"));
        // PG18 reports ON DELETE RESTRICT violations with the dedicated
        // restrict_violation code (23001), distinct from foreign_key_violation.
        Assert.Equal("23001", restricted.SqlState);

        Assert.Equal("1:parent-current:bob", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || changed_by
            FROM restrict_parents
            """));
        Assert.Equal("10:1:child-old:alice", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name || ':' || changed_by
            FROM restrict_children
            """));
        Assert.Equal("1:parent-old:", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || coalesce(deleted_by, '')
            FROM restrict_parents__history
            """));
        Assert.Equal(0L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM restrict_children__history"));
        Assert.Equal(0L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM restrict_parents__history
            WHERE deleted_by IS NOT NULL
            """));

        await SetAsOfAsync(conn, beforeUpdate!);
        Assert.Equal("1:parent-old|10:child-old", await ScalarAsync<string>(conn, """
            SELECT p.id::text || ':' || p.name || '|' || c.id::text || ':' || c.name
            FROM restrict_parents p
            JOIN restrict_children c ON c.parent_id = p.id
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:parent-current|10:child-old", await ScalarAsync<string>(conn, """
            SELECT p.id::text || ':' || p.name || '|' || c.id::text || ':' || c.name
            FROM restrict_parents p
            JOIN restrict_children c ON c.parent_id = p.id
            """));
    }

    [Fact]
    public async Task Ddl_AddForeignKeyAfterEnableValidatesAndReferentialActionsAreVersioned()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE fk_late_parents (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE fk_late_children (
                id        int PRIMARY KEY,
                parent_id int,
                name      text NOT NULL
            );
            SELECT temporal.enable('fk_late_parents', combine_interval => interval '0');
            SELECT temporal.enable('fk_late_children', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO fk_late_parents VALUES (1, 'parent-old');
            INSERT INTO fk_late_children VALUES (10, 1, 'child-old');
            """);

        await ExecAsync(conn, """
            ALTER TABLE fk_late_children
                ADD CONSTRAINT fk_late_children_parent_fk
                FOREIGN KEY (parent_id)
                REFERENCES fk_late_parents(id)
                ON UPDATE CASCADE
                ON DELETE SET NULL
                NOT VALID;
            ALTER TABLE fk_late_children
                VALIDATE CONSTRAINT fk_late_children_parent_fk;
            """);

        var fkViolation = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "INSERT INTO fk_late_children VALUES (99, 999, 'bad-child')"));
        Assert.Equal("23503", fkViolation.SqlState);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var beforeParentUpdate = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE fk_late_parents SET id = 2, name = 'parent-current' WHERE id = 1");

        Assert.Equal("2:parent-current", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name
            FROM fk_late_parents
            """));
        Assert.Equal("10:2:child-old:bob", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name || ':' || changed_by
            FROM fk_late_children
            """));
        Assert.Equal("1:parent-old:", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || coalesce(deleted_by, '')
            FROM fk_late_parents__history
            """));
        Assert.Equal("10:1:child-old:", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name || ':' || coalesce(deleted_by, '')
            FROM fk_late_children__history
            WHERE parent_id = 1
            """));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var beforeParentDelete = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'carol'");
        await ExecAsync(conn, "DELETE FROM fk_late_parents WHERE id = 2");

        Assert.Equal(0L, await ScalarAsync<long>(conn, "SELECT count(*) FROM fk_late_parents"));
        Assert.Equal("10::child-old:carol", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || coalesce(parent_id::text, '') || ':' || name || ':' || changed_by
            FROM fk_late_children
            """));
        Assert.Equal("2:parent-current:carol", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || deleted_by
            FROM fk_late_parents__history
            WHERE id = 2
            """));
        Assert.Equal("10:2:child-old:", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name || ':' || coalesce(deleted_by, '')
            FROM fk_late_children__history
            WHERE parent_id = 2
            """));

        await SetAsOfAsync(conn, beforeParentUpdate!);
        Assert.Equal("1:parent-old", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name
            FROM fk_late_parents
            """));
        Assert.Equal("10:1:child-old", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name
            FROM fk_late_children
            """));

        await SetAsOfAsync(conn, beforeParentDelete!);
        Assert.Equal("2:parent-current", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name
            FROM fk_late_parents
            """));
        Assert.Equal("10:2:child-old", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || parent_id::text || ':' || name
            FROM fk_late_children
            """));
    }

    [Fact]
    public async Task Dml_TransitionTableStatementTriggersCoexistWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE transition_items (
                id   int PRIMARY KEY,
                name text NOT NULL,
                qty  int NOT NULL
            );
            CREATE TABLE transition_audit (
                seq      int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                action   text NOT NULL,
                old_rows text,
                new_rows text
            );
            SELECT temporal.enable('transition_items', combine_interval => interval '0');

            CREATE FUNCTION audit_transition_update()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                INSERT INTO transition_audit (action, old_rows, new_rows)
                VALUES (
                    'update',
                    (SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id) FROM old_rows),
                    (SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id) FROM new_rows)
                );
                RETURN NULL;
            END
            $$;

            CREATE FUNCTION audit_transition_delete()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                INSERT INTO transition_audit (action, old_rows, new_rows)
                VALUES (
                    'delete',
                    (SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id) FROM old_rows),
                    NULL
                );
                RETURN NULL;
            END
            $$;

            CREATE TRIGGER transition_update_audit
            AFTER UPDATE ON transition_items
            REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
            FOR EACH STATEMENT EXECUTE FUNCTION audit_transition_update();

            CREATE TRIGGER transition_delete_audit
            AFTER DELETE ON transition_items
            REFERENCING OLD TABLE AS old_rows
            FOR EACH STATEMENT EXECUTE FUNCTION audit_transition_delete();
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO transition_items VALUES
                (1, 'old-one', 1),
                (2, 'old-two', 2),
                (3, 'old-three', 3);
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE transition_items
               SET name = name || '-current',
                   qty = qty + 10
             WHERE id IN (1, 2);
            """);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await ExecAsync(conn, """
            DELETE FROM transition_items
             WHERE id = 2;
            """);

        Assert.Equal(
            "update|1:old-one:1,2:old-two:2|1:old-one-current:11,2:old-two-current:12;" +
            "delete|2:old-two-current:12|<null>",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(action || '|' || old_rows || '|' || coalesce(new_rows, '<null>'), ';' ORDER BY seq)
                FROM transition_audit
                """));

        var historyRows = await QueryAsync(conn, """
            SELECT id, name, qty, coalesce(deleted_by, '<null>')
            FROM transition_items__history
            ORDER BY id, qty
            """, r => (
                Id: r.GetInt32(0),
                Name: r.GetString(1),
                Qty: r.GetInt32(2),
                DeletedBy: r.GetString(3)));
        Assert.Equal(
            [
                (1, "old-one", 1, "<null>"),
                (2, "old-two", 2, "<null>"),
                (2, "old-two-current", 12, "bob")
            ],
            historyRows);

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-one:1,2:old-two:2,3:old-three:3", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM transition_items
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:old-one-current:11,3:old-three:3", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM transition_items
            """));
    }

    [Fact]
    public async Task Dml_NotifyTriggersCoexistWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var listener = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var writer = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var notifications = new List<string>();
        listener.Notification += (_, e) =>
        {
            if (e.Channel == "temporal_item_events")
                notifications.Add(e.Payload);
        };
        await ExecAsync(listener, "LISTEN temporal_item_events");

        async Task WaitForNotificationsAsync(int expected)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (notifications.Count < expected)
            {
                var remaining = deadline - DateTime.UtcNow;
                Assert.True(remaining > TimeSpan.Zero,
                    $"Timed out waiting for {expected} notifications; received {notifications.Count}.");
                Assert.True(await listener.WaitAsync(remaining, TestContext.Current.CancellationToken),
                    $"Timed out waiting for {expected} notifications; received {notifications.Count}.");
            }
        }

        await ExecAsync(writer, """
            CREATE TABLE notify_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('notify_items', combine_interval => interval '0');

            CREATE FUNCTION notify_item_change()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                item_id int;
                item_name text;
                actor text;
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    item_id := OLD.id;
                    item_name := OLD.name;
                    actor := current_setting('temporal.user_id', true);
                ELSE
                    item_id := NEW.id;
                    item_name := NEW.name;
                    actor := NEW.changed_by;
                END IF;

                PERFORM pg_notify(
                    'temporal_item_events',
                    TG_OP || ':' || item_id::text || ':' || coalesce(actor, '') || ':' || item_name
                );
                RETURN NULL;
            END
            $$;

            CREATE TRIGGER notify_items_change
            AFTER INSERT OR UPDATE OR DELETE ON notify_items
            FOR EACH ROW EXECUTE FUNCTION notify_item_change();
            """);

        await ExecAsync(writer, "SET temporal.user_id = 'alice'");
        await ExecAsync(writer, "INSERT INTO notify_items VALUES (1, 'old-one'), (2, 'old-two')");
        await WaitForNotificationsAsync(2);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(writer, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(writer, "SET temporal.user_id = 'bob'");
        await ExecAsync(writer, "UPDATE notify_items SET name = 'current-one' WHERE id = 1");
        await WaitForNotificationsAsync(3);
        await ExecAsync(writer, "DELETE FROM notify_items WHERE id = 2");
        await WaitForNotificationsAsync(4);

        Assert.Contains("INSERT:1:alice:old-one", notifications);
        Assert.Contains("INSERT:2:alice:old-two", notifications);
        Assert.Contains("UPDATE:1:bob:current-one", notifications);
        Assert.Contains("DELETE:2:bob:old-two", notifications);

        Assert.Equal("1:old-one:alice:,2:old-two:alice:bob", await ScalarAsync<string>(writer, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
            FROM notify_items__history
            """));

        await SetAsOfAsync(writer, asOf!);
        Assert.Equal("1:old-one:alice,2:old-two:alice", await ScalarAsync<string>(writer, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by, ',' ORDER BY id)
            FROM notify_items
            """));

        await ExecAsync(writer, "RESET temporal.as_of");
        Assert.Equal("1:current-one:bob", await ScalarAsync<string>(writer, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by, ',' ORDER BY id)
            FROM notify_items
            """));
    }

    [Fact]
    public async Task Dml_ManagedTriggersFireWhenSessionReplicationRoleIsReplica()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE replication_role_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('replication_role_items', combine_interval => interval '0');
            """);

        Assert.Equal("temporal_history:A,temporal_stamp:A,temporal_truncate:A",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(tgname || ':' || tgenabled::text, ',' ORDER BY tgname)
                FROM pg_trigger
                WHERE tgrelid = 'replication_role_items'::regclass
                  AND tgname LIKE 'temporal_%'
                """));
        Assert.Equal("temporal_protect:A,temporal_protect_truncate:A",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(tgname || ':' || tgenabled::text, ',' ORDER BY tgname)
                FROM pg_trigger
                WHERE tgrelid = 'replication_role_items__history'::regclass
                  AND tgname LIKE 'temporal_%'
                """));

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO replication_role_items VALUES (1, 'old-one'), (2, 'old-two')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "SET session_replication_role = replica");
        try
        {
            await ExecAsync(conn, """
                UPDATE replication_role_items SET name = 'current-one' WHERE id = 1;
                DELETE FROM replication_role_items WHERE id = 2;
                INSERT INTO replication_role_items VALUES (3, 'current-three');
                """);

            var directHistoryWrite = await Assert.ThrowsAsync<PostgresException>(async () =>
                await ExecAsync(conn, """
                    INSERT INTO replication_role_items__history
                        (id, name, valid_from, changed_by, valid_to, deleted_by)
                    VALUES
                        (99, 'bypass', clock_timestamp(), 'mallory', clock_timestamp(), NULL)
                    """));
            Assert.Equal("42501", directHistoryWrite.SqlState);
        }
        finally
        {
            await ExecAsync(conn, "SET session_replication_role = origin");
        }

        Assert.Equal("1:old-one:alice:,2:old-two:alice:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
            FROM replication_role_items__history
            """));
        Assert.Equal("1:current-one:bob,3:current-three:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by, ',' ORDER BY id)
            FROM replication_role_items
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-one:alice,2:old-two:alice", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by, ',' ORDER BY id)
            FROM replication_role_items
            """));
    }

    [Fact]
    public async Task Dml_DeferrableConstraintTriggersCoexistWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE constraint_trigger_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE constraint_trigger_audit (
                seq        int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                action     text NOT NULL,
                item_id    int NOT NULL,
                name       text NOT NULL,
                changed_by text NOT NULL
            );
            SELECT temporal.enable('constraint_trigger_items', combine_interval => interval '0');

            CREATE FUNCTION audit_constraint_trigger_items()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    INSERT INTO constraint_trigger_audit (action, item_id, name, changed_by)
                    VALUES (TG_OP, OLD.id, OLD.name, OLD.changed_by);
                ELSE
                    INSERT INTO constraint_trigger_audit (action, item_id, name, changed_by)
                    VALUES (TG_OP, NEW.id, NEW.name, NEW.changed_by);
                END IF;
                RETURN NULL;
            END
            $$;

            CREATE CONSTRAINT TRIGGER constraint_trigger_items_audit
            AFTER INSERT OR UPDATE OR DELETE ON constraint_trigger_items
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION audit_constraint_trigger_items();
            """);

        await ExecAsync(conn, "BEGIN");
        try
        {
            await ExecAsync(conn, "SET temporal.user_id = 'alice'");
            await ExecAsync(conn, """
                INSERT INTO constraint_trigger_items VALUES
                    (1, 'old-one'),
                    (2, 'old-two');
                """);

            Assert.Equal(0L, await ScalarAsync<long>(conn,
                "SELECT count(*) FROM constraint_trigger_audit"));

            await ExecAsync(conn, "SET CONSTRAINTS ALL IMMEDIATE");
            Assert.Equal("INSERT:1:old-one:alice,INSERT:2:old-two:alice", await ScalarAsync<string>(conn, """
                SELECT string_agg(action || ':' || item_id::text || ':' || name || ':' || changed_by, ',' ORDER BY seq)
                FROM constraint_trigger_audit
                """));
            await ExecAsync(conn, "COMMIT");
        }
        catch
        {
            await ExecAsync(conn, "ROLLBACK");
            throw;
        }

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "BEGIN");
        try
        {
            await ExecAsync(conn, "SET temporal.user_id = 'bob'");
            await ExecAsync(conn, """
                UPDATE constraint_trigger_items SET name = 'current-one' WHERE id = 1;
                DELETE FROM constraint_trigger_items WHERE id = 2;
                """);

            Assert.Equal(2L, await ScalarAsync<long>(conn,
                "SELECT count(*) FROM constraint_trigger_audit"));

            await ExecAsync(conn, "SET CONSTRAINTS ALL IMMEDIATE");
            Assert.Equal(
                "DELETE:2:old-two:alice,INSERT:1:old-one:alice,INSERT:2:old-two:alice,UPDATE:1:current-one:bob",
                await ScalarAsync<string>(conn, """
                    SELECT string_agg(action || ':' || item_id::text || ':' || name || ':' || changed_by, ',' ORDER BY action, item_id, seq)
                    FROM constraint_trigger_audit
                    """));
            await ExecAsync(conn, "COMMIT");
        }
        catch
        {
            await ExecAsync(conn, "ROLLBACK");
            throw;
        }

        Assert.Equal("1:old-one:alice:,2:old-two:alice:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
            FROM constraint_trigger_items__history
            """));
        Assert.Equal("1:current-one:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by, ',' ORDER BY id)
            FROM constraint_trigger_items
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-one:alice,2:old-two:alice", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by, ',' ORDER BY id)
            FROM constraint_trigger_items
            """));
    }

    [Fact]
    public async Task Dml_DeferrableConstraintsExclusionAndIdentityOverride_Coexist()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE EXTENSION btree_gist;

            CREATE TABLE booking_accounts (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE bookings (
                id         int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                account_id int NOT NULL,
                room       text NOT NULL,
                during     tstzrange NOT NULL,
                status     text NOT NULL,
                CONSTRAINT bookings_account_fk
                    FOREIGN KEY (account_id)
                    REFERENCES booking_accounts(id)
                    DEFERRABLE INITIALLY DEFERRED,
                CONSTRAINT bookings_no_overlap
                    EXCLUDE USING gist (room WITH =, during WITH &&)
                    DEFERRABLE INITIALLY IMMEDIATE
            );
            SELECT temporal.enable('bookings', include_indexes => true, combine_interval => interval '0');
            """);

        var exclusionMirror = await QuerySingleAsync(conn, """
            SELECT h.indisunique, pg_get_indexdef(m.history_index_oid)
            FROM temporal.mirrored_indexes m
            JOIN pg_index h ON h.indexrelid = m.history_index_oid
            WHERE m.base_index_oid = 'bookings_no_overlap'::regclass
            """, r => (IsUnique: r.GetBoolean(0), Definition: r.GetString(1)));
        Assert.False(exclusionMirror.IsUnique);
        Assert.Contains("USING gist", exclusionMirror.Definition, StringComparison.OrdinalIgnoreCase);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "BEGIN");
        try
        {
            await ExecAsync(conn, "SET CONSTRAINTS ALL DEFERRED");
            await ExecAsync(conn, """
                INSERT INTO bookings (id, account_id, room, during, status)
                    OVERRIDING SYSTEM VALUE
                VALUES (
                    42,
                    7,
                    'alpha',
                    tstzrange('2026-01-01 09:00+00', '2026-01-01 10:00+00', '[)'),
                    'planned'
                )
                """);
            await ExecAsync(conn, "INSERT INTO booking_accounts VALUES (7, 'Acme')");
            await ExecAsync(conn, "COMMIT");
        }
        catch
        {
            await ExecAsync(conn, "ROLLBACK");
            throw;
        }

        var inserted = await QuerySingleAsync(conn, """
            SELECT id, account_id, room, status, changed_by
            FROM bookings
            WHERE id = 42
            """, r => (
                Id: r.GetInt32(0),
                AccountId: r.GetInt32(1),
                Room: r.GetString(2),
                Status: r.GetString(3),
                ChangedBy: r.GetString(4)));
        Assert.Equal((42, 7, "alpha", "planned", "alice"), inserted);

        var overlap = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, """
                INSERT INTO bookings (account_id, room, during, status)
                VALUES (
                    7,
                    'alpha',
                    tstzrange('2026-01-01 09:30+00', '2026-01-01 10:30+00', '[)'),
                    'overlap'
                )
                """));
        Assert.Equal("23P01", overlap.SqlState);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE bookings SET status = 'moved' WHERE id = 42");

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("planned", await ScalarAsync<string>(conn,
            "SELECT status FROM bookings WHERE id = 42"));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("moved", await ScalarAsync<string>(conn,
            "SELECT status FROM bookings WHERE id = 42"));
        Assert.Equal("planned", await ScalarAsync<string>(conn, """
            SELECT status
            FROM bookings__history
            WHERE id = 42
            ORDER BY valid_to DESC
            LIMIT 1
            """));
    }

    [Fact]
    public async Task Dml_CopyFrom_StampsRowsAndIsBlockedUnderAsOf()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE copy_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('copy_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await using (var writer = await conn.BeginTextImportAsync(
                         "COPY copy_items (id, name) FROM STDIN WITH (FORMAT csv)",
                         TestContext.Current.CancellationToken))
        {
            await writer.WriteLineAsync("1,bulk-one");
            await writer.WriteLineAsync("2,bulk-two");
        }

        var inserted = await QueryAsync(conn,
            "SELECT id, name, changed_by FROM copy_items ORDER BY id",
            r => (Id: r.GetInt32(0), Name: r.GetString(1), ChangedBy: r.GetString(2)));
        Assert.Equal(
            [(1, "bulk-one", "alice"), (2, "bulk-two", "alice")],
            inserted);

        Assert.Equal(0L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM copy_items__history"));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE copy_items SET name = 'bulk-one-current' WHERE id = 1");

        await SetAsOfAsync(conn, asOf!);
        var historicName = await ScalarAsync<string>(conn,
            "SELECT name FROM copy_items WHERE id = 1");
        Assert.Equal("bulk-one", historicName);

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var writer = await conn.BeginTextImportAsync(
                "COPY copy_items (id, name) FROM STDIN WITH (FORMAT csv)",
                TestContext.Current.CancellationToken);
            await writer.WriteLineAsync("3,blocked");
        });
        Assert.Equal("25006", ex.SqlState);

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal(2L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM copy_items"));
    }

    [Fact]
    public async Task Dml_OnConflictNamedConstraintWhereClause_IsVersionedAndBlockedUnderAsOf()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE conflict_items (
                id      int PRIMARY KEY,
                sku     text NOT NULL,
                version int NOT NULL,
                status  text NOT NULL,
                CONSTRAINT conflict_items_sku_unique UNIQUE (sku)
            );
            SELECT temporal.enable('conflict_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO conflict_items VALUES
                (1, 'A', 1, 'old-a'),
                (2, 'B', 1, 'old-b');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        var updated = await QuerySingleAsync(conn, """
            INSERT INTO conflict_items (id, sku, version, status)
            VALUES (10, 'A', 2, 'current-a')
            ON CONFLICT ON CONSTRAINT conflict_items_sku_unique DO UPDATE
                SET version = EXCLUDED.version,
                    status = EXCLUDED.status
                WHERE conflict_items.version < EXCLUDED.version
            RETURNING id, sku, version, status, changed_by
            """, r => (
                Id: r.GetInt32(0),
                Sku: r.GetString(1),
                Version: r.GetInt32(2),
                Status: r.GetString(3),
                ChangedBy: r.GetString(4)));
        Assert.Equal((1, "A", 2, "current-a", "bob"), updated);

        var skipped = await QueryAsync(conn, """
            INSERT INTO conflict_items (id, sku, version, status)
            VALUES (11, 'A', 1, 'stale-a')
            ON CONFLICT ON CONSTRAINT conflict_items_sku_unique DO UPDATE
                SET version = EXCLUDED.version,
                    status = EXCLUDED.status
                WHERE conflict_items.version < EXCLUDED.version
            RETURNING id
            """, r => r.GetInt32(0));
        Assert.Empty(skipped);

        var inserted = await QuerySingleAsync(conn, """
            INSERT INTO conflict_items (id, sku, version, status)
            VALUES (3, 'C', 1, 'current-c')
            ON CONFLICT ON CONSTRAINT conflict_items_sku_unique DO UPDATE
                SET version = EXCLUDED.version,
                    status = EXCLUDED.status
                WHERE conflict_items.version < EXCLUDED.version
            RETURNING id, sku, version, status, changed_by
            """, r => (
                Id: r.GetInt32(0),
                Sku: r.GetString(1),
                Version: r.GetInt32(2),
                Status: r.GetString(3),
                ChangedBy: r.GetString(4)));
        Assert.Equal((3, "C", 1, "current-c", "bob"), inserted);

        Assert.Equal("1:A:1:old-a:alice:", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || sku || ':' || version::text || ':' || status || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY valid_to)
            FROM conflict_items__history
            """));
        Assert.Equal("1:A:2:current-a:bob,2:B:1:old-b:alice,3:C:1:current-c:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || sku || ':' || version::text || ':' || status || ':' || changed_by, ',' ORDER BY id)
            FROM conflict_items
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:A:1:old-a,2:B:1:old-b", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || sku || ':' || version::text || ':' || status, ',' ORDER BY id)
            FROM conflict_items
            """));

        var blocked = await Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(conn, """
            INSERT INTO conflict_items (id, sku, version, status)
            VALUES (12, 'A', 3, 'blocked-a')
            ON CONFLICT ON CONSTRAINT conflict_items_sku_unique DO UPDATE
                SET version = EXCLUDED.version,
                    status = EXCLUDED.status
            """));
        Assert.Equal("25006", blocked.SqlState);
    }

    [Fact]
    public async Task Dml_ComposedStatements_AreVersioned()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE seed_items (
                id  int PRIMARY KEY,
                sku text NOT NULL,
                qty int NOT NULL
            );
            CREATE TABLE items (
                id     int PRIMARY KEY,
                sku    text NOT NULL UNIQUE,
                qty    int NOT NULL,
                status text NOT NULL
            );
            CREATE TABLE adjustments (
                id        int PRIMARY KEY,
                delta     int NOT NULL,
                delete_me boolean NOT NULL
            );
            CREATE TABLE merge_source (
                id     int PRIMARY KEY,
                sku    text NOT NULL,
                qty    int NOT NULL,
                status text NOT NULL
            );
            CREATE TABLE audit_log (
                id      int GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                item_id int NOT NULL,
                status  text NOT NULL
            );
            SELECT temporal.enable('items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        var inserted = await QueryAsync(conn, """
            WITH seeded AS (
                INSERT INTO seed_items VALUES (1, 'A', 10), (2, 'B', 20)
                RETURNING id, sku, qty
            )
            INSERT INTO items (id, sku, qty, status)
            SELECT id, sku, qty, 'seeded'
            FROM seeded
            RETURNING id, changed_by
            """, r => (Id: r.GetInt32(0), ChangedBy: r.GetString(1)));

        Assert.Equal([1, 2], inserted.Select(r => r.Id).Order().ToArray());
        Assert.All(inserted, r => Assert.Equal("alice", r.ChangedBy));
        Assert.Equal(0L, await ScalarAsync<long>(conn, "SELECT count(*) FROM items__history"));

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        var upserted = await QuerySingleAsync(conn, """
            INSERT INTO items (id, sku, qty, status)
            VALUES (1, 'A', 15, 'upserted')
            ON CONFLICT (id) DO UPDATE
                SET qty = EXCLUDED.qty,
                    status = EXCLUDED.status
            RETURNING id, qty, status, changed_by
            """, r => (
                Id: r.GetInt32(0),
                Qty: r.GetInt32(1),
                Status: r.GetString(2),
                ChangedBy: r.GetString(3)));

        Assert.Equal((1, 15, "upserted", "bob"), upserted);
        Assert.Equal("seeded", await ScalarAsync<string>(conn,
            "SELECT status FROM items__history WHERE id = 1 ORDER BY valid_to DESC LIMIT 1"));

        var skippedConflict = await ScalarAsync<int>(conn, """
            INSERT INTO items (id, sku, qty, status)
            VALUES (1, 'A-conflict', 999, 'skipped')
            ON CONFLICT (id) DO NOTHING
            RETURNING id
            """);
        Assert.Equal(0, skippedConflict);
        Assert.Equal("1:upserted:bob", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || status || ':' || changed_by
            FROM items
            WHERE id = 1
            """));
        Assert.Equal(1L, await ScalarAsync<long>(conn, "SELECT count(*) FROM items__history WHERE id = 1"));

        await ExecAsync(conn, "INSERT INTO adjustments VALUES (1, 5, false), (2, -20, true)");
        await ExecAsync(conn, "SET temporal.user_id = 'carol'");
        var adjusted = await QueryAsync(conn, """
            UPDATE items AS i
               SET qty = i.qty + a.delta,
                   status = CASE WHEN a.delete_me THEN 'remove' ELSE 'adjusted' END
            FROM adjustments AS a
            WHERE a.id = i.id
            RETURNING i.id, i.qty, i.status, i.changed_by
            """, r => (
                Id: r.GetInt32(0),
                Qty: r.GetInt32(1),
                Status: r.GetString(2),
                ChangedBy: r.GetString(3)));

        Assert.Contains(adjusted, r => r is { Id: 1, Qty: 20, Status: "adjusted", ChangedBy: "carol" });
        Assert.Contains(adjusted, r => r is { Id: 2, Qty: 0, Status: "remove", ChangedBy: "carol" });

        await ExecAsync(conn, "SET temporal.user_id = 'dave'");
        var deleted = await QuerySingleAsync(conn, """
            DELETE FROM items AS i
            USING adjustments AS a
            WHERE a.id = i.id AND a.delete_me
            RETURNING i.id, i.status
            """, r => (Id: r.GetInt32(0), Status: r.GetString(1)));

        Assert.Equal((2, "remove"), deleted);
        Assert.Equal("dave", await ScalarAsync<string>(conn, """
            SELECT deleted_by
            FROM items__history
            WHERE id = 2
            ORDER BY valid_to DESC
            LIMIT 1
            """));

        await ExecAsync(conn, "SET temporal.user_id = 'erin'");
        await ExecAsync(conn, "INSERT INTO merge_source VALUES (1, 'A', 99, 'merged'), (3, 'C', 30, 'merged-insert')");
        await ExecAsync(conn, """
            MERGE INTO items AS i
            USING merge_source AS s
            ON i.id = s.id
            WHEN MATCHED THEN
                UPDATE SET sku = s.sku, qty = s.qty, status = s.status
            WHEN NOT MATCHED THEN
                INSERT (id, sku, qty, status)
                VALUES (s.id, s.sku, s.qty, s.status)
            """);

        var mergedCurrent = await QueryAsync(conn,
            "SELECT id, qty, status, changed_by FROM items ORDER BY id",
            r => (Id: r.GetInt32(0), Qty: r.GetInt32(1), Status: r.GetString(2), ChangedBy: r.GetString(3)));

        Assert.Contains(mergedCurrent, r => r is { Id: 1, Qty: 99, Status: "merged", ChangedBy: "erin" });
        Assert.Contains(mergedCurrent, r => r is { Id: 3, Qty: 30, Status: "merged-insert", ChangedBy: "erin" });
        Assert.Equal(3L, await ScalarAsync<long>(conn, "SELECT count(*) FROM items__history WHERE id = 1"));
        Assert.Equal(0L, await ScalarAsync<long>(conn, "SELECT count(*) FROM items__history WHERE id = 3"));

        await ExecAsync(conn, "SET temporal.user_id = 'frank'");
        var cteAudit = await QuerySingleAsync(conn, """
            WITH changed AS (
                UPDATE items
                   SET status = 'cte-updated',
                       qty = qty + 1
                 WHERE id = 3
                 RETURNING id, status
            )
            INSERT INTO audit_log (item_id, status)
            SELECT id, status FROM changed
            RETURNING item_id, status
            """, r => (ItemId: r.GetInt32(0), Status: r.GetString(1)));

        Assert.Equal((3, "cte-updated"), cteAudit);
        Assert.Equal(1L, await ScalarAsync<long>(conn, "SELECT count(*) FROM items__history WHERE id = 3"));

        await ExecAsync(conn, "SET temporal.user_id = 'gina'");
        await ExecAsync(conn, "DELETE FROM merge_source");
        await ExecAsync(conn, """
            INSERT INTO merge_source VALUES
                (1, 'A', 0, 'merge-delete'),
                (3, 'C', 0, 'merge-skip'),
                (4, 'D', 40, 'merge-ignored');
            """);
        await ExecAsync(conn, """
            MERGE INTO items AS i
            USING merge_source AS s
            ON i.id = s.id
            WHEN MATCHED AND s.status = 'merge-delete' THEN
                DELETE
            WHEN MATCHED AND s.status = 'merge-skip' THEN
                DO NOTHING
            WHEN NOT MATCHED THEN
                DO NOTHING
            """);

        Assert.Equal("3:cte-updated:frank", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || status || ':' || changed_by, ',' ORDER BY id)
            FROM items
            """));
        Assert.Equal("gina", await ScalarAsync<string>(conn, """
            SELECT deleted_by
            FROM items__history
            WHERE id = 1
            ORDER BY valid_to DESC
            LIMIT 1
            """));
        Assert.Equal("merged", await ScalarAsync<string>(conn, """
            SELECT status
            FROM items__history
            WHERE id = 1
            ORDER BY valid_to DESC
            LIMIT 1
            """));
        Assert.Equal(1L, await ScalarAsync<long>(conn, "SELECT count(*) FROM items__history WHERE id = 3"));
    }

    [Fact]
    public async Task Dml_UpdatableViewsAreVersionedAndBlockedUnderAsOf()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE view_items (
                id   int PRIMARY KEY,
                name text NOT NULL,
                qty  int NOT NULL
            );
            SELECT temporal.enable('view_items', combine_interval => interval '0');
            CREATE VIEW visible_view_items AS
            SELECT id, name, qty
            FROM view_items
            WHERE qty >= 0
            WITH LOCAL CHECK OPTION;
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO visible_view_items VALUES
                (1, 'old-one', 10),
                (2, 'old-two', 20);
            """);

        Assert.Equal("alice", await ScalarAsync<string>(conn,
            "SELECT changed_by FROM view_items WHERE id = 1"));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE visible_view_items SET name = 'current-one', qty = 110 WHERE id = 1;
            DELETE FROM visible_view_items WHERE id = 2;
            INSERT INTO visible_view_items VALUES (3, 'current-three', 30);
            """);

        await SetAsOfAsync(conn, asOf!);

        Assert.Equal("1:old-one:10,2:old-two:20", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM visible_view_items
            """));

        foreach (var sql in new[]
        {
            "INSERT INTO visible_view_items VALUES (4, 'blocked', 40)",
            "UPDATE visible_view_items SET name = 'blocked' WHERE id = 1",
            "DELETE FROM visible_view_items WHERE id = 1"
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(conn, sql));
            Assert.Equal("25006", ex.SqlState);
        }

        await ExecAsync(conn, "RESET temporal.as_of");

        Assert.Equal("1:current-one:110,3:current-three:30", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM visible_view_items
            """));
    }

    [Fact]
    public async Task Dml_InsteadOfTriggerViewsAreVersionedAndBlockedUnderAsOf()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE trigger_view_items (
                id   int PRIMARY KEY,
                name text NOT NULL,
                qty  int NOT NULL
            );
            SELECT temporal.enable('trigger_view_items', combine_interval => interval '0');

            CREATE VIEW writable_trigger_view_items AS
            SELECT id, name, qty
            FROM trigger_view_items;

            CREATE FUNCTION writable_trigger_view_items_dml()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    INSERT INTO trigger_view_items (id, name, qty)
                    VALUES (NEW.id, NEW.name, NEW.qty);
                    RETURN NEW;
                ELSIF TG_OP = 'UPDATE' THEN
                    UPDATE trigger_view_items
                       SET name = NEW.name,
                           qty = NEW.qty
                     WHERE id = OLD.id;
                    RETURN NEW;
                ELSE
                    DELETE FROM trigger_view_items
                     WHERE id = OLD.id;
                    RETURN OLD;
                END IF;
            END
            $$;

            CREATE TRIGGER writable_trigger_view_items_insert
            INSTEAD OF INSERT ON writable_trigger_view_items
            FOR EACH ROW EXECUTE FUNCTION writable_trigger_view_items_dml();

            CREATE TRIGGER writable_trigger_view_items_update
            INSTEAD OF UPDATE ON writable_trigger_view_items
            FOR EACH ROW EXECUTE FUNCTION writable_trigger_view_items_dml();

            CREATE TRIGGER writable_trigger_view_items_delete
            INSTEAD OF DELETE ON writable_trigger_view_items
            FOR EACH ROW EXECUTE FUNCTION writable_trigger_view_items_dml();
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO writable_trigger_view_items VALUES
                (1, 'old-one', 10),
                (2, 'old-two', 20);
            """);

        Assert.Equal("alice", await ScalarAsync<string>(conn,
            "SELECT changed_by FROM trigger_view_items WHERE id = 1"));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE writable_trigger_view_items SET name = 'current-one', qty = 110 WHERE id = 1;
            DELETE FROM writable_trigger_view_items WHERE id = 2;
            INSERT INTO writable_trigger_view_items VALUES (3, 'current-three', 30);
            """);

        Assert.Equal("1:old-one:10:,2:old-two:20:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
            FROM trigger_view_items__history
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-one:10,2:old-two:20", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM writable_trigger_view_items
            """));

        foreach (var sql in new[]
        {
            "INSERT INTO writable_trigger_view_items VALUES (4, 'blocked', 40)",
            "UPDATE writable_trigger_view_items SET name = 'blocked' WHERE id = 1",
            "DELETE FROM writable_trigger_view_items WHERE id = 1"
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(conn, sql));
            Assert.Equal("25006", ex.SqlState);
        }

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current-one:110,3:current-three:30", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM writable_trigger_view_items
            """));
    }

    [Fact]
    public async Task AsOf_RewriteRulesCanPopulateUntrackedTablesFromHistoricalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE rule_source_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE rule_requests (
                id        int PRIMARY KEY,
                source_id int NOT NULL
            );
            CREATE TABLE rule_exports (
                request_id int PRIMARY KEY,
                source_id  int NOT NULL,
                name       text NOT NULL
            );
            SELECT temporal.enable('rule_source_items', combine_interval => interval '0');

            CREATE RULE rule_requests_export AS
            ON INSERT TO rule_requests
            DO ALSO
                INSERT INTO rule_exports (request_id, source_id, name)
                SELECT NEW.id, s.id, s.name
                FROM rule_source_items s
                WHERE s.id = NEW.source_id;
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO rule_source_items VALUES (1, 'old-name')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE rule_source_items SET name = 'current-name' WHERE id = 1");

        await SetAsOfAsync(conn, asOf!);
        await ExecAsync(conn, "INSERT INTO rule_requests VALUES (10, 1)");
        await ExecAsync(conn, "RESET temporal.as_of");
        await ExecAsync(conn, "INSERT INTO rule_requests VALUES (11, 1)");

        Assert.Equal("10:1:old-name,11:1:current-name", await ScalarAsync<string>(conn, """
            SELECT string_agg(request_id::text || ':' || source_id::text || ':' || name, ',' ORDER BY request_id)
            FROM rule_exports
            """));
    }

    [Fact]
    public async Task Dml_RewriteRulesOnTrackedTablesCoexistWithHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE rule_dml_items (
                id   int PRIMARY KEY,
                name text NOT NULL,
                qty  int NOT NULL
            );
            CREATE TABLE rule_dml_audit (
                seq      int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                action   text NOT NULL,
                item_id  int NOT NULL,
                old_name text NOT NULL,
                new_name text
            );
            SELECT temporal.enable('rule_dml_items', combine_interval => interval '0');

            CREATE RULE rule_dml_update_audit AS
            ON UPDATE TO rule_dml_items
            DO ALSO
                INSERT INTO rule_dml_audit (action, item_id, old_name, new_name)
                VALUES ('update', OLD.id, OLD.name, NEW.name);

            CREATE RULE rule_dml_delete_audit AS
            ON DELETE TO rule_dml_items
            DO ALSO
                INSERT INTO rule_dml_audit (action, item_id, old_name, new_name)
                VALUES ('delete', OLD.id, OLD.name, NULL);
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO rule_dml_items VALUES (1, 'old-one', 10), (2, 'old-two', 20)");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE rule_dml_items SET name = 'current-one', qty = 110 WHERE id = 1;
            DELETE FROM rule_dml_items WHERE id = 2;
            """);

        Assert.Equal("update:1:old-one:current-one,delete:2:old-two:<null>",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(action || ':' || item_id::text || ':' || old_name || ':' || coalesce(new_name, '<null>'), ',' ORDER BY seq)
                FROM rule_dml_audit
                """));
        Assert.Equal("1:old-one:10:alice:,2:old-two:20:alice:bob",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || name || ':' || qty::text || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
                FROM rule_dml_items__history
                """));
        Assert.Equal("1:current-one:110:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text || ':' || changed_by, ',' ORDER BY id)
            FROM rule_dml_items
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-one:10,2:old-two:20", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM rule_dml_items
            """));
    }

    [Fact]
    public async Task AsOf_UntrackedWriteStatementsCanReadHistoricalTemporalRows()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE source_items (
                id   int PRIMARY KEY,
                name text NOT NULL,
                qty  int NOT NULL
            );
            CREATE TABLE insert_exports (
                id   int PRIMARY KEY,
                name text NOT NULL,
                qty  int NOT NULL
            );
            CREATE TABLE merge_exports (
                id   int PRIMARY KEY,
                name text NOT NULL,
                qty  int NOT NULL
            );
            CREATE TABLE cte_exports (
                id   int PRIMARY KEY,
                name text NOT NULL,
                qty  int NOT NULL
            );
            SELECT temporal.enable('source_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO source_items VALUES
                (1, 'old-one', 10),
                (2, 'old-two', 20);
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE source_items SET name = 'current-one', qty = 110 WHERE id = 1;
            DELETE FROM source_items WHERE id = 2;
            INSERT INTO source_items VALUES (3, 'current-three', 30);
            """);

        await SetAsOfAsync(conn, asOf!);

        await ExecAsync(conn, """
            CREATE TEMP TABLE snapshot_items AS
            SELECT id, name, qty
            FROM source_items
            ORDER BY id
            """);

        Assert.Equal("1:old-one:10,2:old-two:20", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM snapshot_items
            """));

        await ExecAsync(conn, """
            INSERT INTO insert_exports (id, name, qty)
            SELECT id, name, qty
            FROM source_items
            ORDER BY id
            """);

        await ExecAsync(conn, """
            UPDATE insert_exports AS e
               SET name = s.name || '-updated',
                   qty = s.qty + 1
            FROM source_items AS s
            WHERE s.id = e.id
              AND e.id = 1
            """);

        await ExecAsync(conn, """
            DELETE FROM insert_exports AS e
            USING source_items AS s
            WHERE s.id = e.id
              AND s.id = 2
            """);

        Assert.Equal("1:old-one-updated:11", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM insert_exports
            """));

        await ExecAsync(conn, """
            WITH historical AS MATERIALIZED (
                SELECT id, name, qty
                FROM source_items
                WHERE id IN (1, 2, 3)
            ),
            inserted AS (
                INSERT INTO cte_exports (id, name, qty)
                SELECT id, name || '-cte', qty + 100
                FROM historical
                RETURNING id
            )
            SELECT count(*) FROM inserted
            """);

        Assert.Equal("1:old-one-cte:110,2:old-two-cte:120", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM cte_exports
            """));

        await ExecAsync(conn, "INSERT INTO merge_exports VALUES (1, 'seed', 0)");
        await ExecAsync(conn, """
            MERGE INTO merge_exports AS m
            USING source_items AS s
            ON m.id = s.id
            WHEN MATCHED THEN
                UPDATE SET name = s.name || '-merged', qty = s.qty
            WHEN NOT MATCHED THEN
                INSERT (id, name, qty) VALUES (s.id, s.name, s.qty)
            """);

        Assert.Equal("1:old-one-merged:10,2:old-two:20", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM merge_exports
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current-one:110,3:current-three:30", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || qty::text, ',' ORDER BY id)
            FROM source_items
            """));
    }

    [Fact]
    public async Task AsOf_RowLockingClausesOnTrackedTablesAreBlocked()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE lock_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('lock_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO lock_items VALUES (1, 'old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE lock_items SET name = 'current' WHERE id = 1");

        await SetAsOfAsync(conn, asOf!);

        foreach (var sql in new[]
        {
            "SELECT id FROM lock_items WHERE id = 1 FOR UPDATE",
            "SELECT id FROM lock_items WHERE id = 1 FOR NO KEY UPDATE",
            "SELECT id FROM lock_items WHERE id = 1 FOR SHARE",
            "SELECT id FROM lock_items WHERE id = 1 FOR KEY SHARE"
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(conn, sql));
            Assert.Equal("25006", ex.SqlState);
        }

        await ExecAsync(conn, "BEGIN");
        try
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
                await ExecAsync(conn, "LOCK TABLE lock_items IN ACCESS SHARE MODE"));
            Assert.Equal("25006", ex.SqlState);
        }
        finally
        {
            await ExecAsync(conn, "ROLLBACK");
        }

        await ExecAsync(conn, "RESET temporal.as_of");
        await ExecAsync(conn, "BEGIN");
        await ExecAsync(conn, "LOCK TABLE lock_items IN ACCESS SHARE MODE");
        await ExecAsync(conn, "COMMIT");

        Assert.Equal("current", await ScalarAsync<string>(conn,
            "SELECT name FROM lock_items WHERE id = 1 FOR UPDATE"));
    }

    [Fact]
    public async Task AsOf_RowLockingUntrackedJoinRowsCanReadTrackedHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE lock_owners (
                id     int PRIMARY KEY,
                marker text NOT NULL
            );
            CREATE TABLE lock_join_items (
                id       int PRIMARY KEY,
                owner_id int NOT NULL REFERENCES lock_owners(id),
                name     text NOT NULL
            );
            SELECT temporal.enable('lock_join_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO lock_owners VALUES (1, 'owner-one');
            INSERT INTO lock_join_items VALUES (10, 1, 'old-one');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE lock_join_items SET name = 'current-one' WHERE id = 10");

        await SetAsOfAsync(conn, asOf!);
        var allowed = await QuerySingleAsync(conn, """
            SELECT o.marker, i.name
            FROM lock_owners AS o
            JOIN lock_join_items AS i ON i.owner_id = o.id
            WHERE o.id = 1
            FOR UPDATE OF o
            """, r => (Owner: r.GetString(0), Item: r.GetString(1)));
        Assert.Equal(("owner-one", "old-one"), allowed);

        var blocked = await Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(conn, """
            SELECT i.id
            FROM lock_owners AS o
            JOIN lock_join_items AS i ON i.owner_id = o.id
            WHERE o.id = 1
            FOR UPDATE OF i
            """));
        Assert.Equal("25006", blocked.SqlState);

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("current-one", await ScalarAsync<string>(conn, """
            SELECT i.name
            FROM lock_owners AS o
            JOIN lock_join_items AS i ON i.owner_id = o.id
            WHERE o.id = 1
            FOR UPDATE OF i
            """));
    }

    [Fact]
    public async Task AsOf_StoredProceduresReadHistoryButCannotModifyTrackedTables()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE procedure_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE procedure_exports (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('procedure_items', combine_interval => interval '0');

            CREATE PROCEDURE export_procedure_items()
            LANGUAGE plpgsql
            AS $$
            BEGIN
                INSERT INTO procedure_exports (id, name)
                SELECT id, name
                FROM procedure_items
                ORDER BY id;
            END
            $$;

            CREATE PROCEDURE update_procedure_items()
            LANGUAGE plpgsql
            AS $$
            BEGIN
                UPDATE procedure_items SET name = 'blocked' WHERE id = 1;
            END
            $$;
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO procedure_items VALUES (1, 'old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE procedure_items SET name = 'current' WHERE id = 1;
            INSERT INTO procedure_items VALUES (2, 'current-only');
            """);

        await SetAsOfAsync(conn, asOf!);
        await ExecAsync(conn, "CALL export_procedure_items()");

        Assert.Equal("1:old", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM procedure_exports
            """));

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "CALL update_procedure_items()"));
        Assert.Equal("25006", ex.SqlState);

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current,2:current-only", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM procedure_items
            """));
    }

    [Fact]
    public async Task AsOf_AnonymousBlocksDynamicSqlAndTableCopyToUseTemporalRules()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE dynamic_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE dynamic_exports (
                export_id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                item_id   int NOT NULL,
                name      text NOT NULL,
                source    text NOT NULL
            );
            CREATE TABLE dynamic_trigger_requests (
                request_id   int PRIMARY KEY,
                block_update boolean NOT NULL DEFAULT false
            );
            SELECT temporal.enable('dynamic_items', combine_interval => interval '0');

            CREATE FUNCTION export_dynamic_items(p_suffix text)
            RETURNS void
            LANGUAGE plpgsql
            AS $$
            BEGIN
                EXECUTE format(
                    'INSERT INTO dynamic_exports (item_id, name, source)
                     SELECT id, name || %L, %L
                     FROM dynamic_items
                     ORDER BY id',
                    p_suffix,
                    'function-dynamic');
            END
            $$;

            CREATE FUNCTION blocked_dynamic_item_update()
            RETURNS void
            LANGUAGE plpgsql
            AS $$
            BEGIN
                EXECUTE 'UPDATE dynamic_items SET name = ''blocked'' WHERE id = 1';
            END
            $$;

            CREATE FUNCTION dynamic_request_trigger()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF NEW.block_update THEN
                    UPDATE dynamic_items SET name = 'blocked' WHERE id = 1;
                ELSE
                    INSERT INTO dynamic_exports (item_id, name, source)
                    SELECT id, name, 'trigger-' || NEW.request_id::text
                    FROM dynamic_items
                    ORDER BY id;
                END IF;

                RETURN NEW;
            END
            $$;

            CREATE TRIGGER dynamic_request_after_insert
            AFTER INSERT ON dynamic_trigger_requests
            FOR EACH ROW EXECUTE FUNCTION dynamic_request_trigger();
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO dynamic_items VALUES (1, 'old-one'), (2, 'old-two')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE dynamic_items SET name = 'current-one' WHERE id = 1;
            DELETE FROM dynamic_items WHERE id = 2;
            INSERT INTO dynamic_items VALUES (3, 'current-three');
            """);

        await SetAsOfAsync(conn, asOf!);
        await ExecAsync(conn, """
            DO $$
            BEGIN
                INSERT INTO dynamic_exports (item_id, name, source)
                SELECT id, name, 'do-static'
                FROM dynamic_items
                ORDER BY id;

                EXECUTE
                    'INSERT INTO dynamic_exports (item_id, name, source)
                     SELECT id, name, ''do-dynamic''
                     FROM dynamic_items
                     ORDER BY id';
            END
            $$;
            """);
        await ExecAsync(conn, "SELECT export_dynamic_items('-fn')");
        await ExecAsync(conn, "INSERT INTO dynamic_trigger_requests (request_id) VALUES (10)");

        Assert.Equal(
            "do-dynamic:1:old-one,do-dynamic:2:old-two," +
            "do-static:1:old-one,do-static:2:old-two," +
            "function-dynamic:1:old-one-fn,function-dynamic:2:old-two-fn," +
            "trigger-10:1:old-one,trigger-10:2:old-two",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(source || ':' || item_id::text || ':' || name, ',' ORDER BY source, item_id)
                FROM dynamic_exports
                """));

        var copyTo = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            using var reader = await conn.BeginTextExportAsync(
                "COPY dynamic_items TO STDOUT WITH (FORMAT csv)",
                TestContext.Current.CancellationToken);
        });
        Assert.Equal("0A000", copyTo.SqlState);

        var dynamicWrite = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "SELECT blocked_dynamic_item_update()"));
        Assert.Equal("25006", dynamicWrite.SqlState);

        var doWrite = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, """
                DO $$
                BEGIN
                    UPDATE dynamic_items SET name = 'blocked' WHERE id = 1;
                END
                $$;
                """));
        Assert.Equal("25006", doWrite.SqlState);

        var triggerWrite = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "INSERT INTO dynamic_trigger_requests VALUES (11, true)"));
        Assert.Equal("25006", triggerWrite.SqlState);

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current-one,3:current-three", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM dynamic_items
            """));
    }

    [Fact]
    public async Task AsOf_BlocksTrackedDmlShapes()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('items', combine_interval => interval '0');
            INSERT INTO items VALUES (1, 'present');
            """);

        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await SetAsOfAsync(conn, asOf!);

        var blockedStatements = new[]
        {
            "INSERT INTO items VALUES (2, 'inserted')",
            "UPDATE items SET name = 'updated' WHERE id = 1",
            """
            UPDATE items AS i
               SET name = s.name
            FROM (VALUES (1, 'updated-from')) AS s(id, name)
            WHERE i.id = s.id
            """,
            """
            DELETE FROM items AS i
            USING (VALUES (1)) AS s(id)
            WHERE i.id = s.id
            """,
            """
            MERGE INTO items AS i
            USING (VALUES (1, 'merged')) AS s(id, name)
            ON i.id = s.id
            WHEN MATCHED THEN
                UPDATE SET name = s.name
            WHEN NOT MATCHED THEN
                INSERT (id, name) VALUES (s.id, s.name)
            """,
            """
            WITH changed AS (
                UPDATE items SET name = 'cte' WHERE id = 1 RETURNING id
            )
            SELECT count(*) FROM changed
            """
        };

        foreach (var sql in blockedStatements)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(conn, sql));
            Assert.Equal("25006", ex.SqlState);
        }
    }

    [Fact]
    public async Task Dml_UnloggedTables_AreVersionedAndReadableAsOf()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE UNLOGGED TABLE unlogged_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('unlogged_items', combine_interval => interval '0');
            """);

        Assert.Equal("u", await ScalarAsync<string>(conn, """
            SELECT relpersistence::text
            FROM pg_class
            WHERE oid = 'unlogged_items'::regclass
            """));

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO unlogged_items VALUES (1, 'old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE unlogged_items SET name = 'current' WHERE id = 1;
            INSERT INTO unlogged_items VALUES (2, 'current-only');
            """);

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM unlogged_items
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current,2:current-only", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM unlogged_items
            """));
    }

    [Fact]
    public async Task Ddl_SetLoggedAndUnloggedPreservesTrackingAndHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE persistence_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('persistence_items', include_indexes => true, combine_interval => interval '0');
            CREATE INDEX persistence_items_name_idx ON persistence_items (name);
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO persistence_items VALUES (1, 'logged-old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var beforePersistenceChange = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "ALTER TABLE persistence_items SET UNLOGGED");
        Assert.Equal("u", await ScalarAsync<string>(conn, """
            SELECT relpersistence::text
            FROM pg_class
            WHERE oid = 'persistence_items'::regclass
            """));
        Assert.Equal("p", await ScalarAsync<string>(conn, """
            SELECT relpersistence::text
            FROM pg_class
            WHERE oid = 'persistence_items__history'::regclass
            """));

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE persistence_items SET name = 'unlogged-current' WHERE id = 1");

        await ExecAsync(conn, "ALTER TABLE persistence_items SET LOGGED");
        Assert.Equal("p", await ScalarAsync<string>(conn, """
            SELECT relpersistence::text
            FROM pg_class
            WHERE oid = 'persistence_items'::regclass
            """));

        await ExecAsync(conn, "SET temporal.user_id = 'carol'");
        await ExecAsync(conn, """
            UPDATE persistence_items SET name = 'logged-current' WHERE id = 1;
            INSERT INTO persistence_items VALUES (2, 'logged-new');
            """);

        Assert.Equal("persistence_items:temporal_history:A,persistence_items:temporal_stamp:A,persistence_items:temporal_truncate:A," +
                     "persistence_items__history:temporal_protect:A,persistence_items__history:temporal_protect_truncate:A",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(tgrelid::regclass::text || ':' || tgname || ':' || tgenabled::text, ',' ORDER BY tgrelid::regclass::text, tgname)
                FROM pg_trigger
                WHERE tgname IN ('temporal_history',
                                 'temporal_protect',
                                 'temporal_protect_truncate',
                                 'temporal_stamp',
                                 'temporal_truncate')
                  AND tgrelid IN ('persistence_items'::regclass, 'persistence_items__history'::regclass)
                """));
        Assert.Equal(1L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'persistence_items'::regclass
              AND base_index_oid = 'persistence_items_name_idx'::regclass
            """));
        Assert.Equal("1:logged-old:alice:,1:unlogged-current:bob:", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY valid_to)
            FROM persistence_items__history
            """));

        await SetAsOfAsync(conn, beforePersistenceChange!);
        Assert.Equal("1:logged-old", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM persistence_items
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:logged-current,2:logged-new", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM persistence_items
            """));
    }

    [Fact]
    public async Task Ddl_CommentsAndOwnershipChangesPreserveTemporalBehavior()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'temporal_feature_metadata_owner') THEN
                    CREATE ROLE temporal_feature_metadata_owner;
                END IF;
            END
            $$;

            CREATE TABLE metadata_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('metadata_items', combine_interval => interval '0');

            COMMENT ON TABLE metadata_items IS 'base temporal metadata comment';
            COMMENT ON COLUMN metadata_items.name IS 'base name comment';
            COMMENT ON TABLE metadata_items__history IS 'history temporal metadata comment';
            COMMENT ON VIEW metadata_items__versions IS 'versions temporal metadata comment';

            ALTER TABLE metadata_items OWNER TO temporal_feature_metadata_owner;
            ALTER TABLE metadata_items__history OWNER TO temporal_feature_metadata_owner;
            ALTER VIEW metadata_items__versions OWNER TO temporal_feature_metadata_owner;
            GRANT USAGE ON SCHEMA public TO temporal_feature_metadata_owner;
            GRANT USAGE ON SCHEMA temporal TO temporal_feature_metadata_owner;
            """);

        var metadata = await QuerySingleAsync(conn, """
            SELECT obj_description('metadata_items'::regclass),
                   col_description('metadata_items'::regclass, (
                       SELECT attnum
                       FROM pg_attribute
                       WHERE attrelid = 'metadata_items'::regclass
                         AND attname = 'name'
                   )),
                   obj_description('metadata_items__history'::regclass),
                   obj_description('metadata_items__versions'::regclass),
                   pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid = 'metadata_items'::regclass)),
                   pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid = 'metadata_items__history'::regclass)),
                   pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid = 'metadata_items__versions'::regclass))
            """, r => (
                BaseComment: r.GetString(0),
                ColumnComment: r.GetString(1),
                HistoryComment: r.GetString(2),
                VersionsComment: r.GetString(3),
                BaseOwner: r.GetString(4),
                HistoryOwner: r.GetString(5),
                VersionsOwner: r.GetString(6)));
        Assert.Equal(
            ("base temporal metadata comment",
             "base name comment",
             "history temporal metadata comment",
             "versions temporal metadata comment",
             "temporal_feature_metadata_owner",
             "temporal_feature_metadata_owner",
             "temporal_feature_metadata_owner"),
            metadata);

        await ExecAsync(conn, "SET ROLE temporal_feature_metadata_owner");
        try
        {
            await ExecAsync(conn, "SET temporal.user_id = 'alice'");
            await ExecAsync(conn, "INSERT INTO metadata_items VALUES (1, 'old')");

            await Task.Delay(50, TestContext.Current.CancellationToken);
            var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
            await Task.Delay(50, TestContext.Current.CancellationToken);

            await ExecAsync(conn, "SET temporal.user_id = 'bob'");
            await ExecAsync(conn, """
                UPDATE metadata_items SET name = 'current' WHERE id = 1;
                INSERT INTO metadata_items VALUES (2, 'current-only');
                """);

            await SetAsOfAsync(conn, asOf!);
            Assert.Equal("1:old:alice", await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || name || ':' || changed_by, ',' ORDER BY id)
                FROM metadata_items
                """));

            await ExecAsync(conn, "RESET temporal.as_of");
            Assert.Equal("1:current:bob,2:current-only:bob", await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || name || ':' || changed_by, ',' ORDER BY id)
                FROM metadata_items
                """));
            Assert.Equal("1:old:alice:", await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || name || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY valid_to)
                FROM metadata_items__history
                """));
        }
        finally
        {
            await ExecAsync(conn, "RESET ROLE");
        }
    }

    [Fact]
    public async Task Ddl_InheritanceRelationshipsCannotBeAddedAfterEnable()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE tracked_parent_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE tracked_child_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE ordinary_parent_items (
                id   int,
                name text
            );
            CREATE TABLE ordinary_partition_parent_items (
                id   int NOT NULL,
                name text,
                PRIMARY KEY (id)
            ) PARTITION BY RANGE (id);
            SELECT temporal.enable('tracked_parent_items', combine_interval => interval '0');
            SELECT temporal.enable('tracked_child_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO tracked_parent_items VALUES (1, 'old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE tracked_parent_items SET name = 'current' WHERE id = 1");

        foreach (var sql in new[]
        {
            "CREATE TABLE tracked_parent_items_child (extra text) INHERITS (tracked_parent_items)",
            "ALTER TABLE tracked_child_items INHERIT ordinary_parent_items",
            "ALTER TABLE ordinary_partition_parent_items ATTACH PARTITION tracked_child_items FOR VALUES FROM (0) TO (100)"
        })
        {
            // Each is blocked — either by our event trigger (legacy inheritance,
            // child/partition attach of a tracked table) or by PostgreSQL itself
            // (the tracked child carries managed columns the parent lacks). The
            // count check below verifies no relationship was actually established.
            await Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(conn, sql));
        }

        Assert.Equal(0L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM pg_inherits
            WHERE inhparent IN ('tracked_parent_items'::regclass, 'tracked_child_items'::regclass)
               OR inhrelid IN ('tracked_parent_items'::regclass, 'tracked_child_items'::regclass)
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("old", await ScalarAsync<string>(conn,
            "SELECT name FROM tracked_parent_items WHERE id = 1"));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("current", await ScalarAsync<string>(conn,
            "SELECT name FROM tracked_parent_items WHERE id = 1"));
    }

    [Fact]
    public async Task Ddl_MaintenanceCommandsAndExtendedStatisticsPreserveTemporalReads()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE maintenance_items (
                id       int PRIMARY KEY,
                category text NOT NULL,
                status   text NOT NULL,
                name     text NOT NULL
            );
            SELECT temporal.enable('maintenance_items', include_indexes => true, combine_interval => interval '0');
            CREATE INDEX maintenance_items_name_idx ON maintenance_items (name);
            CREATE STATISTICS maintenance_items_status_stats (dependencies, ndistinct, mcv)
                ON category, status
                FROM maintenance_items;
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO maintenance_items VALUES
                (1, 'core', 'open', 'alpha'),
                (2, 'edge', 'open', 'beta'),
                (3, 'edge', 'closed', 'gamma');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE maintenance_items SET status = 'closed', name = 'alpha-current' WHERE id = 1;
            DELETE FROM maintenance_items WHERE id = 2;
            INSERT INTO maintenance_items VALUES (4, 'core', 'open', 'delta');
            """);

        await ExecAsync(conn, "ANALYZE maintenance_items");
        await ExecAsync(conn, "VACUUM maintenance_items");
        await ExecAsync(conn, "REINDEX TABLE maintenance_items");
        await ExecAsync(conn, "CLUSTER maintenance_items USING maintenance_items_name_idx");

        Assert.Equal(1L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM pg_statistic_ext
            WHERE stxrelid = 'maintenance_items'::regclass
              AND stxname = 'maintenance_items_status_stats'
            """));
        Assert.Equal(1L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'maintenance_items'::regclass
              AND base_index_oid = 'maintenance_items_name_idx'::regclass
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:open:alpha,2:open:beta,3:closed:gamma", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || status || ':' || name, ',' ORDER BY id)
            FROM maintenance_items
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:closed:alpha-current,3:closed:gamma,4:open:delta", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || status || ':' || name, ',' ORDER BY id)
            FROM maintenance_items
            """));
    }

    [Fact]
    public async Task Ddl_ReplicaIdentityChangesPreserveHistoryReads()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE replica_identity_items (
                id   int PRIMARY KEY,
                code text NOT NULL,
                name text NOT NULL
            );
            SELECT temporal.enable('replica_identity_items', include_indexes => true, combine_interval => interval '0');
            CREATE UNIQUE INDEX replica_identity_items_code_uidx
                ON replica_identity_items (code);
            ALTER TABLE replica_identity_items
                REPLICA IDENTITY USING INDEX replica_identity_items_code_uidx;
            """);

        Assert.Equal("i", await ScalarAsync<string>(conn, """
            SELECT relreplident::text
            FROM pg_class
            WHERE oid = 'replica_identity_items'::regclass
            """));
        Assert.Equal("replica_identity_items_code_uidx", await ScalarAsync<string>(conn, """
            SELECT c.relname
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            WHERE i.indrelid = 'replica_identity_items'::regclass
              AND i.indisreplident
            """));

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO replica_identity_items VALUES (1, 'A', 'old-one'), (2, 'B', 'old-two')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "ALTER TABLE replica_identity_items REPLICA IDENTITY FULL");
        Assert.Equal("f", await ScalarAsync<string>(conn, """
            SELECT relreplident::text
            FROM pg_class
            WHERE oid = 'replica_identity_items'::regclass
            """));

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE replica_identity_items SET name = 'current-one' WHERE code = 'A';
            DELETE FROM replica_identity_items WHERE code = 'B';
            INSERT INTO replica_identity_items VALUES (3, 'C', 'current-three');
            """);

        Assert.Equal("1:A:old-one:alice:,2:B:old-two:alice:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || code || ':' || name || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
            FROM replica_identity_items__history
            """));
        Assert.Equal("1:A:current-one:bob,3:C:current-three:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || code || ':' || name || ':' || changed_by, ',' ORDER BY id)
            FROM replica_identity_items
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:A:old-one,2:B:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || code || ':' || name, ',' ORDER BY id)
            FROM replica_identity_items
            """));
    }

    [Fact]
    public async Task Ddl_TableAccessMethodsPreserveHistoryReads()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE ACCESS METHOD temporal_heap_am TYPE TABLE HANDLER heap_tableam_handler;
            CREATE ACCESS METHOD temporal_heap_am_rewrite TYPE TABLE HANDLER heap_tableam_handler;

            CREATE TABLE table_am_items (
                id   int PRIMARY KEY,
                code text NOT NULL UNIQUE,
                name text NOT NULL
            ) USING temporal_heap_am;
            SELECT temporal.enable('table_am_items', include_indexes => true, combine_interval => interval '0');
            """);

        var initialAccessMethods = await QuerySingleAsync(conn, """
            SELECT base_am.amname, history_am.amname
            FROM temporal.tracked_tables tt
            JOIN pg_class base_rel ON base_rel.oid = tt.table_oid
            JOIN pg_am base_am ON base_am.oid = base_rel.relam
            JOIN pg_class history_rel ON history_rel.oid = tt.history_table_oid
            JOIN pg_am history_am ON history_am.oid = history_rel.relam
            WHERE tt.table_oid = 'table_am_items'::regclass
            """, r => (Base: r.GetString(0), History: r.GetString(1)));
        Assert.Equal(("temporal_heap_am", "heap"), initialAccessMethods);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO table_am_items VALUES (1, 'A', 'old-one'), (2, 'B', 'old-two')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "ALTER TABLE table_am_items SET ACCESS METHOD temporal_heap_am_rewrite");
        Assert.Equal("temporal_heap_am_rewrite", await ScalarAsync<string>(conn, """
            SELECT am.amname
            FROM pg_class c
            JOIN pg_am am ON am.oid = c.relam
            WHERE c.oid = 'table_am_items'::regclass
            """));

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE table_am_items SET name = 'current-one' WHERE code = 'A';
            DELETE FROM table_am_items WHERE code = 'B';
            INSERT INTO table_am_items VALUES (3, 'C', 'current-three');
            """);

        Assert.Equal("1:A:old-one:alice:,2:B:old-two:alice:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || code || ':' || name || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
            FROM table_am_items__history
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:A:old-one,2:B:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || code || ':' || name, ',' ORDER BY id)
            FROM table_am_items
            """));
    }

    [Fact]
    public async Task Ddl_HashAndBrinIndexesAreMirroredAndPreserveHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE access_method_items (
                id     int PRIMARY KEY,
                code   text NOT NULL,
                bucket int NOT NULL,
                name   text NOT NULL
            );
            SELECT temporal.enable('access_method_items', include_indexes => true, combine_interval => interval '0');

            CREATE INDEX access_method_items_code_hash_idx
                ON access_method_items USING hash (code);
            CREATE INDEX access_method_items_bucket_brin_idx
                ON access_method_items USING brin (bucket) WITH (pages_per_range = 1);
            """);

        var mirroredDefinitions = await QueryAsync(conn, """
            SELECT pg_get_indexdef(history_index_oid)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'access_method_items'::regclass
              AND base_index_oid IN (
                  'access_method_items_code_hash_idx'::regclass,
                  'access_method_items_bucket_brin_idx'::regclass
              )
            ORDER BY pg_get_indexdef(history_index_oid)
            """, r => r.GetString(0));

        Assert.Equal(2, mirroredDefinitions.Count);
        Assert.Contains(mirroredDefinitions, d => d.Contains("USING hash", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mirroredDefinitions, d => d.Contains("USING brin", StringComparison.OrdinalIgnoreCase));

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO access_method_items VALUES
                (1, 'old-a', 10, 'old-one'),
                (2, 'old-b', 110, 'old-two');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE access_method_items
               SET code = 'current-a',
                   bucket = 210,
                   name = 'current-one'
             WHERE id = 1;
            DELETE FROM access_method_items WHERE id = 2;
            INSERT INTO access_method_items VALUES (3, 'current-c', 310, 'current-three');
            """);

        Assert.Equal("1:old-a:10:alice:,2:old-b:110:alice:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || code || ':' || bucket::text || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
            FROM access_method_items__history
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-a:10:old-one,2:old-b:110:old-two", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || code || ':' || bucket::text || ':' || name, ',' ORDER BY id)
            FROM access_method_items
            WHERE code = 'old-a'
               OR bucket BETWEEN 100 AND 120
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current-a:210:current-one,3:current-c:310:current-three", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || code || ':' || bucket::text || ':' || name, ',' ORDER BY id)
            FROM access_method_items
            WHERE code = 'current-a'
               OR bucket >= 300
            """));
    }

    [Fact]
    public async Task Ddl_SpGistIndexesAreMirroredAndPreserveHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE spgist_index_items (
                id    int PRIMARY KEY,
                ip    inet NOT NULL,
                label text NOT NULL
            );
            SELECT temporal.enable('spgist_index_items', include_indexes => true, combine_interval => interval '0');

            CREATE INDEX spgist_index_items_ip_idx
                ON spgist_index_items USING spgist (ip);
            """);

        var mirrorDefinition = await ScalarAsync<string>(conn, """
            SELECT pg_get_indexdef(history_index_oid)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'spgist_index_items'::regclass
              AND base_index_oid = 'spgist_index_items_ip_idx'::regclass
            """);
        Assert.NotNull(mirrorDefinition);
        Assert.Contains("USING spgist", mirrorDefinition, StringComparison.OrdinalIgnoreCase);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO spgist_index_items VALUES
                (1, inet '10.1.2.3', 'old-private'),
                (2, inet '192.168.1.10', 'old-lan');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE spgist_index_items
               SET ip = inet '172.16.5.5',
                   label = 'current-private'
             WHERE id = 1;
            DELETE FROM spgist_index_items WHERE id = 2;
            INSERT INTO spgist_index_items VALUES (3, inet '203.0.113.8', 'current-public');
            """);

        Assert.Equal("1:10.1.2.3:old-private:alice:,2:192.168.1.10:old-lan:alice:bob",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(id::text || ':' || host(ip) || ':' || label || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
                FROM spgist_index_items__history
                """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:10.1.2.3:old-private,2:192.168.1.10:old-lan", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || host(ip) || ':' || label, ',' ORDER BY id)
            FROM spgist_index_items
            WHERE ip << inet '10.0.0.0/8'
               OR ip << inet '192.168.0.0/16'
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:172.16.5.5:current-private,3:203.0.113.8:current-public", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || host(ip) || ':' || label, ',' ORDER BY id)
            FROM spgist_index_items
            WHERE ip << inet '172.16.0.0/12'
               OR ip << inet '203.0.113.0/24'
            """));
    }

    [Fact]
    public async Task Ddl_AlterTableConstraintsAfterEnableMirrorIndexesAndPreserveHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE constraint_ddl_items (
                id   int PRIMARY KEY,
                code text NOT NULL,
                qty  int NOT NULL
            );
            SELECT temporal.enable('constraint_ddl_items', include_indexes => true, combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO constraint_ddl_items VALUES (1, 'A', 10), (2, 'B', 20)");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE constraint_ddl_items SET qty = 15 WHERE id = 1;
            DELETE FROM constraint_ddl_items WHERE id = 2;
            INSERT INTO constraint_ddl_items VALUES (3, 'C', 30);
            """);

        await ExecAsync(conn, """
            ALTER TABLE constraint_ddl_items
                ADD CONSTRAINT constraint_ddl_items_qty_positive CHECK (qty > 0) NOT VALID;
            ALTER TABLE constraint_ddl_items
                VALIDATE CONSTRAINT constraint_ddl_items_qty_positive;
            ALTER TABLE constraint_ddl_items
                ADD CONSTRAINT constraint_ddl_items_code_unique UNIQUE (code);
            """);

        var checkViolation = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "INSERT INTO constraint_ddl_items VALUES (4, 'D', -1)"));
        Assert.Equal("23514", checkViolation.SqlState);

        var uniqueViolation = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "INSERT INTO constraint_ddl_items VALUES (4, 'A', 40)"));
        Assert.Equal("23505", uniqueViolation.SqlState);

        var historyUniqueMirror = await ScalarAsync<string>(conn, """
            SELECT format('%I.%I', n.nspname, c.relname)
            FROM pg_constraint con
            JOIN temporal.mirrored_indexes m ON m.base_index_oid = con.conindid
            JOIN pg_class c ON c.oid = m.history_index_oid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE con.conrelid = 'constraint_ddl_items'::regclass
              AND con.conname = 'constraint_ddl_items_code_unique'
            """);
        Assert.NotNull(historyUniqueMirror);

        await ExecAsync(conn, "ALTER TABLE constraint_ddl_items DROP CONSTRAINT constraint_ddl_items_code_unique");

        Assert.Null(await ScalarAsync<string>(conn, """
            SELECT conname
            FROM pg_constraint
            WHERE conrelid = 'constraint_ddl_items'::regclass
              AND conname = 'constraint_ddl_items_code_unique'
            """));
        Assert.Null(await ScalarAsync<string>(conn,
            $"SELECT to_regclass('{historyUniqueMirror}')::text"));
        Assert.Equal(0L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'constraint_ddl_items'::regclass
              AND base_index_oid::text = 'constraint_ddl_items_code_unique'
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:A:10,2:B:20", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || code || ':' || qty::text, ',' ORDER BY id)
            FROM constraint_ddl_items
            """));
    }

    [Fact]
    public async Task Ddl_AddExclusionConstraintAfterEnableMirrorsIndexAndPreservesHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE EXTENSION btree_gist;

            CREATE TABLE exclusion_ddl_bookings (
                id     int PRIMARY KEY,
                room   text NOT NULL,
                during tstzrange NOT NULL,
                label  text NOT NULL
            );
            SELECT temporal.enable('exclusion_ddl_bookings', include_indexes => true, combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, """
            INSERT INTO exclusion_ddl_bookings VALUES
                (1, 'blue', tstzrange('2026-01-01 09:00+00', '2026-01-01 10:00+00', '[)'), 'old-blue'),
                (2, 'red',  tstzrange('2026-01-01 09:00+00', '2026-01-01 10:00+00', '[)'), 'old-red');
            """);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            ALTER TABLE exclusion_ddl_bookings
                ADD CONSTRAINT exclusion_ddl_bookings_no_overlap
                EXCLUDE USING gist (room WITH =, during WITH &&)
                DEFERRABLE INITIALLY IMMEDIATE;
            """);

        var mirror = await QuerySingleAsync(conn, """
            SELECT h.indisunique, pg_get_indexdef(m.history_index_oid)
            FROM pg_constraint con
            JOIN temporal.mirrored_indexes m ON m.base_index_oid = con.conindid
            JOIN pg_index h ON h.indexrelid = m.history_index_oid
            WHERE con.conrelid = 'exclusion_ddl_bookings'::regclass
              AND con.conname = 'exclusion_ddl_bookings_no_overlap'
            """, r => (IsUnique: r.GetBoolean(0), Definition: r.GetString(1)));
        Assert.False(mirror.IsUnique);
        Assert.Contains("USING gist", mirror.Definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("room", mirror.Definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("during", mirror.Definition, StringComparison.OrdinalIgnoreCase);

        var overlap = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, """
                INSERT INTO exclusion_ddl_bookings VALUES
                    (3, 'blue', tstzrange('2026-01-01 09:30+00', '2026-01-01 09:45+00', '[)'), 'overlap');
                """));
        Assert.Equal("23P01", overlap.SqlState);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE exclusion_ddl_bookings
               SET during = tstzrange('2026-01-01 11:00+00', '2026-01-01 12:00+00', '[)'),
                   label = 'current-blue'
             WHERE id = 1;
            DELETE FROM exclusion_ddl_bookings WHERE id = 2;
            INSERT INTO exclusion_ddl_bookings VALUES
                (4, 'blue', tstzrange('2026-01-01 13:00+00', '2026-01-01 14:00+00', '[)'), 'current-blue-new');
            """);

        Assert.Equal("1:blue:old-blue:alice:,2:red:old-red:alice:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || room || ':' || label || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
            FROM exclusion_ddl_bookings__history
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:blue:old-blue,2:red:old-red", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || room || ':' || label, ',' ORDER BY id)
            FROM exclusion_ddl_bookings
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:blue:current-blue,4:blue:current-blue-new", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || room || ':' || label, ',' ORDER BY id)
            FROM exclusion_ddl_bookings
            """));
    }

    [Fact]
    public async Task Ddl_CoveringOperatorClassIndexesAreMirroredAndPreserveHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE cover_index_items (
                id     int PRIMARY KEY,
                code   text NOT NULL,
                name   text NOT NULL,
                qty    int NOT NULL,
                active boolean NOT NULL
            );
            SELECT temporal.enable('cover_index_items', include_indexes => true, combine_interval => interval '0');

            CREATE INDEX cover_index_items_code_cover_idx
                ON cover_index_items (code COLLATE "C" text_pattern_ops)
                INCLUDE (name, qty)
                WHERE active;
            """);

        var mirroredDefinition = await ScalarAsync<string>(conn, """
            SELECT pg_get_indexdef(history_index_oid)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'cover_index_items'::regclass
              AND base_index_oid = 'cover_index_items_code_cover_idx'::regclass
            """);
        Assert.Contains("text_pattern_ops", mirroredDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INCLUDE (name, qty)", mirroredDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE active", mirroredDefinition, StringComparison.OrdinalIgnoreCase);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO cover_index_items VALUES (1, 'old-alpha', 'old-name', 10, true)");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE cover_index_items
               SET code = 'current-alpha',
                   name = 'current-name',
                   qty = 20
             WHERE id = 1
            """);

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("old-alpha:old-name:10", await ScalarAsync<string>(conn, """
            SELECT code || ':' || name || ':' || qty::text
            FROM cover_index_items
            WHERE code COLLATE "C" LIKE 'old-%'
              AND active
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("current-alpha:current-name:20", await ScalarAsync<string>(conn, """
            SELECT code || ':' || name || ':' || qty::text
            FROM cover_index_items
            WHERE code COLLATE "C" LIKE 'current-%'
              AND active
            """));
    }

    [Fact]
    public async Task Ddl_AddColumnWithExplicitCollationPropagatesToHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE collated_column_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('collated_column_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO collated_column_items VALUES (1, 'old-name')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            ALTER TABLE collated_column_items
                ADD COLUMN sort_key text COLLATE "C" NOT NULL DEFAULT 'middle';
            """);

        var collationShape = await QuerySingleAsync(conn, """
            SELECT base_coll.collname,
                   history_coll.collname,
                   history_attr.attnotnull,
                   history_def.oid IS NOT NULL
            FROM pg_attribute base_attr
            JOIN pg_attribute history_attr
              ON history_attr.attrelid = 'collated_column_items__history'::regclass
             AND history_attr.attname = base_attr.attname
            LEFT JOIN pg_attrdef history_def
              ON history_def.adrelid = history_attr.attrelid
             AND history_def.adnum = history_attr.attnum
            JOIN pg_collation base_coll ON base_coll.oid = base_attr.attcollation
            JOIN pg_collation history_coll ON history_coll.oid = history_attr.attcollation
            WHERE base_attr.attrelid = 'collated_column_items'::regclass
              AND base_attr.attname = 'sort_key'
            """, r => (
                BaseCollation: r.GetString(0),
                HistoryCollation: r.GetString(1),
                HistoryNotNull: r.GetBoolean(2),
                HistoryHasDefault: r.GetBoolean(3)));
        Assert.Equal(("C", "C", false, false), collationShape);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE collated_column_items SET name = 'current-name', sort_key = 'zulu' WHERE id = 1");

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("old-name:middle", await ScalarAsync<string>(conn, """
            SELECT name || ':' || sort_key
            FROM collated_column_items
            WHERE sort_key COLLATE "C" < 'z'
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("current-name:zulu", await ScalarAsync<string>(conn, """
            SELECT name || ':' || sort_key
            FROM collated_column_items
            WHERE sort_key COLLATE "C" = 'zulu'
            """));
    }

    [Fact]
    public async Task Ddl_AddColumnWithSchemaQualifiedCustomTypesPropagatesToHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE SCHEMA app_types;
            CREATE TYPE app_types.priority_level AS ENUM ('low', 'high');
            CREATE DOMAIN app_types.positive_cents AS integer CHECK (VALUE >= 0);
            CREATE TYPE app_types.contact_card AS (
                email text,
                opted_in boolean
            );

            CREATE TABLE custom_type_column_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('custom_type_column_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO custom_type_column_items VALUES (1, 'old-name')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var beforeAlter = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            ALTER TABLE custom_type_column_items
                ADD COLUMN priority app_types.priority_level NOT NULL DEFAULT 'low'::app_types.priority_level,
                ADD COLUMN amount app_types.positive_cents NOT NULL DEFAULT 10,
                ADD COLUMN contact app_types.contact_card NOT NULL
                    DEFAULT ROW('old@example.test', true)::app_types.contact_card;
            """);

        var propagatedColumns = await QueryAsync(conn, """
            SELECT b.attname,
                   b.atttypid = h.atttypid,
                   h.attnotnull,
                   d.oid IS NOT NULL
            FROM pg_attribute b
            JOIN pg_attribute h
              ON h.attrelid = 'custom_type_column_items__history'::regclass
             AND h.attname = b.attname
            LEFT JOIN pg_attrdef d
              ON d.adrelid = h.attrelid
             AND d.adnum = h.attnum
            WHERE b.attrelid = 'custom_type_column_items'::regclass
              AND b.attname IN ('priority', 'amount', 'contact')
            ORDER BY b.attname
            """, r => (
                Name: r.GetString(0),
                SameType: r.GetBoolean(1),
                HistoryNotNull: r.GetBoolean(2),
                HistoryHasDefault: r.GetBoolean(3)));

        Assert.Equal(
            [
                ("amount", true, false, false),
                ("contact", true, false, false),
                ("priority", true, false, false)
            ],
            propagatedColumns);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE custom_type_column_items
               SET name = 'current-name',
                   priority = 'high',
                   amount = 25,
                   contact = ROW('current@example.test', false)::app_types.contact_card
             WHERE id = 1
            """);

        await SetAsOfAsync(conn, beforeAlter!);
        Assert.Equal("old-name:low:10:old@example.test:true", await ScalarAsync<string>(conn, """
            SELECT name || ':' ||
                   priority::text || ':' ||
                   amount::text || ':' ||
                   (contact).email || ':' ||
                   (contact).opted_in::text
            FROM custom_type_column_items
            WHERE id = 1
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("current-name:high:25:current@example.test:false", await ScalarAsync<string>(conn, """
            SELECT name || ':' ||
                   priority::text || ':' ||
                   amount::text || ':' ||
                   (contact).email || ':' ||
                   (contact).opted_in::text
            FROM custom_type_column_items
            WHERE id = 1
            """));
    }

    [Fact]
    public async Task Ddl_NullsNotDistinctUniqueIndexesMirrorAsNonUniqueAndPreserveHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE null_unique_items (
                id    int PRIMARY KEY,
                sku   text,
                name  text NOT NULL
            );
            CREATE UNIQUE INDEX null_unique_items_sku_idx
                ON null_unique_items (sku) NULLS NOT DISTINCT;
            SELECT temporal.enable('null_unique_items', include_indexes => true, combine_interval => interval '0');
            """);

        var mirror = await QuerySingleAsync(conn, """
            SELECT i.indisunique, pg_get_indexdef(m.history_index_oid)
            FROM temporal.mirrored_indexes m
            JOIN pg_index i ON i.indexrelid = m.history_index_oid
            WHERE m.table_oid = 'null_unique_items'::regclass
              AND m.base_index_oid = 'null_unique_items_sku_idx'::regclass
            """, r => (IsUnique: r.GetBoolean(0), Definition: r.GetString(1)));
        Assert.False(mirror.IsUnique);
        Assert.DoesNotContain("NULLS NOT DISTINCT", mirror.Definition, StringComparison.OrdinalIgnoreCase);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO null_unique_items VALUES (1, NULL, 'old-null')");

        var duplicateNull = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "INSERT INTO null_unique_items VALUES (2, NULL, 'duplicate-null')"));
        Assert.Equal("23505", duplicateNull.SqlState);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE null_unique_items SET name = 'current-null' WHERE id = 1");

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("old-null", await ScalarAsync<string>(conn,
            "SELECT name FROM null_unique_items WHERE sku IS NULL"));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("current-null", await ScalarAsync<string>(conn,
            "SELECT name FROM null_unique_items WHERE sku IS NULL"));
    }

    [Fact]
    public async Task Ddl_AlterColumnDefaultsAndNotNullPreserveHistoryReads()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE alter_metadata_items (
                id   int PRIMARY KEY,
                name text NOT NULL,
                note text
            );
            SELECT temporal.enable('alter_metadata_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO alter_metadata_items VALUES (1, 'old-name', 'old-note')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            ALTER TABLE alter_metadata_items
                ALTER COLUMN note SET DEFAULT 'default-note',
                ALTER COLUMN note SET NOT NULL;
            """);

        var columnShape = await QuerySingleAsync(conn, """
            SELECT base_attr.attnotnull,
                   base_def.oid IS NOT NULL,
                   history_attr.attnotnull,
                   history_def.oid IS NOT NULL
            FROM pg_attribute base_attr
            JOIN pg_attribute history_attr
              ON history_attr.attrelid = 'alter_metadata_items__history'::regclass
             AND history_attr.attname = base_attr.attname
            LEFT JOIN pg_attrdef base_def
              ON base_def.adrelid = base_attr.attrelid
             AND base_def.adnum = base_attr.attnum
            LEFT JOIN pg_attrdef history_def
              ON history_def.adrelid = history_attr.attrelid
             AND history_def.adnum = history_attr.attnum
            WHERE base_attr.attrelid = 'alter_metadata_items'::regclass
              AND base_attr.attname = 'note'
            """, r => (
                BaseNotNull: r.GetBoolean(0),
                BaseHasDefault: r.GetBoolean(1),
                HistoryNotNull: r.GetBoolean(2),
                HistoryHasDefault: r.GetBoolean(3)));
        Assert.Equal((true, true, false, false), columnShape);

        var notNull = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync(conn, "INSERT INTO alter_metadata_items VALUES (3, 'bad-null', NULL)"));
        Assert.Equal("23502", notNull.SqlState);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "INSERT INTO alter_metadata_items (id, name) VALUES (2, 'defaulted')");
        await ExecAsync(conn, "UPDATE alter_metadata_items SET name = 'current-name', note = 'current-note' WHERE id = 1");

        Assert.Equal("2:defaulted:default-note", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || note
            FROM alter_metadata_items
            WHERE id = 2
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-name:old-note", await ScalarAsync<string>(conn, """
            SELECT id::text || ':' || name || ':' || note
            FROM alter_metadata_items
            WHERE id = 1
            """));
        Assert.Equal(0L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM alter_metadata_items WHERE id = 2"));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current-name:current-note,2:defaulted:default-note", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || note, ',' ORDER BY id)
            FROM alter_metadata_items
            """));
    }

    [Fact]
    public async Task Ddl_IndexAndConstraintRenamesKeepMirroredIndexMappings()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE rename_index_items (
                id   int PRIMARY KEY,
                code text NOT NULL,
                name text NOT NULL
            );
            SELECT temporal.enable('rename_index_items', include_indexes => true, combine_interval => interval '0');
            CREATE INDEX rename_index_items_name_idx ON rename_index_items (name);
            ALTER TABLE rename_index_items
                ADD CONSTRAINT rename_index_items_code_unique UNIQUE (code);
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO rename_index_items VALUES (1, 'A', 'old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE rename_index_items SET name = 'current' WHERE id = 1");

        var nameMirror = await ScalarAsync<string>(conn, """
            SELECT format('%I.%I', n.nspname, c.relname)
            FROM temporal.mirrored_indexes m
            JOIN pg_class c ON c.oid = m.history_index_oid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE m.base_index_oid = 'rename_index_items_name_idx'::regclass
            """);
        var codeMirror = await ScalarAsync<string>(conn, """
            SELECT format('%I.%I', n.nspname, c.relname)
            FROM pg_constraint con
            JOIN temporal.mirrored_indexes m ON m.base_index_oid = con.conindid
            JOIN pg_class c ON c.oid = m.history_index_oid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE con.conrelid = 'rename_index_items'::regclass
              AND con.conname = 'rename_index_items_code_unique'
            """);

        Assert.NotNull(nameMirror);
        Assert.NotNull(codeMirror);

        await ExecAsync(conn, """
            ALTER INDEX rename_index_items_name_idx RENAME TO rename_index_items_name_renamed_idx;
            ALTER TABLE rename_index_items
                RENAME CONSTRAINT rename_index_items_code_unique TO rename_index_items_code_renamed_key;
            """);

        Assert.Equal("rename_index_items_name_renamed_idx", await ScalarAsync<string>(conn, """
            SELECT base_index_oid::regclass::text
            FROM temporal.mirrored_indexes
            WHERE base_index_oid = 'rename_index_items_name_renamed_idx'::regclass
            """));
        Assert.Equal("rename_index_items_code_renamed_key", await ScalarAsync<string>(conn, """
            SELECT con.conname
            FROM pg_constraint con
            JOIN temporal.mirrored_indexes m ON m.base_index_oid = con.conindid
            WHERE con.conrelid = 'rename_index_items'::regclass
              AND con.conname = 'rename_index_items_code_renamed_key'
            """));
        Assert.True(await ScalarAsync<bool>(conn, $"SELECT to_regclass('{nameMirror}') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(conn, $"SELECT to_regclass('{codeMirror}') IS NOT NULL"));

        await ExecAsync(conn, "DROP INDEX rename_index_items_name_renamed_idx");
        await ExecAsync(conn, "ALTER TABLE rename_index_items DROP CONSTRAINT rename_index_items_code_renamed_key");

        Assert.Null(await ScalarAsync<string>(conn,
            $"SELECT to_regclass('{nameMirror}')::text"));
        Assert.Null(await ScalarAsync<string>(conn,
            $"SELECT to_regclass('{codeMirror}')::text"));
        Assert.Equal("rename_index_items_pkey", await ScalarAsync<string>(conn, """
            SELECT b.relname
            FROM temporal.mirrored_indexes m
            JOIN pg_class b ON b.oid = m.base_index_oid
            WHERE m.table_oid = 'rename_index_items'::regclass
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("old", await ScalarAsync<string>(conn,
            "SELECT name FROM rename_index_items WHERE id = 1"));
    }

    [Fact]
    public async Task Ddl_AddIdentityColumnAfterEnablePropagatesPlainHistoryColumn()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE identity_column_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('identity_column_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO identity_column_items VALUES (1, 'old-one'), (2, 'old-two')");

        await ExecAsync(conn, """
            ALTER TABLE identity_column_items
                ADD COLUMN audit_id bigint GENERATED BY DEFAULT AS IDENTITY;
            """);

        var identityShape = await QuerySingleAsync(conn, """
            SELECT base_attr.attidentity::text,
                   base_attr.attnotnull,
                   history_attr.attidentity::text,
                   history_attr.attnotnull,
                   history_def.oid IS NOT NULL
            FROM pg_attribute base_attr
            JOIN pg_attribute history_attr
              ON history_attr.attrelid = 'identity_column_items__history'::regclass
             AND history_attr.attname = base_attr.attname
            LEFT JOIN pg_attrdef history_def
              ON history_def.adrelid = history_attr.attrelid
             AND history_def.adnum = history_attr.attnum
            WHERE base_attr.attrelid = 'identity_column_items'::regclass
              AND base_attr.attname = 'audit_id'
            """, r => (
                BaseIdentity: r.GetString(0),
                BaseNotNull: r.GetBoolean(1),
                HistoryIdentity: r.GetString(2),
                HistoryNotNull: r.GetBoolean(3),
                HistoryHasDefault: r.GetBoolean(4)));
        Assert.Equal(("d", true, "", false, false), identityShape);

        Assert.Equal(0L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM identity_column_items WHERE audit_id IS NULL"));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, """
            UPDATE identity_column_items SET name = 'current-one' WHERE id = 1;
            DELETE FROM identity_column_items WHERE id = 2;
            INSERT INTO identity_column_items (id, name) VALUES (3, 'current-three');
            """);

        Assert.Equal("1:old-one:alice:,2:old-two:alice:bob", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || changed_by || ':' || coalesce(deleted_by, ''), ',' ORDER BY id)
            FROM identity_column_items__history
            WHERE audit_id IS NOT NULL
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old-one:true,2:old-two:true", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || (audit_id IS NOT NULL)::text, ',' ORDER BY id)
            FROM identity_column_items
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current-one:true,3:current-three:true", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name || ':' || (audit_id IS NOT NULL)::text, ',' ORDER BY id)
            FROM identity_column_items
            """));
    }

    [Fact]
    public async Task Ddl_AddColumnIndexMirrorAndProtectedAlterations_WorkAsDocumented()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('items', include_indexes => true, combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO items VALUES (1, 'original')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var beforeAlter = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "ALTER TABLE items ADD COLUMN tag text");
        await ExecAsync(conn, "ALTER TABLE items ADD COLUMN category text NOT NULL DEFAULT 'general'");
        await ExecAsync(conn, """
            ALTER TABLE items ADD COLUMN summary text
            GENERATED ALWAYS AS (name || ':' || coalesce(tag, 'none')) STORED
            """);
        await ExecAsync(conn, "CREATE INDEX items_tag_idx ON items (tag)");
        await ExecAsync(conn, "CREATE INDEX CONCURRENTLY items_category_concurrent_idx ON items (category)");

        var historyColumnShape = await QueryAsync(conn, """
            SELECT column_name, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'items__history'
              AND column_name IN ('tag', 'category', 'summary')
            ORDER BY column_name
            """, r => (
                Name: r.GetString(0),
                IsNullable: r.GetString(1),
                Default: r.IsDBNull(2) ? null : r.GetString(2)));

        Assert.Equal(
            [
                ("category", "YES", null),
                ("summary", "YES", null),
                ("tag", "YES", null)
            ],
            historyColumnShape);

        Assert.True(await ScalarAsync<bool>(conn, """
            SELECT attgenerated = 's'
            FROM pg_attribute
            WHERE attrelid = 'items'::regclass
              AND attname = 'summary'
            """));
        Assert.False(await ScalarAsync<bool>(conn, """
            SELECT attgenerated = 's'
            FROM pg_attribute
            WHERE attrelid = 'items__history'::regclass
              AND attname = 'summary'
            """));

        var mirroredTagIndexCount = await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'items'::regclass
              AND base_index_oid = 'items_tag_idx'::regclass
            """);
        Assert.Equal(1L, mirroredTagIndexCount);

        var concurrentIndexMirror = await QuerySingleAsync(conn, """
            SELECT b.indisvalid, h.indisvalid
            FROM temporal.mirrored_indexes m
            JOIN pg_index b ON b.indexrelid = m.base_index_oid
            JOIN pg_index h ON h.indexrelid = m.history_index_oid
            WHERE m.table_oid = 'items'::regclass
              AND m.base_index_oid = 'items_category_concurrent_idx'::regclass
            """, r => (BaseValid: r.GetBoolean(0), HistoryValid: r.GetBoolean(1)));
        Assert.Equal((true, true), concurrentIndexMirror);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE items SET name = 'current', tag = 'blue' WHERE id = 1");

        await SetAsOfAsync(conn, beforeAlter!);
        var historic = await QuerySingleAsync(conn,
            "SELECT name, tag IS NULL, category, summary FROM items WHERE id = 1",
            r => (
                Name: r.GetString(0),
                TagIsNull: r.GetBoolean(1),
                Category: r.GetString(2),
                Summary: r.GetString(3)));
        Assert.Equal(("original", true, "general", "original:none"), historic);
        await ExecAsync(conn, "RESET temporal.as_of");

        var current = await QuerySingleAsync(conn,
            "SELECT name, tag, category, summary FROM items WHERE id = 1",
            r => (
                Name: r.GetString(0),
                Tag: r.GetString(1),
                Category: r.GetString(2),
                Summary: r.GetString(3)));
        Assert.Equal(("current", "blue", "general", "current:blue"), current);

        await ExecAsync(conn, """
            CREATE FUNCTION user_noop_items()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RETURN NEW;
            END
            $$;
            CREATE TRIGGER user_noop
            BEFORE UPDATE ON items
            FOR EACH ROW EXECUTE FUNCTION user_noop_items();
            ALTER TABLE items DISABLE TRIGGER user_noop;
            """);
        Assert.Equal("D", await ScalarAsync<string>(conn, """
            SELECT tgenabled::text
            FROM pg_trigger
            WHERE tgrelid = 'items'::regclass
              AND tgname = 'user_noop'
            """));

        await ExecAsync(conn, "ALTER TABLE items ENABLE TRIGGER user_noop");
        Assert.Equal("O", await ScalarAsync<string>(conn, """
            SELECT tgenabled::text
            FROM pg_trigger
            WHERE tgrelid = 'items'::regclass
              AND tgname = 'user_noop'
            """));

        foreach (var sql in new[]
        {
            "ALTER TABLE items DISABLE TRIGGER temporal_history",
            "ALTER TABLE items ENABLE REPLICA TRIGGER temporal_history",
            "DROP TRIGGER temporal_history ON items",
            "ALTER TABLE items__history DISABLE TRIGGER temporal_protect",
            "DROP TRIGGER temporal_protect ON items__history"
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(conn, sql));
            Assert.NotNull(ex);
        }

        var managedTriggers = await ScalarAsync<string>(conn, """
            SELECT string_agg(tgrelid::regclass::text || ':' || tgname || ':' || tgenabled::text, ',' ORDER BY tgrelid::regclass::text, tgname)
            FROM pg_trigger
            WHERE tgname IN ('temporal_history',
                             'temporal_protect',
                             'temporal_protect_truncate',
                             'temporal_stamp',
                             'temporal_truncate')
              AND tgrelid IN ('items'::regclass, 'items__history'::regclass)
            """);
        Assert.Equal(
            "items:temporal_history:A,items:temporal_stamp:A,items:temporal_truncate:A," +
            "items__history:temporal_protect:A,items__history:temporal_protect_truncate:A",
            managedTriggers);

        // Raw DROP COLUMN / ALTER TYPE are blocked (the versions view depends on
        // the column); TRUNCATE is blocked by the managed trigger. A raw RENAME
        // COLUMN is NOT blocked — it is auto-propagated to the history table by
        // the event trigger (covered by SchemaEvolutionTests).
        foreach (var sql in new[]
        {
            "ALTER TABLE items DROP COLUMN name",
            "ALTER TABLE items ALTER COLUMN name TYPE varchar(200)",
            "TRUNCATE items"
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(conn, sql));
            Assert.NotNull(ex);
        }

        // Raw rename now propagates to the history table instead of erroring.
        await ExecAsync(conn, "ALTER TABLE items RENAME COLUMN name TO title");
        Assert.Equal(1L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM   information_schema.columns
            WHERE  table_schema = 'public'
              AND  table_name   = 'items__history'
              AND  column_name  = 'title'
            """));
    }

    [Fact]
    public async Task Ddl_ActiveHistoryAndVersionsViewDropsAreBlocked()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE protected_drop_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('protected_drop_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO protected_drop_items VALUES (1, 'old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE protected_drop_items SET name = 'current' WHERE id = 1");

        foreach (var (sql, message) in new[]
        {
            ("DROP TABLE protected_drop_items__history", "cannot drop"),
            ("DROP TABLE protected_drop_items__history CASCADE", "cannot drop temporal"),
            ("DROP VIEW protected_drop_items__versions", "cannot drop temporal versions view"),
            ("DROP VIEW protected_drop_items__versions CASCADE", "cannot drop temporal versions view")
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(conn, sql));
            Assert.Contains(message, ex.MessageText, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(await ScalarAsync<bool>(conn,
            "SELECT to_regclass('public.protected_drop_items__history') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(conn,
            "SELECT to_regclass('public.protected_drop_items__versions') IS NOT NULL"));
        Assert.Equal(1L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.tracked_tables
            WHERE table_oid = 'protected_drop_items'::regclass
              AND history_table_oid = 'protected_drop_items__history'::regclass
              AND versions_view_oid = 'protected_drop_items__versions'::regclass
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("old", await ScalarAsync<string>(conn,
            "SELECT name FROM protected_drop_items WHERE id = 1"));
    }

    [Fact]
    public async Task Ddl_DropMirroredIndexesKeepsCatalogInSync()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE index_drop_items (
                id   int PRIMARY KEY,
                code text NOT NULL,
                name text NOT NULL
            );
            SELECT temporal.enable('index_drop_items', include_indexes => true, combine_interval => interval '0');
            CREATE INDEX index_drop_items_name_idx ON index_drop_items (name);
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO index_drop_items VALUES (1, 'A', 'old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE index_drop_items SET name = 'current' WHERE id = 1");

        var historyNameIndex = await ScalarAsync<string>(conn, """
            SELECT format('%I.%I', n.nspname, c.relname)
            FROM temporal.mirrored_indexes m
            JOIN pg_class c ON c.oid = m.history_index_oid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE m.base_index_oid = 'index_drop_items_name_idx'::regclass
            """);

        await ExecAsync(conn, "DROP INDEX index_drop_items_name_idx");

        Assert.Null(await ScalarAsync<string>(conn,
            "SELECT to_regclass('index_drop_items_name_idx')::text"));
        Assert.Null(await ScalarAsync<string>(conn,
            $"SELECT to_regclass('{historyNameIndex}')::text"));
        Assert.Equal(0L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'index_drop_items'::regclass
              AND base_index_oid::text = 'index_drop_items_name_idx'
            """));

        await ExecAsync(conn, "CREATE INDEX index_drop_items_code_idx ON index_drop_items (code)");

        var historyCodeIndex = await ScalarAsync<string>(conn, """
            SELECT format('%I.%I', n.nspname, c.relname)
            FROM temporal.mirrored_indexes m
            JOIN pg_class c ON c.oid = m.history_index_oid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE m.base_index_oid = 'index_drop_items_code_idx'::regclass
            """);

        await ExecAsync(conn, $"DROP INDEX {historyCodeIndex}");

        Assert.True(await ScalarAsync<bool>(conn,
            "SELECT to_regclass('index_drop_items_code_idx') IS NOT NULL"));
        Assert.Null(await ScalarAsync<string>(conn,
            $"SELECT to_regclass('{historyCodeIndex}')::text"));
        Assert.Equal(0L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE base_index_oid = 'index_drop_items_code_idx'::regclass
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("old", await ScalarAsync<string>(conn,
            "SELECT name FROM index_drop_items WHERE id = 1"));
    }

    [Fact]
    public async Task Ddl_DropIndexConcurrentlyKeepsCatalogInSync()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE concurrent_index_drop_items (
                id   int PRIMARY KEY,
                code text NOT NULL,
                name text NOT NULL
            );
            SELECT temporal.enable('concurrent_index_drop_items', include_indexes => true, combine_interval => interval '0');
            """);

        await ExecAsync(conn, "CREATE INDEX CONCURRENTLY concurrent_index_drop_items_name_idx ON concurrent_index_drop_items (name)");
        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO concurrent_index_drop_items VALUES (1, 'A', 'old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE concurrent_index_drop_items SET name = 'current' WHERE id = 1");

        var historyNameIndex = await ScalarAsync<string>(conn, """
            SELECT format('%I.%I', n.nspname, c.relname)
            FROM temporal.mirrored_indexes m
            JOIN pg_class c ON c.oid = m.history_index_oid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE m.base_index_oid = 'concurrent_index_drop_items_name_idx'::regclass
            """);

        await ExecAsync(conn, "DROP INDEX CONCURRENTLY concurrent_index_drop_items_name_idx");

        Assert.Null(await ScalarAsync<string>(conn,
            "SELECT to_regclass('concurrent_index_drop_items_name_idx')::text"));
        Assert.Null(await ScalarAsync<string>(conn,
            $"SELECT to_regclass('{historyNameIndex}')::text"));
        Assert.Equal(0L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'concurrent_index_drop_items'::regclass
              AND base_index_oid::text = 'concurrent_index_drop_items_name_idx'
            """));

        await ExecAsync(conn, "CREATE INDEX CONCURRENTLY concurrent_index_drop_items_code_idx ON concurrent_index_drop_items (code)");
        var historyCodeIndex = await ScalarAsync<string>(conn, """
            SELECT format('%I.%I', n.nspname, c.relname)
            FROM temporal.mirrored_indexes m
            JOIN pg_class c ON c.oid = m.history_index_oid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE m.base_index_oid = 'concurrent_index_drop_items_code_idx'::regclass
            """);

        await ExecAsync(conn, $"DROP INDEX CONCURRENTLY {historyCodeIndex}");

        Assert.True(await ScalarAsync<bool>(conn,
            "SELECT to_regclass('concurrent_index_drop_items_code_idx') IS NOT NULL"));
        Assert.Null(await ScalarAsync<string>(conn,
            $"SELECT to_regclass('{historyCodeIndex}')::text"));
        Assert.Equal(0L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE base_index_oid = 'concurrent_index_drop_items_code_idx'::regclass
            """));

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("old", await ScalarAsync<string>(conn,
            "SELECT name FROM concurrent_index_drop_items WHERE id = 1"));
    }

    [Fact]
    public async Task Ddl_DropTrackedTableCleansCatalogAndReleasesHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE TABLE drop_lifecycle_items (
                id    int PRIMARY KEY,
                code  text NOT NULL UNIQUE,
                name  text NOT NULL
            );
            CREATE INDEX drop_lifecycle_items_name_idx ON drop_lifecycle_items (name);
            SELECT temporal.enable('drop_lifecycle_items', include_indexes => true, combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO drop_lifecycle_items VALUES (1, 'A', 'old')");
        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE drop_lifecycle_items SET name = 'current' WHERE id = 1");

        Assert.Equal(1L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM drop_lifecycle_items__history WHERE id = 1"));
        Assert.True(await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'drop_lifecycle_items'::regclass
            """) > 0);

        await ExecAsync(conn, "DROP TABLE drop_lifecycle_items CASCADE");

        Assert.False(await ScalarAsync<bool>(conn,
            "SELECT to_regclass('public.drop_lifecycle_items') IS NOT NULL"));
        Assert.False(await ScalarAsync<bool>(conn,
            "SELECT to_regclass('public.drop_lifecycle_items__versions') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(conn,
            "SELECT to_regclass('public.drop_lifecycle_items__history') IS NOT NULL"));
        Assert.Equal(0L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.tracked_tables
            WHERE history_table_oid = 'drop_lifecycle_items__history'::regclass
            """));
        Assert.Equal(0L, await ScalarAsync<long>(conn, """
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE history_index_oid IN (
                SELECT indexrelid
                FROM pg_index
                WHERE indrelid = 'drop_lifecycle_items__history'::regclass
            )
            """));

        await ExecAsync(conn, "DROP TABLE drop_lifecycle_items__history");
        Assert.False(await ScalarAsync<bool>(conn,
            "SELECT to_regclass('public.drop_lifecycle_items__history') IS NOT NULL"));
    }

    [Fact]
    public async Task Ddl_DropSchemaCascadeCleansCatalogForTrackedRelations()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE SCHEMA schema_drop_case;
            CREATE TABLE schema_drop_case.schema_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE INDEX schema_items_name_idx ON schema_drop_case.schema_items (name);
            SELECT temporal.enable('schema_drop_case.schema_items', include_indexes => true, combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO schema_drop_case.schema_items VALUES (1, 'old')");
        await ExecAsync(conn, "SET temporal.user_id = 'bob'");
        await ExecAsync(conn, "UPDATE schema_drop_case.schema_items SET name = 'current' WHERE id = 1");

        Assert.Equal("old", await ScalarAsync<string>(conn, """
            SELECT name
            FROM schema_drop_case.schema_items__history
            WHERE id = 1
            """));

        var tracking = await QuerySingleAsync(conn, """
            SELECT table_oid::oid::bigint, history_table_oid::oid::bigint, versions_view_oid::oid::bigint,
                   table_oid::regclass::text, history_table_oid::regclass::text, versions_view_oid::regclass::text
            FROM temporal.tracked_tables
            WHERE table_oid = 'schema_drop_case.schema_items'::regclass
            """, r => (
                TableOid: r.GetInt64(0),
                HistoryOid: r.GetInt64(1),
                VersionsOid: r.GetInt64(2),
                TableName: r.GetString(3),
                HistoryName: r.GetString(4),
                VersionsName: r.GetString(5)));

        Assert.Equal(
            ("schema_drop_case.schema_items", "schema_drop_case.schema_items__history", "schema_drop_case.schema_items__versions"),
            (tracking.TableName, tracking.HistoryName, tracking.VersionsName));
        Assert.True(await ScalarAsync<long>(conn, $"""
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE table_oid::oid = {tracking.TableOid}::oid
            """) > 0);

        await ExecAsync(conn, "DROP SCHEMA schema_drop_case CASCADE");

        Assert.True(await ScalarAsync<bool>(conn,
            "SELECT to_regnamespace('schema_drop_case') IS NULL"));
        Assert.Equal(0L, await ScalarAsync<long>(conn, $"""
            SELECT count(*)
            FROM temporal.tracked_tables
            WHERE table_oid::oid = {tracking.TableOid}::oid
               OR history_table_oid::oid = {tracking.HistoryOid}::oid
               OR versions_view_oid::oid = {tracking.VersionsOid}::oid
            """));
        Assert.Equal(0L, await ScalarAsync<long>(conn, $"""
            SELECT count(*)
            FROM temporal.mirrored_indexes
            WHERE table_oid::oid = {tracking.TableOid}::oid
            """));
    }

    [Fact]
    public async Task Ddl_RenameAndSetSchemaKeepTrackingByOid()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            CREATE SCHEMA moved_schema;
            CREATE TABLE rename_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            SELECT temporal.enable('rename_items', combine_interval => interval '0');
            """);

        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, "INSERT INTO rename_items VALUES (1, 'old')");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var asOf = await ScalarAsync<string>(conn, "SELECT clock_timestamp()::text");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await ExecAsync(conn, """
            SET temporal.user_id = 'bob';
            ALTER TABLE rename_items RENAME TO renamed_items;
            ALTER TABLE renamed_items SET SCHEMA moved_schema;
            UPDATE moved_schema.renamed_items SET name = 'current' WHERE id = 1;
            """);

        var tracking = await QuerySingleAsync(conn, """
            SELECT table_oid::regclass::text, history_table_oid::regclass::text, versions_view_oid::regclass::text
            FROM temporal.tracked_tables
            WHERE table_oid = 'moved_schema.renamed_items'::regclass
            """, r => (
                Table: r.GetString(0),
                History: r.GetString(1),
                Versions: r.GetString(2)));
        Assert.Equal(("moved_schema.renamed_items", "rename_items__history", "rename_items__versions"), tracking);

        await SetAsOfAsync(conn, asOf!);
        Assert.Equal("1:old", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM moved_schema.renamed_items
            """));

        await ExecAsync(conn, "RESET temporal.as_of");
        Assert.Equal("1:current", await ScalarAsync<string>(conn, """
            SELECT string_agg(id::text || ':' || name, ',' ORDER BY id)
            FROM moved_schema.renamed_items
            """));

        Assert.Equal("old,current", await ScalarAsync<string>(conn, """
            SELECT string_agg(name, ',' ORDER BY valid_from)
            FROM rename_items__versions
            WHERE id = 1
            """));

        Assert.Equal("temporal_history:A,temporal_stamp:A,temporal_truncate:A",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(tgname || ':' || tgenabled::text, ',' ORDER BY tgname)
                FROM pg_trigger
                WHERE tgrelid = 'moved_schema.renamed_items'::regclass
                  AND tgname LIKE 'temporal_%'
                """));
        Assert.Equal("temporal_protect:A,temporal_protect_truncate:A",
            await ScalarAsync<string>(conn, """
                SELECT string_agg(tgname || ':' || tgenabled::text, ',' ORDER BY tgname)
                FROM pg_trigger
                WHERE tgrelid = 'rename_items__history'::regclass
                  AND tgname LIKE 'temporal_%'
                """));
    }

    private const string ComplexReadQuery = """
        WITH open_ticket_counts AS MATERIALIZED (
            SELECT user_id, count(*) AS open_count, max(priority) AS max_priority
            FROM tickets
            WHERE state = 'open'
            GROUP BY user_id
        ),
        ranked AS (
            SELECT u.id,
                   u.name,
                   r.name AS role_name,
                   d.name AS department_name,
                   otc.open_count,
                   otc.max_priority,
                   row_number() OVER (PARTITION BY d.id ORDER BY otc.max_priority DESC, u.id) AS user_rank,
                   EXISTS (
                       SELECT 1
                       FROM tickets tx
                       WHERE tx.user_id = u.id
                         AND tx.title = 'alpha'
                         AND tx.state = 'open'
                   ) AS has_alpha,
                   (
                       SELECT string_agg(t.title, ',' ORDER BY t.id)
                       FROM tickets t
                       WHERE t.user_id = u.id
                         AND t.state = 'open'
                   ) AS titles,
                   lat.all_ticket_count
            FROM users u
            INNER JOIN roles r ON r.id = u.role_id
            INNER JOIN departments d ON d.id = r.department_id
            LEFT JOIN open_ticket_counts otc ON otc.user_id = u.id
            JOIN LATERAL (
                SELECT count(*) AS all_ticket_count
                FROM tickets t_all
                WHERE t_all.user_id = u.id
            ) lat ON true
            WHERE u.active
              AND u.id IN (
                  SELECT user_id
                  FROM tickets
                  WHERE priority >= 5
              )
        )
        SELECT name,
               role_name,
               department_name,
               open_count,
               max_priority,
               user_rank,
               has_alpha,
               titles,
               all_ticket_count
        FROM ranked
        WHERE id = 1
        """;

    private static async Task CreateComplexReadSchemaAsync(NpgsqlConnection conn)
    {
        await ExecAsync(conn, """
            CREATE TABLE departments (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            CREATE TABLE roles (
                id            int PRIMARY KEY,
                department_id int NOT NULL,
                name          text NOT NULL
            );
            CREATE TABLE users (
                id      int PRIMARY KEY,
                role_id int NOT NULL,
                name    text NOT NULL,
                active  boolean NOT NULL
            );
            CREATE TABLE tickets (
                id       int PRIMARY KEY,
                user_id  int NOT NULL,
                title    text NOT NULL,
                state    text NOT NULL,
                priority int NOT NULL
            );
            SELECT temporal.enable('departments', combine_interval => interval '0');
            SELECT temporal.enable('roles', combine_interval => interval '0');
            SELECT temporal.enable('users', combine_interval => interval '0');
            SELECT temporal.enable('tickets', combine_interval => interval '0');
            """);
    }

    private static async Task SetAsOfAsync(NpgsqlConnection conn, string timestampText)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('temporal.as_of', $1, false)";
        cmd.Parameters.AddWithValue(timestampText);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task SetLocalAsOfAsync(NpgsqlConnection conn, string timestampText)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('temporal.as_of', $1, true)";
        cmd.Parameters.AddWithValue(timestampText);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> CopyTextAsync(NpgsqlConnection conn, string sql)
    {
        using var reader = await conn.BeginTextExportAsync(sql, TestContext.Current.CancellationToken);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
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
        NpgsqlConnection conn, string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
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
