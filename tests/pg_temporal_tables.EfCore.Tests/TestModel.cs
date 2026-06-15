using Microsoft.EntityFrameworkCore;
using PgTemporalTables.EntityFrameworkCore;

namespace PgTemporalTables.EfCore.Tests;

// ---------------------------------------------------------------------------
// Domain entities
// ---------------------------------------------------------------------------

public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;

    /// <summary>
    /// Mapped to column <c>updated_at</c> and excluded from history tracking.
    /// Changes to this column alone must NOT produce a history row.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}

// ---------------------------------------------------------------------------
// DbContext
// ---------------------------------------------------------------------------

public class TemporalTestContext : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();

    public TemporalTestContext(DbContextOptions<TemporalTestContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Model-wide default combine interval — applied to tables that don't
        // set their own (both Role and User in this model).
        modelBuilder.UseTemporalDefaults(combineInterval: TimeSpan.Zero);

        modelBuilder.Entity<Role>(entity =>
        {
            // No per-table CombineInterval — relies on the model default above.
            entity.ToTable("roles", t => t.IsTemporal());

            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.Name).HasColumnName("name");
        });

        modelBuilder.Entity<User>(entity =>
        {
            // Typed lambda exclude — property name resolves to updated_at column
            // at migration time. No per-table CombineInterval (model default applies).
            entity.ToTable("users", t => t.IsTemporal<User>(tt =>
                tt.ExcludeColumns(u => new { u.UpdatedAt })));

            entity.Property(u => u.Id).HasColumnName("id");
            entity.Property(u => u.Name).HasColumnName("name");
            entity.Property(u => u.RoleId).HasColumnName("role_id");
            entity.Property(u => u.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne(u => u.Role)
                  .WithMany()
                  .HasForeignKey(u => u.RoleId);
        });
    }
}

// ---------------------------------------------------------------------------
// Options factory
// ---------------------------------------------------------------------------

public static class TemporalTestContextFactory
{
    /// <summary>
    /// Current default user injected by the provider (writable per test).
    /// Set before creating a context to control the stamped <c>changed_by</c>.
    /// </summary>
    public static string? DefaultUser;

    /// <summary>
    /// Build <see cref="DbContextOptions{TemporalTestContext}"/> for the given
    /// connection string, wiring in <see cref="UseTemporalTables"/> with a
    /// provider that reads <see cref="DefaultUser"/> at call time.
    /// </summary>
    public static DbContextOptions<TemporalTestContext> BuildOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<TemporalTestContext>();
        builder
            .UseNpgsql(connectionString)
            .UseTemporalTables(o => o.UseUserIdProvider(() => DefaultUser));
        return builder.Options;
    }

    /// <summary>
    /// Convenience: create a new <see cref="TemporalTestContext"/> for the
    /// given connection string.
    /// </summary>
    public static TemporalTestContext Create(string connectionString)
        => new(BuildOptions(connectionString));
}
