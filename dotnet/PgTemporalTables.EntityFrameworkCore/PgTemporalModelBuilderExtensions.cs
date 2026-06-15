using Microsoft.EntityFrameworkCore;

namespace PgTemporalTables.EntityFrameworkCore;

/// <summary>Model-wide pg_temporal_tables defaults.</summary>
public static class PgTemporalModelBuilderExtensions
{
    /// <summary>
    /// Sets model-wide defaults applied to every <c>IsTemporal()</c> table that
    /// does not configure its own value:
    /// <code>
    /// modelBuilder.UseTemporalDefaults(
    ///     combineInterval: TimeSpan.FromSeconds(5),
    ///     historyCompression: "lz4",
    ///     includeIndexes: true);
    /// </code>
    /// A per-table setting always overrides the model default. (Lineage is
    /// per-table only — it adds a column/shadow property — via
    /// <c>tt.TrackLineage()</c>.)
    /// </summary>
    public static ModelBuilder UseTemporalDefaults(this ModelBuilder modelBuilder,
        TimeSpan? combineInterval = null,
        bool? includeIndexes = null,
        string? historyCompression = null,
        TimeSpan? retentionDropAfter = null,
        TimeSpan? retentionRunEvery = null,
        TimeSpan? compactionRunEvery = null)
    {
        var model = modelBuilder.Model;

        if (combineInterval is { } interval)
            model.SetAnnotation(PgTemporalAnnotationNames.DefaultCombineInterval, interval.ToString("c"));

        if (includeIndexes is { } idx)
            model.SetAnnotation(PgTemporalAnnotationNames.DefaultIncludeIndexes, idx);

        if (historyCompression is { } compression)
            model.SetAnnotation(PgTemporalAnnotationNames.DefaultHistoryCompression, compression);

        if (retentionDropAfter is { } drop)
        {
            model.SetAnnotation(PgTemporalAnnotationNames.DefaultRetentionDropAfter, drop.ToString("c"));
            model.SetAnnotation(PgTemporalAnnotationNames.DefaultRetentionRunEvery,
                (retentionRunEvery ?? TimeSpan.FromDays(1)).ToString("c"));
        }

        if (compactionRunEvery is { } compRun)
            model.SetAnnotation(PgTemporalAnnotationNames.DefaultCompactionRunEvery, compRun.ToString("c"));

        return modelBuilder;
    }
}
