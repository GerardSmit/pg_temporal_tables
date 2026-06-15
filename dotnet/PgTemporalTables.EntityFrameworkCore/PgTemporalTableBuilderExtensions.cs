using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PgTemporalTables.EntityFrameworkCore;

/// <summary>
/// Fluent model-building API for pg_temporal_tables, mirroring SQL Server's
/// <c>.ToTable("Customers", t =&gt; t.IsTemporal())</c>.
/// </summary>
public static class PgTemporalTableBuilderExtensions
{
    /// <summary>Shadow property exposing when the current row version was made (column <c>valid_from</c>).</summary>
    public const string ValidFromProperty = "ValidFrom";

    /// <summary>Shadow property exposing who made the current row version (column <c>changed_by</c>).</summary>
    public const string ChangedByProperty = "ChangedBy";

    /// <summary>Shadow property exposing the stable lineage id (column <c>temporal_row_id</c>), present only when <c>TrackLineage()</c> is set.</summary>
    public const string TemporalRowIdProperty = "TemporalRowId";

    /// <summary>
    /// Marks the table as tracked by pg_temporal_tables. Migrations emit
    /// <c>SELECT temporal.enable(...)</c> after creating the table; shadow
    /// properties <see cref="ValidFromProperty"/> and <see cref="ChangedByProperty"/>
    /// are added (store-generated, never written by SaveChanges).
    /// </summary>
    public static TableBuilder IsTemporal(this TableBuilder tableBuilder,
        Action<PgTemporalTableBuilder>? buildAction = null)
    {
        ConfigureTemporal(tableBuilder.Metadata, buildAction);
        return tableBuilder;
    }

    /// <inheritdoc cref="IsTemporal(TableBuilder, Action{PgTemporalTableBuilder}?)"/>
    public static TableBuilder<TEntity> IsTemporal<TEntity>(this TableBuilder<TEntity> tableBuilder,
        Action<PgTemporalTableBuilder<TEntity>>? buildAction = null)
        where TEntity : class
    {
        ConfigureTemporalCore(tableBuilder.Metadata);
        buildAction?.Invoke(new PgTemporalTableBuilder<TEntity>(tableBuilder.Metadata));
        return tableBuilder;
    }

    /// <summary>Convenience overload directly on the entity type builder.</summary>
    public static EntityTypeBuilder IsTemporal(this EntityTypeBuilder entityTypeBuilder,
        Action<PgTemporalTableBuilder>? buildAction = null)
    {
        ConfigureTemporal(entityTypeBuilder.Metadata, buildAction);
        return entityTypeBuilder;
    }

    /// <inheritdoc cref="IsTemporal(EntityTypeBuilder, Action{PgTemporalTableBuilder}?)"/>
    public static EntityTypeBuilder<TEntity> IsTemporal<TEntity>(this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Action<PgTemporalTableBuilder<TEntity>>? buildAction = null)
        where TEntity : class
    {
        ConfigureTemporalCore(entityTypeBuilder.Metadata);
        buildAction?.Invoke(new PgTemporalTableBuilder<TEntity>(entityTypeBuilder.Metadata));
        return entityTypeBuilder;
    }

    private static void ConfigureTemporal(IMutableEntityType entityType,
        Action<PgTemporalTableBuilder>? buildAction)
    {
        ConfigureTemporalCore(entityType);
        buildAction?.Invoke(new PgTemporalTableBuilder(entityType));
    }

    private static void ConfigureTemporalCore(IMutableEntityType entityType)
    {
        entityType.SetAnnotation(PgTemporalAnnotationNames.IsTemporal, true);

        // Register PgTemporal.AsOf as a model DbFunction mapped to
        // temporal.as_of(timestamptz). Model registration both translates the
        // call and stops the funcletizer from client-evaluating it.
        var model = entityType.Model;
        var asOfMethod = typeof(PgTemporal).GetMethod(nameof(PgTemporal.AsOf))!;
        if (model.FindDbFunction(asOfMethod) is null)
        {
            var dbFunction = model.AddDbFunction(asOfMethod);
            dbFunction.Name = "as_of";
            dbFunction.Schema = "temporal";
            dbFunction.Parameters[0].StoreType = "timestamp with time zone";
        }

        // valid_from / changed_by are maintained by the extension's triggers.
        // ValueGeneratedOnAddOrUpdate => EF never includes them in INSERT/UPDATE
        // (BeforeSaveBehavior/AfterSaveBehavior default to Ignore), but they are
        // created by migrations and readable via EF.Property<T>(e, ...).
        var validFrom = entityType.FindProperty(ValidFromProperty)
            ?? entityType.AddProperty(ValidFromProperty, typeof(DateTime));
        validFrom.SetColumnName("valid_from");
        validFrom.ValueGenerated = ValueGenerated.OnAddOrUpdate;

        var changedBy = entityType.FindProperty(ChangedByProperty)
            ?? entityType.AddProperty(ChangedByProperty, typeof(string));
        changedBy.SetColumnName("changed_by");
        changedBy.IsNullable = true;
        changedBy.ValueGenerated = ValueGenerated.OnAddOrUpdate;
    }
}
