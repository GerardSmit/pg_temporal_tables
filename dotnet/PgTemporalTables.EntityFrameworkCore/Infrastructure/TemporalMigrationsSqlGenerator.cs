using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace PgTemporalTables.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Extends the Npgsql migrations SQL generator with pg_temporal_tables DDL:
/// CREATE TABLE on a temporal entity is followed by temporal.enable(...),
/// DROP TABLE is preceded by temporal.disable(...), and AlterTable handles
/// turning tracking on/off and changing its settings.
/// </summary>
public class TemporalMigrationsSqlGenerator : NpgsqlMigrationsSqlGenerator
{
#pragma warning disable EF1001
    public TemporalMigrationsSqlGenerator(
        MigrationsSqlGeneratorDependencies dependencies,
        INpgsqlSingletonOptions npgsqlSingletonOptions)
        : base(dependencies, npgsqlSingletonOptions)
    {
    }
#pragma warning restore EF1001

    protected override void Generate(CreateTableOperation operation, IModel? model,
        MigrationCommandListBuilder builder, bool terminate = true)
    {
        base.Generate(operation, model, builder, terminate);

        if (IsTemporal(operation))
        {
            builder
                .AppendLine("CREATE EXTENSION IF NOT EXISTS pg_temporal_tables;")
                .EndCommand();
            AppendEnable(builder, operation.Name, operation.Schema, operation);
        }
    }

    protected override void Generate(DropTableOperation operation, IModel? model,
        MigrationCommandListBuilder builder, bool terminate = true)
    {
        if (IsTemporal(operation))
        {
            // the versions view depends on the table; disable() removes it
            builder
                .Append("SELECT temporal.disable(")
                .Append(TableLiteral(operation.Name, operation.Schema))
                .AppendLine(");")
                .EndCommand();
        }

        base.Generate(operation, model, builder, terminate);
    }

    protected override void Generate(AlterTableOperation operation, IModel? model,
        MigrationCommandListBuilder builder)
    {
        base.Generate(operation, model, builder);

        var wasTemporal = operation.OldTable[PgTemporalAnnotationNames.IsTemporal] is true;
        var isTemporal = IsTemporal(operation);

        if (!wasTemporal && isTemporal)
        {
            builder
                .AppendLine("CREATE EXTENSION IF NOT EXISTS pg_temporal_tables;")
                .EndCommand();
            AppendEnable(builder, operation.Name, operation.Schema, operation);
        }
        else if (wasTemporal && !isTemporal)
        {
            builder
                .Append("SELECT temporal.disable(")
                .Append(TableLiteral(operation.Name, operation.Schema))
                .AppendLine(");")
                .EndCommand();
        }
        else if (wasTemporal && isTemporal)
        {
            AppendSettingChanges(builder, operation);
        }
    }

    private static bool IsTemporal(MigrationOperation operation)
        => operation[PgTemporalAnnotationNames.IsTemporal] is true;

    private void AppendEnable(MigrationCommandListBuilder builder,
        string table, string? schema, MigrationOperation operation)
    {
        var sql = new StringBuilder();
        sql.Append("SELECT temporal.enable(").Append(TableLiteral(table, schema));

        if (operation[PgTemporalAnnotationNames.ExcludedColumns] is string excluded &&
            excluded.Length > 0)
            sql.Append(", excluded_columns => ").Append(ExcludedColumnsLiteral(excluded));

        if (operation[PgTemporalAnnotationNames.CombineInterval] is string interval)
            sql.Append(", combine_interval => ").Append(IntervalLiteral(interval));

        if (operation[PgTemporalAnnotationNames.IncludeIndexes] is true)
            sql.Append(", include_indexes => true");

        if (operation[PgTemporalAnnotationNames.HistoryTableName] is string history)
            sql.Append(", history_table => ").Append(TableLiteral(history, schema));

        if (operation[PgTemporalAnnotationNames.TrackLineage] is true)
            sql.Append(", track_lineage => true");

        sql.Append(");");
        builder.AppendLine(sql.ToString()).EndCommand();

        AppendExtras(builder, table, schema, operation);
    }

    /// <summary>Compression + retention/compaction policy calls that follow enable().</summary>
    private void AppendExtras(MigrationCommandListBuilder builder,
        string table, string? schema, MigrationOperation operation)
    {
        var tbl = TableLiteral(table, schema);

        if (operation[PgTemporalAnnotationNames.HistoryCompression] is string method && method.Length > 0)
            builder
                .AppendLine($"SELECT temporal.set_history_compression({tbl}, '{method.Replace("'", "''")}');")
                .EndCommand();

        if (operation[PgTemporalAnnotationNames.RetentionDropAfter] is string dropAfter)
        {
            var sql = new StringBuilder();
            sql.Append($"SELECT temporal.add_retention_policy({tbl}, p_drop_after => ")
               .Append(IntervalLiteral(dropAfter));
            if (operation[PgTemporalAnnotationNames.RetentionRunEvery] is string runEvery)
                sql.Append(", p_run_every => ").Append(IntervalLiteral(runEvery));
            sql.Append(");");
            builder.AppendLine(sql.ToString()).EndCommand();
        }

        if (operation[PgTemporalAnnotationNames.CompactionRunEvery] is string compRun)
            builder
                .AppendLine($"SELECT temporal.add_compaction_policy({tbl}, p_run_every => {IntervalLiteral(compRun)});")
                .EndCommand();
    }

    private void AppendSettingChanges(MigrationCommandListBuilder builder, AlterTableOperation operation)
    {
        var table = TableLiteral(operation.Name, operation.Schema);

        var oldExcluded = operation.OldTable[PgTemporalAnnotationNames.ExcludedColumns] as string;
        var newExcluded = operation[PgTemporalAnnotationNames.ExcludedColumns] as string;
        if (oldExcluded != newExcluded)
            builder
                .Append($"SELECT temporal.set_excluded_columns({table}, ")
                .Append(ExcludedColumnsLiteral(newExcluded ?? ""))
                .AppendLine(");")
                .EndCommand();

        var oldInterval = operation.OldTable[PgTemporalAnnotationNames.CombineInterval] as string;
        var newInterval = operation[PgTemporalAnnotationNames.CombineInterval] as string;
        if (oldInterval != newInterval && newInterval is not null)
            builder
                .Append($"SELECT temporal.set_combine_interval({table}, ")
                .Append(IntervalLiteral(newInterval))
                .AppendLine(");")
                .EndCommand();

        var oldIndexes = operation.OldTable[PgTemporalAnnotationNames.IncludeIndexes] is true;
        var newIndexes = operation[PgTemporalAnnotationNames.IncludeIndexes] is true;
        if (oldIndexes != newIndexes)
            builder
                .AppendLine($"SELECT temporal.set_include_indexes({table}, {(newIndexes ? "true" : "false")});")
                .EndCommand();

        var oldComp = operation.OldTable[PgTemporalAnnotationNames.HistoryCompression] as string;
        var newComp = operation[PgTemporalAnnotationNames.HistoryCompression] as string;
        if (oldComp != newComp && newComp is not null)
            builder
                .AppendLine($"SELECT temporal.set_history_compression({table}, '{newComp.Replace("'", "''")}');")
                .EndCommand();

        var oldDrop = operation.OldTable[PgTemporalAnnotationNames.RetentionDropAfter] as string;
        var newDrop = operation[PgTemporalAnnotationNames.RetentionDropAfter] as string;
        var newRetRun = operation[PgTemporalAnnotationNames.RetentionRunEvery] as string;
        var oldRetRun = operation.OldTable[PgTemporalAnnotationNames.RetentionRunEvery] as string;
        if (oldDrop != newDrop || oldRetRun != newRetRun)
        {
            if (newDrop is not null)
            {
                var sql = new StringBuilder();
                sql.Append($"SELECT temporal.add_retention_policy({table}, p_drop_after => ")
                   .Append(IntervalLiteral(newDrop));
                if (newRetRun is not null)
                    sql.Append(", p_run_every => ").Append(IntervalLiteral(newRetRun));
                sql.Append(");");
                builder.AppendLine(sql.ToString()).EndCommand();
            }
            else
                builder.AppendLine($"SELECT temporal.remove_retention_policy({table});").EndCommand();
        }

        var oldCompRun = operation.OldTable[PgTemporalAnnotationNames.CompactionRunEvery] as string;
        var newCompRun = operation[PgTemporalAnnotationNames.CompactionRunEvery] as string;
        if (oldCompRun != newCompRun)
        {
            if (newCompRun is not null)
                builder
                    .AppendLine($"SELECT temporal.add_compaction_policy({table}, p_run_every => {IntervalLiteral(newCompRun)});")
                    .EndCommand();
            else
                builder.AppendLine($"SELECT temporal.remove_compaction_policy({table});").EndCommand();
        }
    }

    /// <summary>'"schema"."Table"' — a quoted regclass literal.</summary>
    private string TableLiteral(string table, string? schema)
    {
        var qualified = Dependencies.SqlGenerationHelper.DelimitIdentifier(table, schema);
        return "'" + qualified.Replace("'", "''") + "'";
    }

    private static string ExcludedColumnsLiteral(string commaSeparated)
    {
        var names = commaSeparated
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => "'" + n.Replace("'", "''") + "'");
        return "ARRAY[" + string.Join(", ", names) + "]::name[]";
    }

    private static string IntervalLiteral(string timeSpan)
    {
        // stored as TimeSpan invariant "c" format (e.g. "00:00:05"), which is
        // a valid PostgreSQL interval literal
        var ts = TimeSpan.Parse(timeSpan, CultureInfo.InvariantCulture);
        return "interval '" + ts.ToString("c", CultureInfo.InvariantCulture) + "'";
    }
}
