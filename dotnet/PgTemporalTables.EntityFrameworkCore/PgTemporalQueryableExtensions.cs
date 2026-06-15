using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace PgTemporalTables.EntityFrameworkCore;

/// <summary>
/// Per-query time travel. Composes on any IQueryable — the marker applies to
/// the WHOLE generated statement, so joined/included tracked tables read the
/// same moment.
/// </summary>
public static class PgTemporalQueryableExtensions
{
    internal static readonly MethodInfo AsOfMethod =
        typeof(PgTemporal).GetMethod(nameof(PgTemporal.AsOf))!;

    /// <summary>
    /// Reads this query — including every joined or Include()d tracked table —
    /// as of the given UTC moment. Translated to a <c>temporal.as_of('...')</c>
    /// marker that the pg_temporal_tables planner consumes; the timestamp is
    /// inlined as a SQL literal (same approach as SQL Server's TemporalAsOf).
    /// </summary>
    public static IQueryable<TEntity> TemporalAsOf<TEntity>(
        this IQueryable<TEntity> source, DateTime utcPointInTime)
        where TEntity : class
    {
        var utc = utcPointInTime.Kind switch
        {
            DateTimeKind.Utc => utcPointInTime,
            DateTimeKind.Local => utcPointInTime.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utcPointInTime, DateTimeKind.Utc)
        };

        // AsOf is a model-registered DbFunction (the funcletizer never
        // client-evaluates those), and Expression.Constant (not a captured
        // variable) keeps the timestamp a SQL literal, which the PostgreSQL
        // planner needs at plan time.
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var predicate = Expression.Lambda<Func<TEntity, bool>>(
            Expression.Call(AsOfMethod, Expression.Constant(utc)),
            parameter);

        return source.Where(predicate);
    }

    /// <inheritdoc cref="TemporalAsOf{TEntity}(IQueryable{TEntity}, DateTime)"/>
    public static IQueryable<TEntity> TemporalAsOf<TEntity>(
        this IQueryable<TEntity> source, DateTimeOffset pointInTime)
        where TEntity : class
        => source.TemporalAsOf(pointInTime.UtcDateTime);
}
