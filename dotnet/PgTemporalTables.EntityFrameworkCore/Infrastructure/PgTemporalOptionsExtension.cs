using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace PgTemporalTables.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Carries pg_temporal_tables runtime configuration (the default user-id
/// provider) through the DbContext options.
/// </summary>
public class PgTemporalOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <summary>Gets the factory used to resolve the current user identifier from a <see cref="DbContext"/>.</summary>
    public Func<DbContext, string?>? UserIdProvider { get; private set; }

    /// <summary>Initializes a new default instance of <see cref="PgTemporalOptionsExtension"/>.</summary>
    public PgTemporalOptionsExtension()
    {
    }

    /// <summary>Initializes a new instance by copying settings from an existing extension.</summary>
    protected PgTemporalOptionsExtension(PgTemporalOptionsExtension copyFrom)
        => UserIdProvider = copyFrom.UserIdProvider;

    /// <summary>Returns a cloned extension with the user-id provider replaced by <paramref name="provider"/>.</summary>
    public virtual PgTemporalOptionsExtension WithUserIdProvider(Func<DbContext, string?> provider)
    {
        var clone = Clone();
        clone.UserIdProvider = provider;
        return clone;
    }

    /// <inheritdoc/>
    protected virtual PgTemporalOptionsExtension Clone() => new(this);

    /// <inheritdoc/>
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <inheritdoc/>
    public void ApplyServices(IServiceCollection services)
    {
    }

    /// <inheritdoc/>
    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(PgTemporalOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using pg_temporal_tables ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["PgTemporalTables"] = "1";
    }
}
