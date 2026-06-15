using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PgTemporalTables.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Supplies the PgTemporal:* annotations for REMOVED tables, so a
/// DropTableOperation on a temporal table carries enough information for the
/// SQL generator to call temporal.disable() before the DROP (the versions
/// view dependency would otherwise block it).
/// </summary>
public class TemporalMigrationsAnnotationProvider : MigrationsAnnotationProvider
{
    /// <summary>Initializes a new instance with the given migrations annotation provider dependencies.</summary>
    public TemporalMigrationsAnnotationProvider(MigrationsAnnotationProviderDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc/>
    public override IEnumerable<IAnnotation> ForRemove(ITable table)
    {
        foreach (var annotation in base.ForRemove(table))
            yield return annotation;

        foreach (var annotation in TemporalAnnotationProvider.TemporalAnnotations(table))
            yield return annotation;
    }
}
