using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PgTemporalTables.EntityFrameworkCore;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.EfCore.Tests;

// A stand-in for IHttpContextAccessor / a scoped "current user" service.
public interface ICurrentUser { string? Name { get; } }
public sealed class FixedCurrentUser(string? name) : ICurrentUser { public string? Name { get; } = name; }

// ---------------------------------------------------------------------------
// A context exercising the Phase 4/5/6 model-configuration surface.
// ---------------------------------------------------------------------------

public class Gadget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class FeatureContext : DbContext
{
    public DbSet<Gadget> Gadgets => Set<Gadget>();

    public FeatureContext(DbContextOptions<FeatureContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Gadget>(entity =>
        {
            entity.ToTable("gadgets", t => t.IsTemporal<Gadget>(tt => tt
                .CombineInterval(TimeSpan.Zero)
                .TrackLineage()
                .HistoryCompression("lz4")
                .WithRetentionPolicy(TimeSpan.FromDays(365), TimeSpan.FromHours(6))
                .WithCompactionPolicy(TimeSpan.FromDays(7))));

            entity.Property(g => g.Id).HasColumnName("id");
            entity.Property(g => g.Name).HasColumnName("name");
        });
    }

    public static FeatureContext Create(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<FeatureContext>();
        builder.UseNpgsql(connectionString).UseTemporalTables(o => o.UseUserIdProvider(() => "alice"));
        return new FeatureContext(builder.Options);
    }
}

public class Thing
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

// Sets model-wide defaults; the entity configures NO per-table compression /
// indexes / combine interval, so all must come from UseTemporalDefaults().
public class DefaultsContext : DbContext
{
    public DbSet<Thing> Things => Set<Thing>();

    public DefaultsContext(DbContextOptions<DefaultsContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseTemporalDefaults(
            combineInterval: TimeSpan.Zero,
            includeIndexes: true,
            historyCompression: "lz4");

        modelBuilder.Entity<Thing>(entity =>
        {
            entity.ToTable("things", t => t.IsTemporal());   // no per-table settings
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name");
        });
    }

    public static DefaultsContext Create(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<DefaultsContext>();
        builder.UseNpgsql(connectionString).UseTemporalTables(o => o.UseUserIdProvider(() => "alice"));
        return new DefaultsContext(builder.Options);
    }
}

/// <summary>
/// EF Core surface for Phases 4–6: model-config flow into migrations (lineage,
/// compression, policies) and the imperative maintenance handle
/// <c>context.Temporal&lt;T&gt;()</c>.
/// </summary>
public sealed class EfCoreFeatureTests
{
    private readonly PgTemporalFixture _pg;

    public EfCoreFeatureTests(PgTemporalFixture pg) => _pg = pg;

    private async Task<(NpgsqlDataSource dataSource, string dbName)> CreateBlankDbAsync()
    {
        var dbName = "effeat_" + Guid.NewGuid().ToString("N")[..8];
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

    private static async Task<long> ScalarLongAsync(NpgsqlDataSource ds, string sql)
    {
        await using var conn = await ds.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task ExecAsync(NpgsqlDataSource ds, string sql)
    {
        await using var conn = await ds.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    // ── model configuration → migration script ──────────────────────────────

    [Fact]
    public async Task Config_LineageCompressionPolicies_FlowIntoScript()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            await using var ctx = FeatureContext.Create(dataSource.ConnectionString);
            var script = ctx.Database.GenerateCreateScript();

            Assert.Contains("track_lineage => true", script, StringComparison.Ordinal);
            Assert.Contains("temporal.set_history_compression", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("'lz4'", script, StringComparison.Ordinal);
            Assert.Contains("temporal.add_retention_policy", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("temporal.add_compaction_policy", script, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    [Fact]
    public async Task Config_ModelDefaults_FlowToTablesWithoutPerTableSetting()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            await using var ctx = DefaultsContext.Create(dataSource.ConnectionString);
            var script = ctx.Database.GenerateCreateScript();

            // none set per-table on Thing — all come from UseTemporalDefaults()
            Assert.Contains("combine_interval => interval '00:00:00'", script, StringComparison.Ordinal);
            Assert.Contains("include_indexes => true", script, StringComparison.Ordinal);
            Assert.Contains("temporal.set_history_compression", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("'lz4'", script, StringComparison.Ordinal);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    [Fact]
    public async Task Config_TrackLineage_AddsShadowPropertyAndColumn()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            await using var ctx = FeatureContext.Create(dataSource.ConnectionString);

            // Shadow property exists on the model
            var prop = ctx.Model.FindEntityType(typeof(Gadget))!
                .FindProperty(PgTemporalTableBuilderExtensions.TemporalRowIdProperty);
            Assert.NotNull(prop);

            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            // Column exists in the database
            Assert.Equal(1L, await ScalarLongAsync(dataSource, """
                SELECT count(*) FROM information_schema.columns
                WHERE table_name = 'gadgets' AND column_name = 'temporal_row_id'
                """));

            // Policies were declared in the catalog
            Assert.Equal(2L, await ScalarLongAsync(dataSource,
                "SELECT count(*) FROM temporal.policies WHERE table_oid = 'gadgets'::regclass"));

            // Insert populates the lineage shadow property via RETURNING
            var g = new Gadget { Name = "a" };
            ctx.Gadgets.Add(g);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var lineage = (long)ctx.Entry(g)
                .Property(PgTemporalTableBuilderExtensions.TemporalRowIdProperty).CurrentValue!;
            Assert.True(lineage > 0);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    [Fact]
    public async Task UserIdProvider_FromServiceProvider_StampsChangedBy()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<ICurrentUser>(new FixedCurrentUser("svc-user"));
            services.AddDbContext<TemporalTestContext>(o => o
                .UseNpgsql(dataSource.ConnectionString)
                .UseTemporalTables(b => b.UseUserIdProvider(
                    sp => sp.GetRequiredService<ICurrentUser>().Name)));   // resolves from app DI

            await using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<TemporalTestContext>();
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "R" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var user = new User { Name = "Alice", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            var changedBy = ctx.Entry(user)
                .Property(PgTemporalTableBuilderExtensions.ChangedByProperty).CurrentValue as string;
            Assert.Equal("svc-user", changedBy);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    [Fact]
    public async Task UserIdProvider_InjectsDelegateParameters()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<ICurrentUser>(new FixedCurrentUser("delegate-user"));
            services.AddDbContext<TemporalTestContext>(o => o
                .UseNpgsql(dataSource.ConnectionString)
                // minimal-API style: the parameter is injected from DI
                .UseTemporalTables(b => b.UseUserIdProvider((ICurrentUser u) => u.Name)));

            await using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<TemporalTestContext>();
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "R" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var user = new User { Name = "Alice", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            var changedBy = ctx.Entry(user)
                .Property(PgTemporalTableBuilderExtensions.ChangedByProperty).CurrentValue as string;
            Assert.Equal("delegate-user", changedBy);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    [Fact]
    public async Task Heatmap_GroupsVersionsByDay_InLinq()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            TemporalTestContextFactory.DefaultUser = "alice";
            await using var ctx = TemporalTestContextFactory.Create(dataSource.ConnectionString);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "R" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var user = new User { Name = "v1", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            ctx.SetTemporalUserId("bob");
            user.Name = "v2";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);   // 1 history row
            ctx.ChangeTracker.Clear();

            // heatmap: versions per day per author — server-side group/aggregate
            var heat = await ctx.Users
                .TemporalAll()
                .Where(u => PgTemporal.ModUser(u) == "alice" || PgTemporal.ModUser(u) == "bob")
                .GroupBy(u => PgTemporal.ModDate(u).Date)
                .Select(g => new
                {
                    Bucket = g.Key,
                    ChangedRows = g.Select(u => u.Id).Distinct().Count(),
                    TotalChanges = g.Count(),
                })
                .OrderBy(x => x.Bucket)
                .ToListAsync(TestContext.Current.CancellationToken);

            Assert.NotEmpty(heat);
            // v1 (alice, history) + v2 (bob, current) = 2 versions total
            Assert.Equal(2, heat.Sum(h => h.TotalChanges));
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    // ── maintenance handle (runtime) ────────────────────────────────────────

    [Fact]
    public async Task Maintenance_GetStats_ReturnsCounts()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            TemporalTestContextFactory.DefaultUser = "alice";
            await using var ctx = TemporalTestContextFactory.Create(dataSource.ConnectionString);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "R" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var user = new User { Name = "v1", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            ctx.SetTemporalUserId("bob");
            user.Name = "v2";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            var stats = await ctx.Temporal<User>().GetStatsAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, stats.BaseRows);
            Assert.Equal(1, stats.HistoryRows);
            Assert.Equal(2, stats.VersionCount);
            Assert.True(stats.HistorySizeBytes > 0);
            Assert.NotNull(stats.AvgChainLength);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    [Fact]
    public async Task Maintenance_GetChanges_ReturnsChangedColumns()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            TemporalTestContextFactory.DefaultUser = "alice";
            await using var ctx = TemporalTestContextFactory.Create(dataSource.ConnectionString);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "R" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var user = new User { Name = "Alice", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            ctx.SetTemporalUserId("bob");
            user.Name = "Alice B";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            var changes = await ctx.Temporal<User>().GetChangesAsync(
                DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1),
                user.Id, TestContext.Current.CancellationToken);   // single-key overload

            var update = Assert.Single(changes, c => c.Operation == "UPDATE");
            Assert.Equal("bob", update.ChangedBy);
            Assert.NotNull(update.ChangedColumns);
            Assert.Contains("name", update.ChangedColumns!);
            Assert.NotNull(update.NewRow);   // JSON text
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    [Fact]
    public async Task Maintenance_Changes_ComposesInLinq()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            TemporalTestContextFactory.DefaultUser = "alice";
            await using var ctx = TemporalTestContextFactory.Create(dataSource.ConnectionString);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "R" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var user = new User { Name = "Alice", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            ctx.SetTemporalUserId("bob");
            user.Name = "Alice B";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            var from = DateTimeOffset.UtcNow.AddHours(-1);
            var to = DateTimeOffset.UtcNow.AddHours(1);

            // WHERE composes to SQL over temporal.changes(...)
            var updates = await ctx.Temporal<User>().Changes(from, to, new object[] { user.Id })
                .Where(c => c.Operation == "UPDATE")
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Single(updates);
            Assert.Equal("bob", updates[0].ChangedBy);

            // COUNT composes
            var bobCount = await ctx.Temporal<User>().Changes(from, to)
                .Where(c => c.ChangedBy == "bob")
                .CountAsync(TestContext.Current.CancellationToken);
            Assert.True(bobCount >= 1);

            // ORDER BY + TAKE compose
            var latest = await ctx.Temporal<User>().Changes(from, to)
                .OrderByDescending(c => c.ChangedAt)
                .Take(1)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Single(latest);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    [Fact]
    public async Task Maintenance_Restore_RollsBack()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            TemporalTestContextFactory.DefaultUser = "alice";
            await using var ctx = TemporalTestContextFactory.Create(dataSource.ConnectionString);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "R" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var user = new User { Name = "Alice", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            await Task.Delay(50, TestContext.Current.CancellationToken);
            var m1 = await ClockAsync(dataSource);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            ctx.SetTemporalUserId("bob");
            user.Name = "Alice B";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            await ctx.Temporal<User>().RestoreAsync(user.Id, m1, TestContext.Current.CancellationToken);

            ctx.ChangeTracker.Clear();
            var restored = await ctx.Users.AsNoTracking()
                .FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
            Assert.Equal("Alice", restored.Name);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    [Fact]
    public async Task Maintenance_RestoreDeleted_Resurrects()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            TemporalTestContextFactory.DefaultUser = "alice";
            await using var ctx = TemporalTestContextFactory.Create(dataSource.ConnectionString);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "R" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var user = new User { Name = "Alice", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var id = user.Id;

            ctx.SetTemporalUserId("bob");
            ctx.Users.Remove(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            ctx.ChangeTracker.Clear();

            Assert.Equal(0L, await ScalarLongAsync(dataSource, $"SELECT count(*) FROM users WHERE id = {id}"));

            await ctx.Temporal<User>().RestoreDeletedAsync(id, TestContext.Current.CancellationToken);

            Assert.Equal(1L, await ScalarLongAsync(dataSource, $"SELECT count(*) FROM users WHERE id = {id}"));
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    [Fact]
    public async Task Maintenance_DropHistoryBefore_Prunes()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            TemporalTestContextFactory.DefaultUser = "alice";
            await using var ctx = TemporalTestContextFactory.Create(dataSource.ConnectionString);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "R" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var user = new User { Name = "v1", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            ctx.SetTemporalUserId("bob");
            user.Name = "v2";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1L, await ScalarLongAsync(dataSource, "SELECT count(*) FROM users__history"));

            await ctx.Temporal<User>().DropHistoryBeforeAsync(
                DateTimeOffset.UtcNow.AddDays(1), TestContext.Current.CancellationToken);

            Assert.Equal(0L, await ScalarLongAsync(dataSource, "SELECT count(*) FROM users__history"));
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    [Fact]
    public async Task Maintenance_CompactHistory_RunsWithoutError()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            TemporalTestContextFactory.DefaultUser = "alice";
            await using var ctx = TemporalTestContextFactory.Create(dataSource.ConnectionString);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var role = new Role { Name = "R" };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            var user = new User { Name = "v1", RoleId = role.Id };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            ctx.SetTemporalUserId("bob");
            user.Name = "v2";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Distinct versions → nothing to merge, but the call must succeed.
            await ctx.Temporal<User>().CompactHistoryAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1L, await ScalarLongAsync(dataSource, "SELECT count(*) FROM users__history"));
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    [Fact]
    public async Task Maintenance_RunDuePolicies_RunsConfigured()
    {
        var (dataSource, dbName) = await CreateBlankDbAsync();
        try
        {
            TemporalTestContextFactory.DefaultUser = "alice";
            await using var ctx = TemporalTestContextFactory.Create(dataSource.ConnectionString);
            await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            // No policies yet → nothing runs.
            Assert.Equal(0, await ctx.RunDueTemporalPoliciesAsync(
                cancellationToken: TestContext.Current.CancellationToken));

            // Declare an always-due compaction policy out-of-band, then run.
            await ExecAsync(dataSource,
                "SELECT temporal.add_compaction_policy('users', p_run_every => interval '0')");
            var ran = await ctx.RunDueTemporalPoliciesAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, ran);
        }
        finally
        {
            await DropBlankDbAsync(dataSource, dbName);
        }
    }

    private static async Task<DateTimeOffset> ClockAsync(NpgsqlDataSource ds)
    {
        await using var conn = await ds.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT clock_timestamp()";
        await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await r.ReadAsync(TestContext.Current.CancellationToken);
        return r.GetFieldValue<DateTimeOffset>(0);
    }
}
