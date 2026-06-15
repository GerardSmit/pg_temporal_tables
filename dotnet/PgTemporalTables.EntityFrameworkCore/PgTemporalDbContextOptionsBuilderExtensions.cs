using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using PgTemporalTables.EntityFrameworkCore.Infrastructure;

namespace PgTemporalTables.EntityFrameworkCore;

/// <summary>
/// DbContext options wiring for pg_temporal_tables. Call after UseNpgsql():
/// <code>
/// optionsBuilder
///     .UseNpgsql(connectionString)
///     .UseTemporalTables(o => o.UseUserIdProvider(ctx => currentUser));
/// </code>
/// </summary>
public static class PgTemporalDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Enables pg_temporal_tables integration: migrations emit
    /// temporal.enable()/disable() for entities marked IsTemporal(),
    /// temporal.user_id is set per connection, and SetTemporalAsOf()
    /// context-wide time travel becomes available.
    /// </summary>
    public static DbContextOptionsBuilder UseTemporalTables(
        this DbContextOptionsBuilder optionsBuilder,
        Action<PgTemporalOptionsBuilder>? configure = null)
    {
        var extension = optionsBuilder.Options.FindExtension<PgTemporalOptionsExtension>()
            ?? new PgTemporalOptionsExtension();

        var builder = new PgTemporalOptionsBuilder(extension);
        configure?.Invoke(builder);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(builder.Extension);

        optionsBuilder
            .ReplaceService<IMigrationsSqlGenerator, TemporalMigrationsSqlGenerator>()
            .ReplaceService<IRelationalAnnotationProvider, TemporalAnnotationProvider>()
            .ReplaceService<IMigrationsAnnotationProvider, TemporalMigrationsAnnotationProvider>()
            .AddInterceptors(
                TemporalUserIdConnectionInterceptor.Instance,
                PgTemporalQueryExpressionInterceptor.Instance);

        return optionsBuilder;
    }

    /// <inheritdoc cref="UseTemporalTables(DbContextOptionsBuilder, Action{PgTemporalOptionsBuilder}?)"/>
    public static DbContextOptionsBuilder<TContext> UseTemporalTables<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<PgTemporalOptionsBuilder>? configure = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseTemporalTables(
            (DbContextOptionsBuilder)optionsBuilder, configure);
}

/// <summary>Configuration surface for <see cref="PgTemporalDbContextOptionsBuilderExtensions.UseTemporalTables"/>.</summary>
public class PgTemporalOptionsBuilder
{
    internal PgTemporalOptionsExtension Extension { get; private set; }

    internal PgTemporalOptionsBuilder(PgTemporalOptionsExtension extension)
        => Extension = extension;

    /// <summary>
    /// Default identity provider, evaluated per connection open with the active
    /// <see cref="DbContext"/>. A per-context
    /// <see cref="PgTemporalDbContextExtensions.SetTemporalUserId"/> overrides it.
    /// </summary>
    public PgTemporalOptionsBuilder UseUserIdProvider(Func<DbContext, string?> provider)
    {
        Extension = Extension.WithUserIdProvider(provider);
        return this;
    }

    /// <inheritdoc cref="UseUserIdProvider(Func{DbContext, string?})"/>
    public PgTemporalOptionsBuilder UseUserIdProvider(Func<string?> provider)
        => UseUserIdProvider((DbContext _) => provider());

    /// <summary>
    /// Default identity provider that receives the application's
    /// <see cref="IServiceProvider"/> (the scope the DbContext was resolved in),
    /// so it can resolve services such as <c>IHttpContextAccessor</c>:
    /// <code>
    /// o.UseUserIdProvider(sp =&gt;
    ///     sp.GetRequiredService&lt;IHttpContextAccessor&gt;().HttpContext?.User?.Identity?.Name);
    /// </code>
    /// Requires the DbContext to be created through DI (<c>AddDbContext</c> /
    /// <c>AddDbContextPool</c>) so EF has the application service provider.
    /// </summary>
    public PgTemporalOptionsBuilder UseUserIdProvider(Func<IServiceProvider, string?> provider)
        => UseUserIdProvider((DbContext context) =>
        {
            var services = context.GetService<IDbContextOptions>()
                .FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
                ?? throw new InvalidOperationException(
                    "UseUserIdProvider(IServiceProvider) requires the DbContext to be created through " +
                    "dependency injection (AddDbContext/AddDbContextPool) so EF has the application service provider.");
            return provider(services);
        });

    /// <summary>
    /// Default identity provider as a delegate whose parameters are injected from
    /// the application service provider (minimal-API style), e.g.:
    /// <code>
    /// o.UseUserIdProvider((IHttpContextAccessor hca) =&gt;
    ///     hca.HttpContext?.User?.Identity?.Name);
    /// </code>
    /// Each parameter is resolved via <c>GetRequiredService</c> per connection
    /// open. The delegate must return <c>string?</c>. Requires the DbContext to
    /// be created through DI.
    /// </summary>
    public PgTemporalOptionsBuilder UseUserIdProvider(Delegate provider)
    {
        if (provider.Method.ReturnType != typeof(string))
            throw new ArgumentException(
                "The user-id delegate must return string (or string?).", nameof(provider));

        var parameters = provider.Method.GetParameters();
        return UseUserIdProvider((IServiceProvider serviceProvider) =>
        {
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                args[i] = serviceProvider.GetRequiredService(parameters[i].ParameterType);
            return (string?)provider.DynamicInvoke(args);
        });
    }
}
