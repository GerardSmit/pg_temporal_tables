namespace PgTemporalTables.EntityFrameworkCore;

/// <summary>
/// Annotation names used to carry pg_temporal_tables configuration through the
/// EF model and into migrations.
/// </summary>
public static class PgTemporalAnnotationNames
{
    /// <summary>Common prefix for all PgTemporal annotation keys.</summary>
    public const string Prefix = "PgTemporal:";

    /// <summary>bool — the table is tracked by pg_temporal_tables.</summary>
    public const string IsTemporal = Prefix + "IsTemporal";

    /// <summary>string — comma-separated column names excluded from history tracking.</summary>
    public const string ExcludedColumns = Prefix + "ExcludedColumns";

    /// <summary>string — combine bucket as a PostgreSQL interval literal (e.g. "00:00:05").</summary>
    public const string CombineInterval = Prefix + "CombineInterval";

    /// <summary>bool — mirror base-table indexes onto the history table.</summary>
    public const string IncludeIndexes = Prefix + "IncludeIndexes";

    /// <summary>string — explicit history table name (default: &lt;table&gt;__history).</summary>
    public const string HistoryTableName = Prefix + "HistoryTableName";

    /// <summary>string (model-level) — default combine interval for tables that don't set one.</summary>
    public const string DefaultCombineInterval = Prefix + "DefaultCombineInterval";

    /// <summary>bool (model-level) — default include-indexes for tables that don't set one.</summary>
    public const string DefaultIncludeIndexes = Prefix + "DefaultIncludeIndexes";

    /// <summary>string (model-level) — default history compression for tables that don't set one.</summary>
    public const string DefaultHistoryCompression = Prefix + "DefaultHistoryCompression";

    /// <summary>string (model-level) — default retention drop-after for tables that don't set one.</summary>
    public const string DefaultRetentionDropAfter = Prefix + "DefaultRetentionDropAfter";

    /// <summary>string (model-level) — default retention run-interval.</summary>
    public const string DefaultRetentionRunEvery = Prefix + "DefaultRetentionRunEvery";

    /// <summary>string (model-level) — default compaction run-interval for tables that don't set one.</summary>
    public const string DefaultCompactionRunEvery = Prefix + "DefaultCompactionRunEvery";

    /// <summary>bool — add a stable temporal_row_id lineage column.</summary>
    public const string TrackLineage = Prefix + "TrackLineage";

    /// <summary>string — history column compression method (pglz / lz4 / default).</summary>
    public const string HistoryCompression = Prefix + "HistoryCompression";

    /// <summary>string — retention policy: drop history older than this PostgreSQL interval.</summary>
    public const string RetentionDropAfter = Prefix + "RetentionDropAfter";

    /// <summary>string — retention policy run interval.</summary>
    public const string RetentionRunEvery = Prefix + "RetentionRunEvery";

    /// <summary>string — compaction policy run interval.</summary>
    public const string CompactionRunEvery = Prefix + "CompactionRunEvery";
}
