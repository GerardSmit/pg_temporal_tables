using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

public sealed class EnableDisableTests
{
    private readonly PgTemporalFixture _pg;

    public EnableDisableTests(PgTemporalFixture pg) => _pg = pg;

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
    // 1. Enable creates history table, view, and triggers
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_CreatesHistoryTableViewAndTriggers()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("SELECT temporal.enable('t')");

        // History table must exist
        var histExists = await db.ScalarAsync<bool>(
            "SELECT to_regclass('public.t__history') IS NOT NULL");
        Assert.True(histExists, "history table should exist");

        // History table must have valid_from, changed_by, valid_to columns
        var histCols = await db.QueryAsync(
            """
            SELECT column_name
            FROM   information_schema.columns
            WHERE  table_schema = 'public'
              AND  table_name   = 't__history'
            """,
            r => r.GetString(0));

        Assert.Contains("valid_from", histCols);
        Assert.Contains("changed_by", histCols);
        Assert.Contains("valid_to",   histCols);

        // View must exist and contain is_current
        var viewExists = await db.ScalarAsync<bool>(
            "SELECT to_regclass('public.t__versions') IS NOT NULL");
        Assert.True(viewExists, "versions view should exist");

        var viewCols = await db.QueryAsync(
            """
            SELECT column_name
            FROM   information_schema.columns
            WHERE  table_schema = 'public'
              AND  table_name   = 't__versions'
            """,
            r => r.GetString(0));
        Assert.Contains("is_current", viewCols);

        // 3 non-internal triggers on base table
        var baseTriggers = await db.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM   pg_trigger t
            JOIN   pg_class   c ON c.oid = t.tgrelid
            WHERE  c.relname     = 't'
              AND  c.relnamespace = 'public'::regnamespace
              AND  NOT t.tgisinternal
            """);
        Assert.Equal(3L, baseTriggers);

        // 2 non-internal triggers on history table
        var histTriggers = await db.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM   pg_trigger t
            JOIN   pg_class   c ON c.oid = t.tgrelid
            WHERE  c.relname     = 't__history'
              AND  c.relnamespace = 'public'::regnamespace
              AND  NOT t.tgisinternal
            """);
        Assert.Equal(2L, histTriggers);

        // Catalog row present
        var catalogCount = await db.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM   temporal.tracked_tables
            WHERE  table_oid = 'public.t'::regclass
            """);
        Assert.Equal(1L, catalogCount);
    }

    // ---------------------------------------------------------------------------
    // 2. Enable requires a primary key
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_RequiresPrimaryKey()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync("CREATE TABLE t (id int, name text)");

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('t')"));

        Assert.Contains("PRIMARY KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 3. Enable rejects already-tracked table
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_RejectsAlreadyTracked()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("SELECT temporal.enable('t')");

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('t')"));

        Assert.Contains("already tracked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 4. Enable rejects excluded PK or managed columns
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_RejectsExcludedPkColumn()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync(
                "SELECT temporal.enable('t', excluded_columns => ARRAY['id']::name[])"));

        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Enable_RejectsExcludedManagedColumn()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync(
                "SELECT temporal.enable('t', excluded_columns => ARRAY['valid_from']::name[])"));

        Assert.NotNull(ex);
    }

    // ---------------------------------------------------------------------------
    // 5. Enable rejects month-based combine interval
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_RejectsMonthInterval()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync(
                "SELECT temporal.enable('t', combine_interval => interval '1 month')"));

        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Enable_AllowsPartitionedRoot_RejectsPartitionLeavesAndInheritance()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();

        await db.ExecAsync("""
            CREATE TABLE partition_parent (
                id     int NOT NULL,
                bucket int NOT NULL,
                name   text,
                PRIMARY KEY (id, bucket)
            ) PARTITION BY RANGE (bucket);
            CREATE TABLE partition_child
                PARTITION OF partition_parent FOR VALUES FROM (0) TO (100);
            """);

        // A declarative partitioned root is supported.
        await db.ExecAsync("SELECT temporal.enable('partition_parent')");
        Assert.Equal(1L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM temporal.tracked_tables WHERE table_oid = 'partition_parent'::regclass"));
        Assert.True(await db.ScalarAsync<bool>(
            "SELECT to_regclass('public.partition_parent__history') IS NOT NULL"));

        // A partition leaf cannot be tracked directly — track the root.
        var leaf = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('partition_child')"));
        Assert.Contains("partition", leaf.Message, StringComparison.OrdinalIgnoreCase);

        // Legacy table inheritance remains unsupported.
        await db.ExecAsync("""
            CREATE TABLE inherit_parent (
                id   int PRIMARY KEY,
                name text
            );
            CREATE TABLE inherit_child (
                extra text
            ) INHERITS (inherit_parent);
            """);

        var inherited = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('inherit_parent')"));
        Assert.Contains("inheritance", inherited.Message, StringComparison.OrdinalIgnoreCase);

        var inheritChild = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('inherit_child')"));
        Assert.Contains("inheritance", inheritChild.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enable_RejectsTimescaleHypertablesAndTrackedHypertableWritesFail()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();

        await db.ExecAsync("""
            CREATE EXTENSION timescaledb;

            CREATE TABLE timescale_metrics (
                recorded_at timestamptz NOT NULL,
                device_id   int NOT NULL,
                reading     double precision NOT NULL,
                PRIMARY KEY (recorded_at, device_id)
            );
            SELECT create_hypertable('timescale_metrics', 'recorded_at');
            INSERT INTO timescale_metrics VALUES ('2026-06-15 10:00Z', 1, 42.5);
            """);

        Assert.True(await db.ScalarAsync<bool>("""
            SELECT EXISTS (
                SELECT 1
                FROM pg_inherits
                WHERE inhparent = 'timescale_metrics'::regclass
            )
            """));

        var hypertable = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('timescale_metrics')"));
        Assert.Contains("inheritance", hypertable.Message, StringComparison.OrdinalIgnoreCase);

        await db.ExecAsync("""
            CREATE TABLE tracked_timescale_candidates (
                recorded_at timestamptz NOT NULL,
                device_id   int NOT NULL,
                reading     double precision NOT NULL,
                PRIMARY KEY (recorded_at, device_id)
            );
            SELECT temporal.enable('tracked_timescale_candidates', combine_interval => interval '0');
            """);

        await db.ExecAsync("SELECT create_hypertable('tracked_timescale_candidates', 'recorded_at')");

        Assert.True(await db.ScalarAsync<bool>("""
            SELECT EXISTS (
                SELECT 1
                FROM _timescaledb_catalog.hypertable
                WHERE schema_name = 'public'
                  AND table_name = 'tracked_timescale_candidates'
            )
            """));
        Assert.Equal(1L, await db.ScalarAsync<long>("""
            SELECT count(*)
            FROM temporal.tracked_tables
            WHERE table_oid = 'tracked_timescale_candidates'::regclass
            """));

        var writeAfterConversion = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("INSERT INTO tracked_timescale_candidates VALUES ('2026-06-15 11:00Z', 1, 7.5)"));
        Assert.Contains("inheritance", writeAfterConversion.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enable_RejectsTemporaryTables()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TEMP TABLE temp_items (
                    id   int PRIMARY KEY,
                    name text
                );
                """;
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT temporal.enable('temp_items')";
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            Assert.Contains("temporary", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Enable_RejectsViewsMaterializedViewsAndForeignTables()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();

        await db.ExecAsync("""
            CREATE EXTENSION postgres_fdw;

            CREATE TABLE source_items (
                id   int PRIMARY KEY,
                name text NOT NULL
            );
            INSERT INTO source_items VALUES (1, 'one');

            CREATE VIEW source_view AS
            SELECT id, name FROM source_items;

            CREATE MATERIALIZED VIEW source_matview AS
            SELECT id, name FROM source_items;

            CREATE SERVER enable_rejects_relation_kind_server
                FOREIGN DATA WRAPPER postgres_fdw
                OPTIONS (host '127.0.0.1', port '5432', dbname 'postgres');
            CREATE USER MAPPING FOR CURRENT_USER
                SERVER enable_rejects_relation_kind_server
                OPTIONS (user 'postgres', password 'postgres');
            CREATE FOREIGN TABLE source_foreign (
                id   int,
                name text
            )
            SERVER enable_rejects_relation_kind_server
            OPTIONS (schema_name 'public', table_name 'source_items');
            """);

        foreach (var relationName in new[] { "source_view", "source_matview", "source_foreign" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => db.ExecAsync($"SELECT temporal.enable('{relationName}')"));
            Assert.Contains("ordinary or partitioned table", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------------------
    // 6. Enable with include_indexes mirrors unique index as non-unique
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_WithIncludeIndexes_MirrorsUniqueAsNonUnique()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("CREATE UNIQUE INDEX t_email_idx ON t (email)");
        await db.ExecAsync(
            "SELECT temporal.enable('t', include_indexes => true)");

        // mirrored_indexes must have 2 rows (pkey + email idx)
        var mirroredCount = await db.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM   temporal.mirrored_indexes
            WHERE  table_oid = 'public.t'::regclass
            """);
        Assert.Equal(2L, mirroredCount);

        // The history email index must exist and be non-unique
        var histEmailIdxUnique = await db.ScalarAsync<bool>(
            """
            SELECT ix.indisunique
            FROM   pg_index ix
            JOIN   pg_class c ON c.oid = ix.indrelid
            WHERE  c.relname     = 't__history'
              AND  c.relnamespace = 'public'::regnamespace
              AND  EXISTS (
                       SELECT 1
                       FROM   pg_attribute a
                       WHERE  a.attrelid = c.oid
                         AND  a.attname  = 'email'
                         AND  a.attnum   = ANY(ix.indkey)
                   )
            """);
        Assert.False(histEmailIdxUnique, "mirrored unique index should be non-unique on history table");
    }

    // ---------------------------------------------------------------------------
    // 7. Disable removes triggers and view but keeps history table
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Disable_RemovesTriggersAndViewKeepsHistory()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("SELECT temporal.enable('t')");
        await db.ExecAsync("INSERT INTO t VALUES (1, 'Alice', 'alice@example.com', now())");
        await db.ExecAsync("SELECT temporal.disable('t')");

        // No non-internal triggers on base table
        var baseTriggers = await db.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM   pg_trigger trg
            JOIN   pg_class   c ON c.oid = trg.tgrelid
            WHERE  c.relname     = 't'
              AND  c.relnamespace = 'public'::regnamespace
              AND  NOT trg.tgisinternal
            """);
        Assert.Equal(0L, baseTriggers);

        // View must be gone
        var viewExists = await db.ScalarAsync<bool>(
            "SELECT to_regclass('public.t__versions') IS NOT NULL");
        Assert.False(viewExists, "versions view should be gone after disable");

        // History table must still exist
        var histExists = await db.ScalarAsync<bool>(
            "SELECT to_regclass('public.t__history') IS NOT NULL");
        Assert.True(histExists, "history table should still exist after disable");

        // Catalog must be empty for this table
        var catalogCount = await db.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM   temporal.tracked_tables
            WHERE  table_oid = 'public.t'::regclass
            """);
        Assert.Equal(0L, catalogCount);
    }

    // ---------------------------------------------------------------------------
    // 8. Disable on untracked table throws
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Disable_NotTracked_Throws()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.disable('t')"));

        Assert.NotNull(ex);
    }

    // ---------------------------------------------------------------------------
    // 9. Direct INSERT into history table is blocked
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HistoryTable_DirectInsert_Blocked()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("SELECT temporal.enable('t')");

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync(
                """
                INSERT INTO t__history (id, name, email, updated_at, valid_from, valid_to)
                VALUES (99, 'Bad', 'bad@example.com', now(), now(), 'infinity')
                """));

        Assert.Contains("not allowed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HistoryTable_DirectUpdateDeleteAndTruncate_Blocked()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("SELECT temporal.enable('t', combine_interval => interval '0')");

        await db.ExecAsync("INSERT INTO t VALUES (1, 'Alice', 'a@example.com', now())");
        await db.ExecAsync("UPDATE t SET name = 'Bob' WHERE id = 1");

        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM t__history WHERE id = 1"));

        foreach (var sql in new[]
        {
            "UPDATE t__history SET name = 'Tampered' WHERE id = 1",
            "DELETE FROM t__history WHERE id = 1",
            "TRUNCATE t__history"
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync(sql));
            Assert.Equal("42501", ex.SqlState);
            Assert.Contains("direct modification", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("Alice", await db.ScalarAsync<string>(
            "SELECT name FROM t__history WHERE id = 1"));
    }

    [Fact]
    public async Task SetIncludeIndexes_True_BackfillsAndAutoMirrorsFutureIndexes()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("CREATE INDEX t_name_idx ON t (name)");
        await db.ExecAsync("SELECT temporal.enable('t')");

        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM temporal.mirrored_indexes WHERE table_oid = 'public.t'::regclass"));

        await db.ExecAsync("CREATE INDEX t_email_after_enable_idx ON t (email)");
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM temporal.mirrored_indexes WHERE table_oid = 'public.t'::regclass"));

        await db.ExecAsync("SELECT temporal.set_include_indexes('t', true)");

        Assert.Equal("t_email_after_enable_idx,t_name_idx,t_pkey", await db.ScalarAsync<string>(
            """
            SELECT string_agg(base_index_oid::regclass::text, ',' ORDER BY base_index_oid::regclass::text)
            FROM temporal.mirrored_indexes
            WHERE table_oid = 'public.t'::regclass
            """));

        await db.ExecAsync("CREATE INDEX t_updated_at_after_include_idx ON t (updated_at)");

        Assert.Equal("t_email_after_enable_idx,t_name_idx,t_pkey,t_updated_at_after_include_idx",
            await db.ScalarAsync<string>(
                """
                SELECT string_agg(base_index_oid::regclass::text, ',' ORDER BY base_index_oid::regclass::text)
                FROM temporal.mirrored_indexes
                WHERE table_oid = 'public.t'::regclass
                """));
    }

    // ---------------------------------------------------------------------------
    // 10. set_include_indexes(false) drops mirrored indexes (keeps managed ones)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SetIncludeIndexes_False_DropsMirrors()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("CREATE UNIQUE INDEX t_email_idx ON t (email)");
        await db.ExecAsync(
            "SELECT temporal.enable('t', include_indexes => true)");

        // Confirm mirrored_indexes is non-empty before the change
        var beforeCount = await db.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM   temporal.mirrored_indexes
            WHERE  table_oid = 'public.t'::regclass
            """);
        Assert.Equal(2L, beforeCount);

        await db.ExecAsync("SELECT temporal.set_include_indexes('t', false)");

        // mirrored_indexes must now be empty for this table
        var afterCount = await db.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM   temporal.mirrored_indexes
            WHERE  table_oid = 'public.t'::regclass
            """);
        Assert.Equal(0L, afterCount);

        // History email index must be gone
        var histEmailIdxExists = await db.ScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM   pg_index ix
                JOIN   pg_class c ON c.oid = ix.indrelid
                WHERE  c.relname     = 't__history'
                  AND  c.relnamespace = 'public'::regnamespace
                  AND  EXISTS (
                           SELECT 1
                           FROM   pg_attribute a
                           WHERE  a.attrelid = c.oid
                             AND  a.attname  = 'email'
                             AND  a.attnum   = ANY(ix.indkey)
                       )
            )
            """);
        Assert.False(histEmailIdxExists, "mirrored email index should be dropped");

        // Managed _pk_idx and _period_idx must still exist
        var pkIdxExists = await db.ScalarAsync<bool>(
            "SELECT to_regclass('public.t__history_pk_idx') IS NOT NULL");
        Assert.True(pkIdxExists, "managed _pk_idx should still exist");

        var periodIdxExists = await db.ScalarAsync<bool>(
            "SELECT to_regclass('public.t__history_period_idx') IS NOT NULL");
        Assert.True(periodIdxExists, "managed _period_idx should still exist");
    }

    // ---------------------------------------------------------------------------
    // 11. Enable rejects an excluded column that does not exist
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_RejectsExcludedNonexistentColumn()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync(
                "SELECT temporal.enable('t', excluded_columns => ARRAY['nope']::name[])"));

        Assert.Contains("does not exist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 12. Enable rejects a base table that already has a managed history column
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_RejectsManagedColumnNameConflict()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync("""
            CREATE TABLE t (
                id       int PRIMARY KEY,
                name     text,
                valid_to timestamptz
            )
            """);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('t')"));

        Assert.Contains("managed history column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 13. Enable accepts pre-existing, correctly typed valid_from / changed_by
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_AcceptsPreexistingManagedColumns()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync("""
            CREATE TABLE t (
                id         int PRIMARY KEY,
                name       text,
                valid_from timestamptz,
                changed_by text
            )
            """);

        // Must not throw.
        await db.ExecAsync("SELECT temporal.enable('t', combine_interval => interval '0')");

        Assert.Equal(1L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM temporal.tracked_tables WHERE table_oid = 'public.t'::regclass"));

        // The pre-existing managed columns are stamped on write.
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await ExecOnConnAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecOnConnAsync(conn, "INSERT INTO t (id, name) VALUES (1, 'v0')");

        Assert.Equal("alice", await ScalarOnConnAsync<string>(conn, "SELECT changed_by FROM t WHERE id = 1"));
        Assert.NotNull(await ScalarOnConnAsync<DateTime?>(conn, "SELECT valid_from FROM t WHERE id = 1"));
    }

    // ---------------------------------------------------------------------------
    // 14. Enable rejects a pre-existing valid_from of the wrong type
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_RejectsPreexistingValidFromWrongType()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync("""
            CREATE TABLE t (
                id         int PRIMARY KEY,
                name       text,
                valid_from text
            )
            """);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('t')"));

        Assert.Contains("valid_from must be timestamptz", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 15. Enable rejects being pointed at an existing history table
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_RejectsEnablingHistoryTableDirectly()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("SELECT temporal.enable('t')");

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.enable('t__history')"));

        Assert.Contains("history table", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 16. set_excluded_columns error paths
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SetExcludedColumns_NotTracked_Throws()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.set_excluded_columns('t', ARRAY['name']::name[])"));

        Assert.Contains("is not tracked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetExcludedColumns_RejectsManagedOrPkColumn()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("SELECT temporal.enable('t')");

        foreach (var col in new[] { "id", "valid_from", "changed_by" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => db.ExecAsync($"SELECT temporal.set_excluded_columns('t', ARRAY['{col}']::name[])"));
            Assert.Contains("cannot be excluded", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SetExcludedColumns_RejectsNonexistentColumn()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("SELECT temporal.enable('t')");

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.set_excluded_columns('t', ARRAY['nope']::name[])"));

        Assert.Contains("does not exist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 17. set_combine_interval error paths
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SetCombineInterval_NotTracked_Throws()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.set_combine_interval('t', interval '5 seconds')"));

        Assert.Contains("is not tracked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetCombineInterval_RejectsMonthInterval()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("SELECT temporal.enable('t')");

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.set_combine_interval('t', interval '1 month')"));

        Assert.NotNull(ex);
    }

    // ---------------------------------------------------------------------------
    // 18. set_include_indexes on an untracked table
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SetIncludeIndexes_NotTracked_Throws()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.ExecAsync("SELECT temporal.set_include_indexes('t', true)"));

        Assert.Contains("is not tracked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // 19. Managed triggers cannot be dropped while tracking is active
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DropManagedTrigger_Blocked()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await db.ExecAsync(CreateTestTable);
        await db.ExecAsync("SELECT temporal.enable('t')");

        // Base-table triggers and a history-table protection trigger are all managed.
        var cases = new (string Sql, string Relation)[]
        {
            ("DROP TRIGGER temporal_stamp ON t",                "t"),
            ("DROP TRIGGER temporal_history ON t",              "t"),
            ("DROP TRIGGER temporal_truncate ON t",             "t"),
            ("DROP TRIGGER temporal_protect ON t__history",     "t__history"),
        };

        foreach (var (sql, _) in cases)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync(sql));
            Assert.Contains("cannot drop temporal trigger", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // All managed triggers must survive the failed drop attempts.
        Assert.Equal(3L, await db.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM   pg_trigger trg
            JOIN   pg_class   c ON c.oid = trg.tgrelid
            WHERE  c.relname     = 't'
              AND  c.relnamespace = 'public'::regnamespace
              AND  NOT trg.tgisinternal
            """));
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
