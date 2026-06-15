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

    public Func<DbContext, string?>? UserIdProvider { get; private set; }

    public PgTemporalOptionsExtension()
    {
    }

    protected PgTemporalOptionsExtension(PgTemporalOptionsExtension copyFrom)
        => UserIdProvider = copyFrom.UserIdProvider;

    public virtual PgTemporalOptionsExtension WithUserIdProvider(Func<DbContext, string?> provider)
    {
        var clone = Clone();
        clone.UserIdProvider = provider;
        return clone;
    }

    protected virtual PgTemporalOptionsExtension Clone() => new(this);

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

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
