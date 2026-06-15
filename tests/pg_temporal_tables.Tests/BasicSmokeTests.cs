using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

public sealed class BasicSmokeTests
{
    private readonly PgTemporalFixture _pg;

    public BasicSmokeTests(PgTemporalFixture pg) => _pg = pg;

    [Fact]
    public async Task CreateExtension_Succeeds()
    {
        await using var db = await _pg.CreateFreshDbWithExtensionAsync();

        // temporal.tracked_tables should exist and be empty immediately after install
        var trackedCount = await db.ScalarAsync<long>(
            "SELECT count(*) FROM temporal.tracked_tables");
        Assert.Equal(0L, trackedCount);

        // The four trigger functions must be present in the temporal schema
        var functions = await db.QueryAsync(
            """
            SELECT proname
            FROM   pg_proc p
            JOIN   pg_namespace n ON n.oid = p.pronamespace
            WHERE  n.nspname = 'temporal'
              AND  proname IN ('row_stamp', 'write_history', 'block_truncate', 'protect_history')
            ORDER BY proname
            """,
            r => r.GetString(0));

        Assert.Contains("block_truncate", functions);
        Assert.Contains("protect_history", functions);
        Assert.Contains("row_stamp", functions);
        Assert.Contains("write_history", functions);
    }
}
