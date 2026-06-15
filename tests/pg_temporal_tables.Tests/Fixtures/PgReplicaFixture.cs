using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Xunit;

namespace PgTemporalTables.Tests.Fixtures;

/// <summary>
/// Manages a two-node primary/standby PostgreSQL cluster for replication tests.
/// Both nodes run the pg_temporal_tables image built by <see cref="CachedTemporalImage"/>.
/// The standby is bootstrapped via pg_basebackup exactly like the reference
/// FailoverSlotTests project — no logical slots needed; plain physical streaming.
/// </summary>
/// <remarks>
/// Implement <see cref="IAsyncLifetime"/> on the consuming test class and forward
/// to this fixture, or declare it as an assembly-scoped fixture when sharing
/// across multiple test classes.
/// </remarks>
public sealed class PgReplicaFixture : IAsyncLifetime
{
    private const string PostgresUser = "postgres";
    private const string PostgresPassword = "postgres";
    private const string PostgresDb = "postgres";

    private INetwork _network = null!;
    private IContainer _primary = null!;
    private IContainer _standby = null!;

    // -------------------------------------------------------------------------
    // Public surface
    // -------------------------------------------------------------------------

    public string PrimaryConnectionString =>
        BuildConnectionString(_primary, PostgresDb);

    public string StandbyConnectionString =>
        BuildConnectionString(_standby, PostgresDb);

    // -------------------------------------------------------------------------
    // IAsyncLifetime
    // -------------------------------------------------------------------------

    public async ValueTask InitializeAsync()
    {
        var image = await CachedTemporalImage.BuildAsync(
            Helpers.PostgresImage, Helpers.PgMajor);

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync(TestContext.Current.CancellationToken);

        _primary = BuildPrimary(image, _network);
        await _primary.StartAsync(TestContext.Current.CancellationToken);
        await WaitForReadyConnectionAsync(_primary, "primary");

        // Allow replication connections (trust) then reload
        await ShellOrThrowAsync(_primary,
            "echo 'host replication all all trust' >> \"$PGDATA/pg_hba.conf\" && " +
            "su postgres -c \"pg_ctl -D '$PGDATA' reload\"",
            "enable replication host auth on primary");

        _standby = BuildStandby(image, _network);
        await _standby.StartAsync(TestContext.Current.CancellationToken);
        await WaitForReadyConnectionAsync(_standby, "standby");
    }

    public async ValueTask DisposeAsync()
    {
        if (_standby is not null) await _standby.DisposeAsync();
        if (_primary is not null) await _primary.DisposeAsync();
        if (_network is not null) await _network.DeleteAsync();
    }

    // -------------------------------------------------------------------------
    // Convenience helpers for tests
    // -------------------------------------------------------------------------

    public async Task<NpgsqlConnection> OpenPrimaryAsync() =>
        await OpenAsync(PrimaryConnectionString);

    public async Task<NpgsqlConnection> OpenStandbyAsync() =>
        await OpenAsync(StandbyConnectionString);

    /// <summary>
    /// Polls the standby until its replay LSN reaches or exceeds <paramref name="lsn"/>.
    /// </summary>
    public async Task WaitForStandbyToReachLsnAsync(string lsn, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = await OpenStandbyAsync();
                var caught = await ScalarAsync<bool>(conn,
                    "SELECT pg_last_wal_replay_lsn() >= $1::pg_lsn",
                    lsn);
                if (caught) return;
            }
            catch
            {
                // standby may not be fully started yet
            }
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException(
            $"Standby did not replay up to LSN {lsn} within {timeoutSeconds}s");
    }

    // -------------------------------------------------------------------------
    // Container builders
    // -------------------------------------------------------------------------

    private static IContainer BuildPrimary(string image, INetwork network) =>
        new ContainerBuilder(image)
            .WithNetwork(network)
            .WithNetworkAliases("pg-primary")
            .WithPortBinding(5432, true)
            .WithEnvironment("POSTGRES_USER", PostgresUser)
            .WithEnvironment("POSTGRES_PASSWORD", PostgresPassword)
            .WithEnvironment("POSTGRES_DB", PostgresDb)
            .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust")
            .WithCommand(
                "postgres",
                "-c", "shared_preload_libraries=pg_temporal_tables",
                "-c", "wal_level=replica",
                "-c", "max_wal_senders=10",
                "-c", "wal_keep_size=64",
                "-c", "listen_addresses=*")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .AddCustomWaitStrategy(new ImmediateReadyWait()))
            .Build();

    private static IContainer BuildStandby(string image, INetwork network)
    {
        // Bootstrap via pg_basebackup then launch postgres with the temporal
        // extension loaded and hot_standby enabled.  Adapted from the reference
        // FailoverSlotTests.BuildStandby() — no logical slots needed here.
        var command =
            "set -e; " +
            "mkdir -p \"$PGDATA\"; " +
            "rm -rf \"$PGDATA\"/*; " +
            "chown -R postgres:postgres \"$PGDATA\"; " +
            "until pg_isready -h pg-primary -U postgres -d postgres; do sleep 1; done; " +
            "su postgres -c \"PGPASSWORD=postgres pg_basebackup " +
            "-h pg-primary -U postgres -D '$PGDATA' -Fp -Xs -R\"; " +
            "cat >> \"$PGDATA/postgresql.auto.conf\" <<'EOF'\n" +
            "hot_standby = on\n" +
            "primary_conninfo = 'host=pg-primary port=5432 user=postgres password=postgres dbname=postgres application_name=standby'\n" +
            "EOF\n" +
            "chown -R postgres:postgres \"$PGDATA\"; " +
            "chmod 700 \"$PGDATA\"; " +
            "bindir=$(pg_config --bindir); " +
            "exec su postgres -c \"$bindir/postgres -D '$PGDATA' " +
            "-c shared_preload_libraries=pg_temporal_tables " +
            "-c hot_standby=on " +
            "-c listen_addresses=*\"";

        return new ContainerBuilder(image)
            .WithNetwork(network)
            .WithNetworkAliases("pg-standby")
            .WithPortBinding(5432, true)
            .WithEnvironment("POSTGRES_USER", PostgresUser)
            .WithEnvironment("POSTGRES_PASSWORD", PostgresPassword)
            .WithEnvironment("POSTGRES_DB", PostgresDb)
            .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust")
            .WithCommand("bash", "-lc", command)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .AddCustomWaitStrategy(new ImmediateReadyWait()))
            .Build();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static string BuildConnectionString(IContainer container, string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = container.GetMappedPublicPort(5432),
            Username = PostgresUser,
            Password = PostgresPassword,
            Database = database,
            Pooling = false,
            IncludeErrorDetail = true,
            Timeout = 10,
        }.ConnectionString;

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    private static async Task WaitForReadyConnectionAsync(IContainer container, string label)
    {
        var cs = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = container.GetMappedPublicPort(5432),
            Username = PostgresUser,
            Password = PostgresPassword,
            Database = PostgresDb,
            Pooling = false,
            Timeout = 5,
        }.ConnectionString;

        var deadline = DateTime.UtcNow.AddSeconds(120);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = new NpgsqlConnection(cs);
                await conn.OpenAsync(TestContext.Current.CancellationToken);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                return;
            }
            catch (Exception e)
            {
                last = e;
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }
        }

        string logs;
        try
        {
            var (stdout, stderr) = await container.GetLogsAsync();
            logs = $"\n--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}";
        }
        catch (Exception le)
        {
            logs = $"\n(could not fetch logs: {le.Message})";
        }

        throw new InvalidOperationException(
            $"PostgreSQL {label} was not ready in time{logs}", last);
    }

    internal static async Task ShellOrThrowAsync(
        IContainer container, string command, string label)
    {
        var result = await container.ExecAsync(["sh", "-c", command]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"{label} failed (exit {result.ExitCode}): " +
                $"stdout={result.Stdout} stderr={result.Stderr}");
    }

    internal static async Task<T> ScalarAsync<T>(
        NpgsqlConnection conn,
        string sql,
        params string[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters)
            cmd.Parameters.AddWithValue(p);
        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (T)result!;
    }
}
