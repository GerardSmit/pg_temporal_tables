using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PgTemporalTables.EntityFrameworkCore;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.EfCore.Tests;

/// <summary>
/// End-to-end integration tests for the pg_temporal_tables EF Core library.
/// Each test uses a fresh PostgreSQL database created from the shared container.
/// </summary>
public sealed class EfCoreIntegrationTests
{
    private readonly PgTemporalFixture _pg;

    public EfCoreIntegrationTests(PgTemporalFixture pg) => _pg = pg;

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a new uniquely-named database (WITH the pg_temporal_tables
    /// extension already installed) and returns its connection string.
    /// The returned <see cref="TemporalDb"/> must be disposed by the caller.
    /// </summary>
    private async Task<(TemporalDb db, string connectionString)> CreateFreshDbAsync()
    {
        var db = await _pg.CreateFreshDbWithExtensionAsync();

        // NpgsqlDataSource.ConnectionString strips the password; rebuild from
        // the fixture's full connection string with the fresh database name.
        var dbName = new NpgsqlConnectionStringBuilder(db.DataSource.ConnectionString).Database;
        var csb = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
        {
            Database = dbName,
            IncludeErrorDetail = true,
        };
        return (db, csb.ConnectionString);
    }

    /// <summary>
    /// Creates a brand-new empty database (WITHOUT extension) using the
    /// fixture's admin connection, suitable for letting EnsureCreated emit
    /// <c>CREATE EXTENSION IF NOT EXISTS pg_temporal_tables</c> itself.
    ///
    /// The caller owns the returned <see cref="NpgsqlDataSource"/> and must
    /// dispose it. The database is dropped when the data source is closed.
    /// </summary>
    private async Task<(NpgsqlDataSource dataSource, string dbName)> CreateBlankDbAsync()
    {
        var dbName = "eftest_" + Guid.NewGuid().ToString("N")[..8];

        // Use the admin connection from the fixture to create the blank database.
        await using (var admin = _pg.CreateConnection())
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var csb = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
        {
            Database = dbName,
            IncludeErrorDetail = true,
            // tests read dataSource.ConnectionString back; keep the password in it
            PersistSecurityInfo = true,
        };

        return (NpgsqlDataSource.Create(csb.ConnectionString), dbName);
    }

    private async Task DropBlankDbAsync(NpgsqlDataSource dataSource, string dbName)
    {
        await dataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await _pg.DropDbAsync(dbName);
    }

    private static async Task<DateTime> ClockTimestampAsync(NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT clock_timestamp()";
        return (DateTime)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // -----------------------------------------------------------------------
    // 1. EnsureCreated emits temporal.enable() and creates users__history
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EnsureCreated_EnablesTemporal()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            await using var ctx = TemporalTestContextFactory.Create(cs);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            // Both tables must be tracked
            await using var conn = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT count(*) FROM temporal.tracked_tables";
            var count = (long)(await countCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal(2L, count);

            // users__history must exist
            await using var histCmd = conn.CreateCommand();
            histCmd.CommandText = """
                SELECT count(*) FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = 'users__history'
                """;
            var histExists = (long)(await histCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal(1L, histExists);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // -----------------------------------------------------------------------
    // 2. SaveChanges stamps ChangedBy and ValidFrom (via shadow properties)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SaveChanges_StampsChangedBy()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            TemporalTestContextFactory.DefaultUser = "alice";

            await using var ctx = TemporalTestContextFactory.Create(cs);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "Admin" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            var user = new User { Name = "Alice", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            // After SaveChanges the EF entry should have the store-generated values
            var changedBy = ctx.Entry(user).Property(PgTemporalTableBuilderExtensions.ChangedByProperty).CurrentValue as string;
            var validFrom = (DateTime?)ctx.Entry(user).Property(PgTemporalTableBuilderExtensions.ValidFromProperty).CurrentValue;

            Assert.Equal("alice", changedBy);
            Assert.NotEqual(default, validFrom);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // -----------------------------------------------------------------------
    // 3. SetTemporalUserId overrides the provider for the context
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SetTemporalUserId_OverridesProvider()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            TemporalTestContextFactory.DefaultUser = "provider-user";

            await using var ctx = TemporalTestContextFactory.Create(cs);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "Editor" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Override to 'bob' at the context level
            ctx.SetTemporalUserId("bob");

            var user = new User { Name = "Bob", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            var changedBy = ctx.Entry(user).Property(PgTemporalTableBuilderExtensions.ChangedByProperty).CurrentValue as string;
            Assert.Equal("bob", changedBy);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // -----------------------------------------------------------------------
    // 4. UPDATE creates a history row (two SaveChanges = two transactions)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Update_CreatesHistoryRow()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            TemporalTestContextFactory.DefaultUser = "alice";

            await using var ctx = TemporalTestContextFactory.Create(cs);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            // First SaveChanges: INSERT
            var role = new Role { Name = "Member" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            var user = new User { Name = "Alice", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Second SaveChanges: UPDATE as a different user so there's no combine
            ctx.SetTemporalUserId("bob");
            user.Name = "Alice B";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Verify via raw SQL that users__history has exactly 1 row
            await using var conn = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM users__history";
            var histCount = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal(1L, histCount);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // -----------------------------------------------------------------------
    // 5. Updating an excluded column does NOT produce a history row
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExcludedColumn_Update_NoHistory()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            TemporalTestContextFactory.DefaultUser = "alice";

            await using var ctx = TemporalTestContextFactory.Create(cs);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "Viewer" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            var user = new User { Name = "Charlie", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Only update the excluded column
            ctx.SetTemporalUserId("dave");
            user.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            // History count must still be 0
            await using var conn = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM users__history";
            var histCount = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal(0L, histCount);

            // changed_by on the current row should still be 'alice' (no write_history trigger fired)
            // Use raw SQL to read the column directly — shadow props on tracked entities
            // reflect store-generated values after SaveChanges, but a fresh query needs
            // tracking (not AsNoTracking) to populate shadows via the RETURNING path.
            await using var changedByCmd = conn.CreateCommand();
            changedByCmd.CommandText = $"SELECT changed_by FROM users WHERE id = {user.Id}";
            var changedBy = (string?)(await changedByCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal("alice", changedBy);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // -----------------------------------------------------------------------
    // 6. TemporalAsOf IQueryable extension time-travels Include()d tables
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TemporalAsOf_TimeTravelsIncludes()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            TemporalTestContextFactory.DefaultUser = "alice";

            await using var ctx = TemporalTestContextFactory.Create(cs);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            // Seed initial data
            var role = new Role { Name = "Admin" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            var user = new User { Name = "Alice", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Capture a moment in time (after small delay for clock granularity)
            await Task.Delay(50, TestContext.Current.CancellationToken);
            var m1 = await ClockTimestampAsync(dataSource);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            // Mutate as 'bob'
            ctx.SetTemporalUserId("bob");
            role.Name = "Administrator";
            user.Name = "Alice B";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            ctx.ChangeTracker.Clear();

            // The marker time-travels the entire statement — the JOIN to roles reads
            // the same moment, so Role.Name must be 'Admin' (the historic value).
            var historicUser = await ctx.Users
                .Include(u => u.Role)
                .TemporalAsOf(m1)
                .AsNoTracking()
                .FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);

            Assert.Equal("Alice", historicUser.Name);
            Assert.Equal("Admin", historicUser.Role.Name);

            // Without the marker, present values are returned.
            var presentUser = await ctx.Users
                .Include(u => u.Role)
                .AsNoTracking()
                .FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);

            Assert.Equal("Alice B", presentUser.Name);
            Assert.Equal("Administrator", presentUser.Role.Name);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // -----------------------------------------------------------------------
    // 7. TemporalAsOf IQueryable extension — returns old version, composes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TemporalAsOf_ReturnsOldVersion_AndComposes()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            TemporalTestContextFactory.DefaultUser = "alice";

            await using var ctx = TemporalTestContextFactory.Create(cs);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "SnapshotRole" };
            ctx.Roles.Add(role);
            ctx.Users.Add(new User { Name = "Alice", Role = role });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Re-query to get the DB-assigned Id
            ctx.ChangeTracker.Clear();
            var seededUser = await ctx.Users.FirstAsync(TestContext.Current.CancellationToken);

            await Task.Delay(50, TestContext.Current.CancellationToken);
            var m1 = await ClockTimestampAsync(dataSource);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            ctx.SetTemporalUserId("bob");
            seededUser.Name = "Alice B";
            ctx.Users.Update(seededUser);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            ctx.ChangeTracker.Clear();

            // Extension method form: .TemporalAsOf(m1) appended to any IQueryable
            var viaExtension = await ctx.Users
                .TemporalAsOf(m1)
                .Where(u => u.Id == seededUser.Id)
                .AsNoTracking()
                .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(viaExtension);
            Assert.Equal("Alice", viaExtension!.Name);

            // Raw marker in Where: PgTemporal.AsOf(m1) composes as a bool predicate
            var viaRawMarker = await ctx.Users
                .Where(u => u.Id == seededUser.Id && PgTemporal.AsOf(m1))
                .AsNoTracking()
                .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(viaRawMarker);
            Assert.Equal("Alice", viaRawMarker!.Name);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // -----------------------------------------------------------------------
    // 9. TemporalAll returns all versions (history + current)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TemporalAll_ReturnsAllVersions()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            TemporalTestContextFactory.DefaultUser = "alice";

            await using var ctx = TemporalTestContextFactory.Create(cs);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "AllRole" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            var user = new User { Name = "v1", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);  // version 1

            // Update 1 — different user => guaranteed history row
            ctx.SetTemporalUserId("bob");
            user.Name = "v2";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);  // version 2

            // Update 2 — yet another user
            ctx.SetTemporalUserId("carol");
            user.Name = "v3";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);  // version 3
            ctx.ChangeTracker.Clear();

            var allVersions = await ctx.Users
                .TemporalAll()
                .Where(u => u.Id == user.Id)
                .ToListAsync(TestContext.Current.CancellationToken);

            // 2 history rows + 1 current = 3 versions
            Assert.Equal(3, allVersions.Count);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // -----------------------------------------------------------------------
    // 10. PgTemporal.ModDate / ModUser translate in queries
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ModDate_ModUser_TranslateInQueries()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            TemporalTestContextFactory.DefaultUser = "alice";

            await using var ctx = TemporalTestContextFactory.Create(cs);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "ModRole" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            var user = new User { Name = "Alice", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Update as 'bob' — produces a history row and stamps changed_by = 'bob'
            ctx.SetTemporalUserId("bob");
            user.Name = "Alice B";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            ctx.ChangeTracker.Clear();

            var cutoff = DateTime.UtcNow.AddMinutes(-1);

            // Filter by ModUser — only the row changed by 'bob' (the current one)
            var bobCount = await ctx.Users
                .Where(u => PgTemporal.ModUser(u) == "bob")
                .CountAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, bobCount);

            // Projection: Select ModDate and ModUser into an anonymous type
            var projected = await ctx.Users
                .Where(u => u.Id == user.Id)
                .Select(u => new
                {
                    u.Name,
                    At = PgTemporal.ModDate(u),
                    By = PgTemporal.ModUser(u),
                })
                .FirstAsync(TestContext.Current.CancellationToken);

            Assert.Equal("bob", projected.By);
            Assert.True(projected.At >= cutoff, "ModDate should be within the last minute");

            // Combine with TemporalAll: order all versions chronologically via ModDate
            var allOrdered = await ctx.Users
                .TemporalAll()
                .Where(u => u.Id == user.Id)
                .OrderBy(u => PgTemporal.ModDate(u))
                .Select(u => new
                {
                    u.Name,
                    By = PgTemporal.ModUser(u),
                })
                .ToListAsync(TestContext.Current.CancellationToken);

            // 1 history row (alice) + 1 current row (bob) = 2 versions in order
            Assert.Equal(2, allOrdered.Count);
            Assert.Equal("alice", allOrdered[0].By);
            Assert.Equal("bob", allOrdered[1].By);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // -----------------------------------------------------------------------
    // 11. Typed ExcludeColumns + model-default CombineInterval flow into enable
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TypedExcludes_AndModelDefaultCombine_FlowIntoEnable()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            await using var ctx = TemporalTestContextFactory.Create(cs);

            var script = ctx.Database.GenerateCreateScript();

            // Typed lambda t => new { t.UpdatedAt } resolved to column name 'updated_at'
            Assert.Contains("excluded_columns => ARRAY['updated_at']::name[]", script,
                StringComparison.Ordinal);

            // Model-wide default combine interval (TimeSpan.Zero → "00:00:00") must appear
            // in BOTH temporal.enable() calls (roles and users), not just users.
            var enableIdx1 = script.IndexOf("temporal.enable", StringComparison.OrdinalIgnoreCase);
            Assert.True(enableIdx1 >= 0, "No temporal.enable found in script");
            var enableIdx2 = script.IndexOf("temporal.enable", enableIdx1 + 1, StringComparison.OrdinalIgnoreCase);
            Assert.True(enableIdx2 >= 0, "Only one temporal.enable found; expected two");

            // Both invocations must carry the combine_interval annotation
            var firstEnable = script[enableIdx1..script.IndexOf(';', enableIdx1)];
            var secondEnable = script[enableIdx2..script.IndexOf(';', enableIdx2)];

            Assert.Contains("combine_interval => interval '00:00:00'", firstEnable, StringComparison.Ordinal);
            Assert.Contains("combine_interval => interval '00:00:00'", secondEnable, StringComparison.Ordinal);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // -----------------------------------------------------------------------
    // 12. TemporalFromTo / Between / ContainedIn boundary semantics
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TemporalFromTo_Between_ContainedIn_Boundaries()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            TemporalTestContextFactory.DefaultUser = "alice";

            await using var ctx = TemporalTestContextFactory.Create(cs);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "BoundaryRole" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Insert v1 and capture exact ValidFrom from the entry
            var user = new User { Name = "v1", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var t0 = (DateTime)ctx.Entry(user).Property(PgTemporalTableBuilderExtensions.ValidFromProperty).CurrentValue!;

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // Update to v2 (bob, different user => history row)
            ctx.SetTemporalUserId("bob");
            user.Name = "v2";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var t1 = (DateTime)ctx.Entry(user).Property(PgTemporalTableBuilderExtensions.ValidFromProperty).CurrentValue!;

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // Update to v3 (carol)
            ctx.SetTemporalUserId("carol");
            user.Name = "v3";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var t2 = (DateTime)ctx.Entry(user).Property(PgTemporalTableBuilderExtensions.ValidFromProperty).CurrentValue!;

            ctx.ChangeTracker.Clear();

            // FromTo: valid_from < upper AND valid_to > lower (both exclusive)
            // v1 runs [t0, t1), v2 runs [t1, t2), v3 runs [t2, ∞)
            // FromTo(t0, t1): v1 valid_from(t0) < t1 ✓, v1 valid_to(t1) > t0 ✓ → v1 included
            //                 v2 valid_from(t1) < t1? NO → v2 excluded
            var fromTo = await ctx.Users.TemporalFromTo(t0, t1)
                .Where(u => u.Id == user.Id)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Single(fromTo);
            Assert.Equal("v1", fromTo[0].Name);

            // Between: valid_from <= upper AND valid_to > lower (upper inclusive)
            // Between(t0, t1): v2 valid_from(t1) <= t1 ✓, v2 valid_to(t2) > t0 ✓ → also includes v2
            var between = await ctx.Users.TemporalBetween(t0, t1)
                .Where(u => u.Id == user.Id)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, between.Count);

            // ContainedIn: valid_from >= lower AND valid_to <= upper (closed rows only)
            // ContainedIn(t0, t2): v1 [t0,t1) and v2 [t1,t2) both fully within [t0,t2] → 2 rows
            // v3 is open (valid_to = infinity) → not included
            var containedIn = await ctx.Users.TemporalContainedIn(t0, t2)
                .Where(u => u.Id == user.Id)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, containedIn.Count);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // -----------------------------------------------------------------------
    // 13. MigrationsSqlGenerator emits temporal.disable() BEFORE DROP TABLE
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MigrationsSqlGenerator_DropTable_EmitsDisable()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            await using var ctx = TemporalTestContextFactory.Create(cs);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var generator = ctx.GetService<IMigrationsSqlGenerator>();

            var op = new DropTableOperation { Name = "users", Schema = null };
            op.AddAnnotation(PgTemporalAnnotationNames.IsTemporal, true);

            var cmds = generator.Generate(new MigrationOperation[] { op });

            var texts = cmds.Select(c => c.CommandText).ToList();

            // Must contain a temporal.disable() call
            Assert.Contains(texts, t => t.Contains("temporal.disable"));

            // temporal.disable must appear before DROP TABLE
            var disableIdx = texts.FindIndex(t => t.Contains("temporal.disable"));
            var dropIdx = texts.FindIndex(t => t.Contains("DROP TABLE"));

            Assert.True(disableIdx >= 0, "No temporal.disable command found");
            Assert.True(dropIdx >= 0, "No DROP TABLE command found");
            Assert.True(disableIdx < dropIdx,
                $"temporal.disable (index {disableIdx}) must come before DROP TABLE (index {dropIdx})");
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // -----------------------------------------------------------------------
    // 14. GenerateCreateScript contains temporal.enable(), CREATE EXTENSION,
    //     typed-excluded column name, and model-default combine interval
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MigrationsSqlGenerator_CreateTable_EmitsEnable()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var cs = dataSource.ConnectionString;
            await using var ctx = TemporalTestContextFactory.Create(cs);

            var script = ctx.Database.GenerateCreateScript();

            // Must contain CREATE EXTENSION
            Assert.Contains("CREATE EXTENSION IF NOT EXISTS pg_temporal_tables", script,
                StringComparison.OrdinalIgnoreCase);

            // Must contain temporal.enable()
            Assert.Contains("temporal.enable", script, StringComparison.OrdinalIgnoreCase);

            // Typed ExcludeColumns(t => new { t.UpdatedAt }) resolves property name to
            // its mapped column name 'updated_at' — check the exact SQL literal form.
            Assert.Contains("excluded_columns => ARRAY['updated_at']::name[]", script,
                StringComparison.Ordinal);

            // UseTemporalDefaults(combineInterval: TimeSpan.Zero) produces "00:00:00"
            // which must appear in the enable() calls for both tables.
            Assert.Contains("combine_interval => interval '00:00:00'", script,
                StringComparison.Ordinal);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }
}
