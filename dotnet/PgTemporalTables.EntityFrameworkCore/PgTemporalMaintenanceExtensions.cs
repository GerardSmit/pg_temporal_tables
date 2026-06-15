using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace PgTemporalTables.EntityFrameworkCore;

/// <summary>
/// Storage / version-count summary returned by
/// <see cref="TemporalMaintenance{TEntity}.GetStatsAsync"/>.
/// </summary>
public sealed record TemporalTableStats(
    long BaseRows,
    long HistoryRows,
    long VersionCount,
    long BaseSizeBytes,
    long HistorySizeBytes,
    DateTimeOffset? OldestValidFrom,
    DateTimeOffset? NewestValidFrom,
    decimal? AvgChainLength);

/// <summary>
/// A change event from <see cref="TemporalMaintenance{TEntity}.GetChangesAsync"/>.
/// <see cref="Pk"/>, <see cref="OldRow"/> and <see cref="NewRow"/> are JSON text
/// (parse with System.Text.Json if needed).
/// </summary>
public sealed record TemporalChange(
    string Pk,
    DateTimeOffset ChangedAt,
    string? ChangedBy,
    string Operation,                       // INSERT / UPDATE / DELETE
    string? OldRow,
    string? NewRow,
    string[]? ChangedColumns);

/// <summary>
/// Entry points for the imperative temporal maintenance operations, grouped per
/// entity: <c>await context.Temporal&lt;User&gt;().CompactHistoryAsync()</c>.
/// </summary>
public static class PgTemporalMaintenanceExtensions
{
    /// <summary>Maintenance operations for the table mapped to <typeparamref name="TEntity"/>.</summary>
    public static TemporalMaintenance<TEntity> Temporal<TEntity>(this DbContext context)
        where TEntity : class
        => new(context);


    /// <summary>
    /// Run all due retention/compaction policies (<c>temporal.run_due_policies</c>).
    /// Returns the number of policies executed. Intended for a scheduler.
    /// </summary>
    public static async Task<int> RunDueTemporalPoliciesAsync(
        this DbContext context, TimeSpan? maxDuration = null, CancellationToken cancellationToken = default)
    {
        var conn = context.Database.GetDbConnection();
        var opened = false;
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
            opened = true;
        }
        try
        {
            await using var cmd = conn.CreateCommand();
            if (maxDuration is { } d)
            {
                cmd.CommandText = "SELECT temporal.run_due_policies($1)";
                var p = cmd.CreateParameter();
                p.Value = d;
                cmd.Parameters.Add(p);
            }
            else
            {
                cmd.CommandText = "SELECT temporal.run_due_policies()";
            }
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(result);
        }
        finally
        {
            if (opened)
                await conn.CloseAsync();
        }
    }
}

/// <summary>Imperative maintenance operations for one tracked entity's table.</summary>
public sealed class TemporalMaintenance<TEntity>
    where TEntity : class
{
    private readonly DbContext _context;

    internal TemporalMaintenance(DbContext context) => _context = context;

    /// <summary>Merge redundant adjacent history versions (e.g. after dropping a column).</summary>
    public Task CompactHistoryAsync(CancellationToken cancellationToken = default)
        => _context.Database.ExecuteSqlRawAsync(
            $"SELECT temporal.compact_history({Regclass()})", cancellationToken);

    /// <summary>Delete history whose validity ended before <paramref name="olderThan"/>.</summary>
    public Task PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
        => _context.Database.ExecuteSqlRawAsync(
            $"SELECT temporal.prune({Regclass()}, {{0}})", new object[] { olderThan }, cancellationToken);

    /// <summary>
    /// Retention: drop whole history partitions older than <paramref name="cutoff"/>
    /// (if the history is partitioned by <c>valid_to</c>), then prune the rest.
    /// </summary>
    public Task DropHistoryBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        => _context.Database.ExecuteSqlRawAsync(
            $"SELECT temporal.drop_history_before({Regclass()}, {{0}})", new object[] { cutoff }, cancellationToken);

    /// <summary>Set the history table's column compression (<c>pglz</c>/<c>lz4</c>/<c>default</c>).</summary>
    public Task SetHistoryCompressionAsync(string method, CancellationToken cancellationToken = default)
        => _context.Database.ExecuteSqlRawAsync(
            $"SELECT temporal.set_history_compression({Regclass()}, {{0}})", new object[] { method }, cancellationToken);

    /// <summary>
    /// Roll the row identified by <paramref name="keyValues"/> back to its state at
    /// <paramref name="asOf"/> (audited: creates a new current version).
    /// </summary>
    public Task RestoreAsync(object[] keyValues, DateTimeOffset asOf, CancellationToken cancellationToken = default)
        => _context.Database.ExecuteSqlRawAsync(
            $"SELECT temporal.restore({Regclass()}, {{0}}::jsonb, {{1}})",
            new object[] { PrimaryKeyJson(keyValues), asOf }, cancellationToken);

    /// <inheritdoc cref="RestoreAsync(object[], DateTimeOffset, CancellationToken)"/>
    public Task RestoreAsync(object key, DateTimeOffset asOf, CancellationToken cancellationToken = default)
        => RestoreAsync(new[] { key }, asOf, cancellationToken);

    /// <summary>Re-insert the last version of a currently-deleted row.</summary>
    public Task RestoreDeletedAsync(object[] keyValues, CancellationToken cancellationToken = default)
        => _context.Database.ExecuteSqlRawAsync(
            $"SELECT temporal.restore_deleted({Regclass()}, {{0}}::jsonb)",
            new object[] { PrimaryKeyJson(keyValues) }, cancellationToken);

    /// <inheritdoc cref="RestoreDeletedAsync(object[], CancellationToken)"/>
    public Task RestoreDeletedAsync(object key, CancellationToken cancellationToken = default)
        => RestoreDeletedAsync(new[] { key }, cancellationToken);

    /// <summary>Row/version counts and storage sizes for the tracked table.</summary>
    public async Task<TemporalTableStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var conn = _context.Database.GetDbConnection();
        var opened = false;
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
            opened = true;
        }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT base_rows, history_rows, version_count, base_size_bytes, history_size_bytes, " +
                $"oldest_valid_from, newest_valid_from, avg_chain_length FROM temporal.table_stats({Regclass()})";
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await r.ReadAsync(cancellationToken))
                throw new InvalidOperationException("temporal.table_stats returned no row");

            return new TemporalTableStats(
                r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
                r.IsDBNull(7) ? null : r.GetDecimal(7));
        }
        finally
        {
            if (opened)
                await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Change events (INSERT/UPDATE/DELETE) for the table between <paramref name="from"/>
    /// and <paramref name="to"/>, each with old/new row JSON and the list of changed
    /// columns. Pass <paramref name="keyValues"/> to filter to one row's primary key.
    /// </summary>
    /// <summary>
    /// Change events as a composable <see cref="IQueryable{T}"/> — layer LINQ
    /// (<c>Where</c> on Operation/ChangedBy/ChangedAt, <c>OrderBy</c>, <c>Take</c>,
    /// <c>Count</c>) and it translates to SQL wrapping <c>temporal.changes()</c>.
    /// Pass <paramref name="keyValues"/> to filter to one row's primary key.
    /// </summary>
    public IQueryable<TemporalChange> Changes(
        DateTimeOffset from, DateTimeOffset to, object[]? keyValues = null)
    {
        object pk = keyValues is null ? DBNull.Value : PrimaryKeyJson(keyValues);

        // SqlQueryRaw maps the result set to the unmapped TemporalChange record by
        // column name (jsonb→string via the cast, text[]→string[]) and returns a
        // composable IQueryable.
        var sql =
            "SELECT pk::text AS \"Pk\", changed_at AS \"ChangedAt\", changed_by AS \"ChangedBy\", " +
            "operation AS \"Operation\", old_row::text AS \"OldRow\", new_row::text AS \"NewRow\", " +
            "changed_columns AS \"ChangedColumns\" " +
            $"FROM temporal.changes({Regclass()}, {{0}}, {{1}}, {{2}}::jsonb)";

        return _context.Database.SqlQueryRaw<TemporalChange>(sql, from, to, pk);
    }

    /// <summary>Change events for a single-column primary key: <c>Changes(from, to, id)</c>.</summary>
    public IQueryable<TemporalChange> Changes(DateTimeOffset from, DateTimeOffset to, object key)
        => Changes(from, to, new[] { key });

    /// <summary>Materialized form of <see cref="Changes(DateTimeOffset, DateTimeOffset, object[])"/>, ordered by change time.</summary>
    public async Task<IReadOnlyList<TemporalChange>> GetChangesAsync(
        DateTimeOffset from, DateTimeOffset to, object[]? keyValues = null,
        CancellationToken cancellationToken = default)
        => await Changes(from, to, keyValues)
            .OrderBy(c => c.ChangedAt)
            .ToListAsync(cancellationToken);

    /// <summary>Materialized change events for a single-column primary key.</summary>
    public Task<IReadOnlyList<TemporalChange>> GetChangesAsync(
        DateTimeOffset from, DateTimeOffset to, object key,
        CancellationToken cancellationToken = default)
        => GetChangesAsync(from, to, new[] { key }, cancellationToken);

    // ── identity resolution ─────────────────────────────────────────────────

    private IEntityType EntityType()
        => _context.Model.FindEntityType(typeof(TEntity))
           ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped in this context.");

    /// <summary>'"schema"."table"'::regclass for the entity's mapped table.</summary>
    private string Regclass()
    {
        var et = EntityType();
        var table = et.GetTableName()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped to a table.");
        var schema = et.GetSchema();
        var ident = schema is null ? Quote(table) : Quote(schema) + "." + Quote(table);
        return "'" + ident.Replace("'", "''") + "'::regclass";
    }

    private string PrimaryKeyJson(object[] keyValues)
    {
        var et = EntityType();
        var pk = et.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no primary key.");
        if (keyValues.Length != pk.Properties.Count)
            throw new ArgumentException(
                $"Expected {pk.Properties.Count} key value(s) for {typeof(TEntity).Name}, got {keyValues.Length}.");

        var store = StoreObjectIdentifier.Table(et.GetTableName()!, et.GetSchema());
        var map = new Dictionary<string, object?>();
        for (var i = 0; i < pk.Properties.Count; i++)
        {
            var column = pk.Properties[i].GetColumnName(store) ?? pk.Properties[i].Name;
            map[column] = keyValues[i];
        }
        return JsonSerializer.Serialize(map);
    }

    private static string Quote(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
