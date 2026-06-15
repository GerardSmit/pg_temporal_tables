namespace PgTemporalTables.EntityFrameworkCore;

/// <summary>
/// LINQ-translatable pg_temporal_tables functions. These methods can only be
/// used inside EF Core queries on a context configured with UseTemporalTables()
/// — calling them directly throws.
/// </summary>
public static class PgTemporal
{
    /// <summary>
    /// Query-level time travel marker, translated to
    /// <c>temporal.as_of('...')</c>: the planner removes it and the WHOLE
    /// statement (all joined tracked tables) reads the given UTC moment.
    /// Usually applied via <see cref="PgTemporalQueryableExtensions.TemporalAsOf{TEntity}"/>,
    /// but composes anywhere: <c>.Where(u =&gt; u.Active &amp;&amp; PgTemporal.AsOf(t))</c>.
    /// </summary>
    public static bool AsOf(DateTime utcPointInTime)
        => throw new InvalidOperationException(
            "PgTemporal.AsOf is only usable inside an EF Core query on a context "
            + "whose model marks tables with IsTemporal().");

    /// <summary>
    /// When the row version was made (the <c>valid_from</c> column / the
    /// <c>temporal.mod_date()</c> SQL function). Usable in Where/Select/OrderBy:
    /// <c>.Where(u =&gt; PgTemporal.ModDate(u) &gt; cutoff)</c>.
    /// </summary>
    public static DateTime ModDate<TEntity>(TEntity entity)
        where TEntity : class
        => throw new InvalidOperationException(
            "PgTemporal.ModDate is only usable inside an EF Core query.");

    /// <summary>
    /// Who made the row version (the <c>changed_by</c> column / the
    /// <c>temporal.mod_user()</c> SQL function).
    /// <c>.Where(u =&gt; PgTemporal.ModUser(u) == "gerard@example.com")</c>.
    /// </summary>
    public static string? ModUser<TEntity>(TEntity entity)
        where TEntity : class
        => throw new InvalidOperationException(
            "PgTemporal.ModUser is only usable inside an EF Core query.");
}
