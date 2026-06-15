using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.Internal;

namespace PgTemporalTables.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Flows the PgTemporal:* entity annotations onto table operations so the
/// migrations model differ sees them and <see cref="TemporalMigrationsSqlGenerator"/>
/// can emit temporal.enable()/disable()/set_*() calls.
/// Subclasses Npgsql's annotation provider (an EF-internal-style API; pinned
/// to the referenced Npgsql.EntityFrameworkCore.PostgreSQL version).
/// </summary>
#pragma warning disable EF1001
public class TemporalAnnotationProvider : NpgsqlAnnotationProvider
{
    public TemporalAnnotationProvider(RelationalAnnotationProviderDependencies dependencies)
        : base(dependencies)
    {
    }

    public override IEnumerable<IAnnotation> For(ITable table, bool designTime)
    {
        foreach (var annotation in base.For(table, designTime))
            yield return annotation;

        foreach (var annotation in TemporalAnnotations(table))
            yield return annotation;
    }

    internal static IEnumerable<IAnnotation> TemporalAnnotations(ITable table)
    {
        var entityType = table.EntityTypeMappings.FirstOrDefault()?.TypeBase as IEntityType;
        if (entityType is null ||
            entityType.FindAnnotation(PgTemporalAnnotationNames.IsTemporal)?.Value is not true)
            yield break;

        yield return new Annotation(PgTemporalAnnotationNames.IsTemporal, true);

        // excluded columns: property names resolve to their mapped column
        // names here (the model is finalized); unknown names pass through as
        // raw column names
        if (entityType.FindAnnotation(PgTemporalAnnotationNames.ExcludedColumns)?.Value is string excluded &&
            excluded.Length > 0)
        {
            var storeObject = StoreObjectIdentifier.Table(table.Name, table.Schema);
            var columnNames = excluded
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => entityType.FindProperty(name)?.GetColumnName(storeObject) ?? name);
            yield return new Annotation(PgTemporalAnnotationNames.ExcludedColumns,
                string.Join(",", columnNames));
        }

        // per-table value wins over the model-wide default
        var combine = entityType.FindAnnotation(PgTemporalAnnotationNames.CombineInterval)?.Value
            ?? entityType.Model.FindAnnotation(PgTemporalAnnotationNames.DefaultCombineInterval)?.Value;
        if (combine is not null)
            yield return new Annotation(PgTemporalAnnotationNames.CombineInterval, combine);

        // each of these falls back to the model-wide default set via
        // ModelBuilder.UseTemporalDefaults(); a per-table value always wins
        var model = entityType.Model;

        var includeIndexes = entityType.FindAnnotation(PgTemporalAnnotationNames.IncludeIndexes)?.Value
            ?? model.FindAnnotation(PgTemporalAnnotationNames.DefaultIncludeIndexes)?.Value;
        if (includeIndexes is not null)
            yield return new Annotation(PgTemporalAnnotationNames.IncludeIndexes, includeIndexes);

        if (entityType.FindAnnotation(PgTemporalAnnotationNames.HistoryTableName)?.Value is { } history)
            yield return new Annotation(PgTemporalAnnotationNames.HistoryTableName, history);

        // lineage is per-table only (it adds a column / shadow property)
        if (entityType.FindAnnotation(PgTemporalAnnotationNames.TrackLineage)?.Value is true)
            yield return new Annotation(PgTemporalAnnotationNames.TrackLineage, true);

        var compression = entityType.FindAnnotation(PgTemporalAnnotationNames.HistoryCompression)?.Value
            ?? model.FindAnnotation(PgTemporalAnnotationNames.DefaultHistoryCompression)?.Value;
        if (compression is not null)
            yield return new Annotation(PgTemporalAnnotationNames.HistoryCompression, compression);

        var dropAfter = entityType.FindAnnotation(PgTemporalAnnotationNames.RetentionDropAfter)?.Value
            ?? model.FindAnnotation(PgTemporalAnnotationNames.DefaultRetentionDropAfter)?.Value;
        if (dropAfter is not null)
        {
            yield return new Annotation(PgTemporalAnnotationNames.RetentionDropAfter, dropAfter);
            var retRun = entityType.FindAnnotation(PgTemporalAnnotationNames.RetentionRunEvery)?.Value
                ?? model.FindAnnotation(PgTemporalAnnotationNames.DefaultRetentionRunEvery)?.Value;
            if (retRun is not null)
                yield return new Annotation(PgTemporalAnnotationNames.RetentionRunEvery, retRun);
        }

        var compRun = entityType.FindAnnotation(PgTemporalAnnotationNames.CompactionRunEvery)?.Value
            ?? model.FindAnnotation(PgTemporalAnnotationNames.DefaultCompactionRunEvery)?.Value;
        if (compRun is not null)
            yield return new Annotation(PgTemporalAnnotationNames.CompactionRunEvery, compRun);
    }
}
#pragma warning restore EF1001
