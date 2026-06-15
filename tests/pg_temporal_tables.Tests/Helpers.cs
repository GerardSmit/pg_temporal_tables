namespace PgTemporalTables.Tests;

/// <summary>
/// Test-side helpers that are not bound to a fixture or connection.
/// </summary>
internal static class Helpers
{
    public static string PgMajor =>
        Environment.GetEnvironmentVariable("PG_TEMPORAL_PG_VERSION") ?? "18";

    public static string PostgresImage => $"postgres:{PgMajor}";
}
