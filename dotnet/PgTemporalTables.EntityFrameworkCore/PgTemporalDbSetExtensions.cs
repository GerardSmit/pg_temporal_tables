using Microsoft.EntityFrameworkCore;

namespace PgTemporalTables.EntityFrameworkCore;

/// <summary>
/// SQL Server-style temporal range operators, served from the
/// <c>&lt;table&gt;__versions</c> view that pg_temporal_tables maintains.
/// Results are always no-tracking (range operators can return several
/// versions of the same key). Boundary semantics match SQL Server
/// FOR SYSTEM_TIME exactly. All timestamps are UTC.
///
/// The version metadata of each returned row is available via
/// <see cref="PgTemporal.ModDate{TEntity}"/> / <see cref="PgTemporal.ModUser{TEntity}"/>
/// in projections, or the shadow properties "ValidFrom"/"ChangedBy".
///
/// For point-in-time reads — including JOINs and Include() — use
/// <see cref="PgTemporalQueryableExtensions.TemporalAsOf{TEntity}(IQueryable{TEntity}, DateTime)"/>.
/// </summary>
public static class PgTemporalDbSetExtensions
{
    /// <summary>Every version ever recorded (history + current).</summary>
    public static IQueryable<TEntity> TemporalAll<TEntity>(this DbSet<TEntity> set)
        where TEntity : class
        => Versions(set, "");

    /// <summary>
    /// Versions active in the interval, both boundaries exclusive:
    /// <c>valid_from &lt; to AND valid_to &gt; from</c>.
    /// </summary>
    public static IQueryable<TEntity> TemporalFromTo<TEntity>(this DbSet<TEntity> set,
        DateTime utcFrom, DateTime utcTo)
        where TEntity : class
        => Versions(set,
            "WHERE valid_from < {1} AND COALESCE(valid_to, 'infinity') > {0}",
            Utc(utcFrom), Utc(utcTo));

    /// <summary>
    /// Like FromTo, but the upper boundary is inclusive:
    /// <c>valid_from &lt;= to AND valid_to &gt; from</c>.
    /// </summary>
    public static IQueryable<TEntity> TemporalBetween<TEntity>(this DbSet<TEntity> set,
        DateTime utcFrom, DateTime utcTo)
        where TEntity : class
        => Versions(set,
            "WHERE valid_from <= {1} AND COALESCE(valid_to, 'infinity') > {0}",
            Utc(utcFrom), Utc(utcTo));

    /// <summary>
    /// Versions whose entire lifetime lies within the interval (closed rows
    /// only): <c>valid_from &gt;= from AND valid_to &lt;= to</c>.
    /// </summary>
    public static IQueryable<TEntity> TemporalContainedIn<TEntity>(this DbSet<TEntity> set,
        DateTime utcFrom, DateTime utcTo)
        where TEntity : class
        => Versions(set,
            "WHERE valid_from >= {0} AND valid_to <= {1}",
            Utc(utcFrom), Utc(utcTo));

    private static IQueryable<TEntity> Versions<TEntity>(DbSet<TEntity> set,
        string whereClause, params object[] parameters)
        where TEntity : class
    {
        var entityType = set.EntityType;
        var table = entityType.GetTableName()
            ?? throw new InvalidOperationException(
                $"Entity type {entityType.DisplayName()} is not mapped to a table.");

        if (entityType.FindAnnotation(PgTemporalAnnotationNames.IsTemporal)?.Value is not true)
            throw new InvalidOperationException(
                $"Entity type {entityType.DisplayName()} is not configured as temporal. " +
                "Mark it with .ToTable(t => t.IsTemporal()).");

        var schema = entityType.GetSchema() ?? "public";
        var view = $"\"{schema.Replace("\"", "\"\"")}\".\"{table.Replace("\"", "\"\"")}__versions\"";

        // The versions view exposes all entity columns plus valid_from /
        // changed_by (mapped as shadow properties) and valid_to / deleted_by /
        // is_current (unmapped, ignored by the materializer).
        //
        // EF1002 suppressed: the only interpolated value is `view`, built from
        // the EF model's own schema/table identifiers and double-quote-escaped
        // above; `whereClause` is a compile-time constant from this file. All
        // caller-supplied values flow through `parameters` as real SQL params.
#pragma warning disable EF1002
        return set
            .FromSqlRaw($"SELECT * FROM {view} {whereClause}", parameters)
            .AsNoTracking();
#pragma warning restore EF1002
    }

    private static DateTime Utc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
