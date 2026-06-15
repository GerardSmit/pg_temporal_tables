using PgTemporalTables.Tests.Fixtures;
using Xunit;

// Register the shared PostgreSQL container fixture for this assembly.
// PgTemporalFixture and TemporalDb are compiled from the referenced
// pg_temporal_tables.Tests project; the [AssemblyFixture] must be declared
// here so xunit.v3 creates one instance per assembly.
[assembly: AssemblyFixture(typeof(PgTemporalFixture))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]
