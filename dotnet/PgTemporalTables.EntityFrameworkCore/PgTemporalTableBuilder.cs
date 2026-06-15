using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace PgTemporalTables.EntityFrameworkCore;

/// <summary>
/// Configures pg_temporal_tables tracking for a table, mirroring the shape of
/// SQL Server's <c>TemporalTableBuilder</c>.
/// </summary>
public class PgTemporalTableBuilder
{
    internal IMutableEntityType EntityType { get; }

    internal PgTemporalTableBuilder(IMutableEntityType entityType)
        => EntityType = entityType;

    /// <summary>
    /// Properties or column names whose changes should NOT produce history
    /// records (noise like <c>UpdatedAt</c>, <c>LockedAt</c>). Property names
    /// are resolved to their mapped column names at migration time; unknown
    /// names are passed through as raw column names.
    /// </summary>
    public PgTemporalTableBuilder ExcludeColumns(params string[] propertyOrColumnNames)
    {
        EntityType.SetAnnotation(PgTemporalAnnotationNames.ExcludedColumns,
            string.Join(",", propertyOrColumnNames));
        return this;
    }

    /// <summary>
    /// Changes by the same <c>temporal.user_id</c> within this window are
    /// combined into a single history record (an ORM saving a form in many
    /// small UPDATEs produces one change). Overrides the model-wide default
    /// set via <c>modelBuilder.UseTemporalDefaults(...)</c>.
    /// </summary>
    public PgTemporalTableBuilder CombineInterval(TimeSpan interval)
    {
        EntityType.SetAnnotation(PgTemporalAnnotationNames.CombineInterval,
            interval.ToString("c"));
        return this;
    }

    /// <summary>
    /// Mirror the base table's indexes onto the history table (unique becomes
    /// non-unique), auto-synced on later CREATE/DROP INDEX.
    /// </summary>
    public PgTemporalTableBuilder IncludeIndexes(bool include = true)
    {
        EntityType.SetAnnotation(PgTemporalAnnotationNames.IncludeIndexes, include);
        return this;
    }

    /// <summary>Use an explicit history table instead of the default <c>&lt;table&gt;__history</c>.</summary>
    public PgTemporalTableBuilder UseHistoryTable(string name)
    {
        EntityType.SetAnnotation(PgTemporalAnnotationNames.HistoryTableName, name);
        return this;
    }

    /// <summary>
    /// Add a stable <c>temporal_row_id</c> lineage column so a row's history
    /// survives primary-key changes (a <see cref="TrackLineageProperty"/> shadow
    /// property is exposed). Off by default.
    /// </summary>
    public PgTemporalTableBuilder TrackLineage(bool track = true)
    {
        EntityType.SetAnnotation(PgTemporalAnnotationNames.TrackLineage, track);
        if (track)
        {
            var lineage = EntityType.FindProperty(PgTemporalTableBuilderExtensions.TemporalRowIdProperty)
                ?? EntityType.AddProperty(PgTemporalTableBuilderExtensions.TemporalRowIdProperty, typeof(long));
            lineage.SetColumnName("temporal_row_id");
            // assigned once by the sequence default at INSERT, read back via RETURNING
            lineage.ValueGenerated = ValueGenerated.OnAdd;
        }
        return this;
    }

    /// <summary>Set the history table's column compression (<c>pglz</c>, <c>lz4</c>, or <c>default</c>).</summary>
    public PgTemporalTableBuilder HistoryCompression(string method)
    {
        EntityType.SetAnnotation(PgTemporalAnnotationNames.HistoryCompression, method);
        return this;
    }

    /// <summary>
    /// Declare a retention policy executed by <c>temporal.run_due_policies()</c>:
    /// drop history older than <paramref name="dropAfter"/>, checked at most every
    /// <paramref name="runEvery"/> (default 1 day). Scheduling the runner is an ops concern.
    /// </summary>
    public PgTemporalTableBuilder WithRetentionPolicy(TimeSpan dropAfter, TimeSpan? runEvery = null)
    {
        EntityType.SetAnnotation(PgTemporalAnnotationNames.RetentionDropAfter, dropAfter.ToString("c"));
        EntityType.SetAnnotation(PgTemporalAnnotationNames.RetentionRunEvery,
            (runEvery ?? TimeSpan.FromDays(1)).ToString("c"));
        return this;
    }

    /// <summary>Declare a compaction policy run at most every <paramref name="runEvery"/> (default 1 day).</summary>
    public PgTemporalTableBuilder WithCompactionPolicy(TimeSpan? runEvery = null)
    {
        EntityType.SetAnnotation(PgTemporalAnnotationNames.CompactionRunEvery,
            (runEvery ?? TimeSpan.FromDays(1)).ToString("c"));
        return this;
    }
}

/// <summary>Strongly-typed variant with expression-based column exclusion.</summary>
public class PgTemporalTableBuilder<TEntity> : PgTemporalTableBuilder
    where TEntity : class
{
    internal PgTemporalTableBuilder(IMutableEntityType entityType)
        : base(entityType)
    {
    }

    /// <summary>
    /// Exclude properties from history tracking:
    /// <c>tt.ExcludeColumns(t =&gt; new { t.UpdatedAt, t.LockedAt })</c>
    /// or <c>tt.ExcludeColumns(t =&gt; t.UpdatedAt)</c>.
    /// </summary>
    public PgTemporalTableBuilder<TEntity> ExcludeColumns(
        Expression<Func<TEntity, object?>> propertiesExpression)
    {
        var names = ExtractPropertyNames(propertiesExpression.Body);
        EntityType.SetAnnotation(PgTemporalAnnotationNames.ExcludedColumns,
            string.Join(",", names));
        return this;
    }

    public new PgTemporalTableBuilder<TEntity> ExcludeColumns(params string[] propertyOrColumnNames)
    {
        base.ExcludeColumns(propertyOrColumnNames);
        return this;
    }

    public new PgTemporalTableBuilder<TEntity> CombineInterval(TimeSpan interval)
    {
        base.CombineInterval(interval);
        return this;
    }

    public new PgTemporalTableBuilder<TEntity> IncludeIndexes(bool include = true)
    {
        base.IncludeIndexes(include);
        return this;
    }

    public new PgTemporalTableBuilder<TEntity> UseHistoryTable(string name)
    {
        base.UseHistoryTable(name);
        return this;
    }

    public new PgTemporalTableBuilder<TEntity> TrackLineage(bool track = true)
    {
        base.TrackLineage(track);
        return this;
    }

    public new PgTemporalTableBuilder<TEntity> HistoryCompression(string method)
    {
        base.HistoryCompression(method);
        return this;
    }

    public new PgTemporalTableBuilder<TEntity> WithRetentionPolicy(TimeSpan dropAfter, TimeSpan? runEvery = null)
    {
        base.WithRetentionPolicy(dropAfter, runEvery);
        return this;
    }

    public new PgTemporalTableBuilder<TEntity> WithCompactionPolicy(TimeSpan? runEvery = null)
    {
        base.WithCompactionPolicy(runEvery);
        return this;
    }

    private static IEnumerable<string> ExtractPropertyNames(Expression body)
    {
        body = StripConvert(body);

        switch (body)
        {
            case NewExpression anonymous:        // t => new { t.A, t.B }
                return anonymous.Arguments.Select(a => MemberName(StripConvert(a)));
            case MemberExpression member:        // t => t.A
                return [member.Member.Name];
            default:
                throw new ArgumentException(
                    "ExcludeColumns expects a property access (t => t.UpdatedAt) " +
                    "or an anonymous type of property accesses (t => new { t.UpdatedAt, t.LockedAt }).");
        }
    }

    private static Expression StripConvert(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u
            ? u.Operand
            : e;

    private static string MemberName(Expression e)
        => e is MemberExpression m
            ? m.Member.Name
            : throw new ArgumentException($"Expected a property access, got: {e}");
}
