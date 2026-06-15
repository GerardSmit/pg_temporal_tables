using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies that system-time versioning works correctly on top of tables that carry
/// a PostgreSQL 18+ native APPLICATION-TIME period (a range column with a temporal
/// PRIMARY KEY using WITHOUT OVERLAPS).  The combination is "bitemporal": the
/// extension tracks system-time history while the range column holds application time.
/// </summary>
public sealed class BitemporalTests
{
    private readonly PgTemporalFixture _pg;

    public BitemporalTests(PgTemporalFixture pg) => _pg = pg;

    // ---------------------------------------------------------------------------
    // Shared SQL
    // ---------------------------------------------------------------------------

    private const string CreatePriceTable = """
        CREATE TABLE price (
            sku   text,
            valid daterange,
            cents int,
            PRIMARY KEY (sku, valid WITHOUT OVERLAPS)
        )
        """;

    private const string EnablePrice =
        "SELECT temporal.enable('price', combine_interval => interval '0')";

    // ---------------------------------------------------------------------------
    // 1. Enable system-versioning on a table that has an application-time period PK
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Enable_OnTableWithApplicationPeriodPk_Tracks()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, "CREATE EXTENSION IF NOT EXISTS btree_gist");
        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, CreatePriceTable);
        await ExecAsync(conn, EnablePrice);

        // Extension must have registered the table
        var tracked = await ScalarAsync<long>(conn,
            "SELECT count(*) FROM temporal.tracked_tables WHERE table_oid = 'price'::regclass");
        Assert.Equal(1L, tracked);

        // History table must exist
        var histExists = await ScalarAsync<bool>(conn,
            "SELECT to_regclass('public.price__history') IS NOT NULL");
        Assert.True(histExists);

        // Mutate data and verify history
        await ExecAsync(conn, "INSERT INTO price VALUES ('x', '[2026-01-01,2026-06-01)', 100)");
        await ExecAsync(conn, "UPDATE price SET cents = 120 WHERE sku = 'x' AND valid = '[2026-01-01,2026-06-01)'");

        var histCount = await ScalarAsync<long>(conn,
            "SELECT count(*) FROM price__history");
        Assert.Equal(1L, histCount); // the superseded version of the row

        var currentCents = await ScalarAsync<int>(conn,
            "SELECT cents FROM price WHERE sku = 'x'");
        Assert.Equal(120, currentCents);
    }

    // ---------------------------------------------------------------------------
    // 2. AS OF time-travel returns the row as it existed at an older system instant
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Bitemporal_SystemTimeAsOf_ReturnsOldRow()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, "CREATE EXTENSION IF NOT EXISTS btree_gist");
        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, CreatePriceTable);
        await ExecAsync(conn, EnablePrice);

        await ExecAsync(conn, "INSERT INTO price VALUES ('x', '[2026-01-01,2026-06-01)', 100)");
        await ExecAsync(conn, "UPDATE price SET cents = 120 WHERE sku = 'x' AND valid = '[2026-01-01,2026-06-01)'");

        // The min(valid_from) in the history table is the exact instant the row was inserted
        var t0 = await ScalarAsync<string>(conn,
            "SELECT min(valid_from)::text FROM price__history");

        Assert.NotNull(t0);

        // AS OF that instant the row still had cents = 100
        var oldCents = await ScalarAsync<int>(conn,
            $"SELECT cents FROM price WHERE temporal.as_of('{t0}'::timestamptz) AND sku = 'x'");
        Assert.Equal(100, oldCents);

        // Present-day query (no marker) sees the updated value
        var newCents = await ScalarAsync<int>(conn,
            "SELECT cents FROM price WHERE sku = 'x'");
        Assert.Equal(120, newCents);
    }

    // ---------------------------------------------------------------------------
    // 3. FOR PORTION OF (PG 19+) creates history rows and splits the live row
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Bitemporal_ForPortionOf_VersionsCorrectly()
    {
        Assert.SkipWhen(int.Parse(Helpers.PgMajor) < 19, "FOR PORTION OF requires PostgreSQL 19+");

        await using var db = await _pg.CreateFreshDbWithExtensionAsync();
        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ExecAsync(conn, "CREATE EXTENSION IF NOT EXISTS btree_gist");
        await ExecAsync(conn, "SET temporal.user_id = 'alice'");
        await ExecAsync(conn, CreatePriceTable);
        await ExecAsync(conn, EnablePrice);

        // Insert a row spanning a full year at cents = 100
        await ExecAsync(conn, "INSERT INTO price VALUES ('x', '[2026-01-01,2027-01-01)', 100)");

        // Update only the second half of the application-time range to cents = 150
        await ExecAsync(conn,
            "UPDATE price FOR PORTION OF valid FROM '2026-06-01' TO '2027-01-01' SET cents = 150 WHERE sku = 'x'");

        // The old (unsplit) version must have been recorded in history
        var histCount = await ScalarAsync<long>(conn,
            "SELECT count(*) FROM price__history");
        Assert.True(histCount >= 1,
            $"Expected at least 1 history row after FOR PORTION OF, got {histCount}");

        // The live table must now contain the split rows
        var liveCount = await ScalarAsync<long>(conn,
            "SELECT count(*) FROM price WHERE sku = 'x'");
        Assert.True(liveCount >= 2,
            $"Expected at least 2 live rows after FOR PORTION OF split, got {liveCount}");
    }

    // ---------------------------------------------------------------------------
    // Private helpers — single-connection, GUC-safe (mirrors ChangeViewerTests)
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
}
