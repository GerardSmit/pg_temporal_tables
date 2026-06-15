using System.Globalization;
using Microsoft.EntityFrameworkCore.Metadata;

namespace PgTemporalTables.EntityFrameworkCore;

/// <summary>
/// Metadata accessors for pg_temporal_tables configuration, mirroring the
/// shape of SQL Server's <c>SqlServerEntityTypeExtensions</c>.
/// </summary>
public static class PgTemporalEntityTypeExtensions
{
    /// <summary>Whether the entity's table is tracked by pg_temporal_tables.</summary>
    public static bool IsTemporal(this IReadOnlyEntityType entityType)
        => entityType.FindAnnotation(PgTemporalAnnotationNames.IsTemporal)?.Value is true;

    /// <summary>Marks or unmarks the entity's table as tracked by pg_temporal_tables.</summary>
    public static void SetIsTemporal(this IMutableEntityType entityType, bool temporal)
        => entityType.SetAnnotation(PgTemporalAnnotationNames.IsTemporal, temporal);

    /// <summary>The configured excluded property/column names, if any.</summary>
    public static IReadOnlyList<string> GetTemporalExcludedColumns(this IReadOnlyEntityType entityType)
        => entityType.FindAnnotation(PgTemporalAnnotationNames.ExcludedColumns)?.Value is string s
            ? s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>Sets the property or column names to exclude from history tracking.</summary>
    public static void SetTemporalExcludedColumns(this IMutableEntityType entityType,
        IEnumerable<string> propertyOrColumnNames)
        => entityType.SetAnnotation(PgTemporalAnnotationNames.ExcludedColumns,
            string.Join(",", propertyOrColumnNames));

    /// <summary>
    /// The table's combine interval, falling back to the model-wide default
    /// set via <c>modelBuilder.UseTemporalDefaults(...)</c>.
    /// </summary>
    public static TimeSpan? GetTemporalCombineInterval(this IReadOnlyEntityType entityType)
    {
        var value = entityType.FindAnnotation(PgTemporalAnnotationNames.CombineInterval)?.Value
            ?? entityType.Model.FindAnnotation(PgTemporalAnnotationNames.DefaultCombineInterval)?.Value;
        return value is string s ? TimeSpan.Parse(s, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Sets the combine interval for this entity's temporal history table.</summary>
    public static void SetTemporalCombineInterval(this IMutableEntityType entityType, TimeSpan? interval)
        => entityType.SetAnnotation(PgTemporalAnnotationNames.CombineInterval,
            interval?.ToString("c"));

    /// <summary>Whether base-table indexes are mirrored onto the history table.</summary>
    public static bool GetTemporalIncludeIndexes(this IReadOnlyEntityType entityType)
        => entityType.FindAnnotation(PgTemporalAnnotationNames.IncludeIndexes)?.Value is true;

    /// <summary>Sets whether base-table indexes should be mirrored onto the history table.</summary>
    public static void SetTemporalIncludeIndexes(this IMutableEntityType entityType, bool include)
        => entityType.SetAnnotation(PgTemporalAnnotationNames.IncludeIndexes, include);

    /// <summary>The explicit history table name, or null for the default <c>&lt;table&gt;__history</c>.</summary>
    public static string? GetTemporalHistoryTableName(this IReadOnlyEntityType entityType)
        => entityType.FindAnnotation(PgTemporalAnnotationNames.HistoryTableName)?.Value as string;

    /// <summary>Sets an explicit history table name, overriding the default <c>&lt;table&gt;__history</c> naming.</summary>
    public static void SetTemporalHistoryTableName(this IMutableEntityType entityType, string? name)
        => entityType.SetAnnotation(PgTemporalAnnotationNames.HistoryTableName, name);
}
