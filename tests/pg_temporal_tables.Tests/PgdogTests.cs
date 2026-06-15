using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using PgTemporalTables.Tests.Fixtures;
using Xunit;

namespace PgTemporalTables.Tests;

/// <summary>
/// Verifies that pg_temporal_tables history and time-travel work when accessed
/// through a pgdog proxy (ghcr.io/pgdogdev/pgdog:latest) routing to either the
/// primary or a hot standby.
/// </summary>
/// <remarks>
/// The cluster bootstrap follows the same pattern as the reference PgdogTests.cs
/// in the pgBackupDatabase project.  No skip logic is added — the test just fails
/// if the pgdog image cannot be pulled, matching the reference behaviour.
/// </remarks>
public sealed class PgdogTests : IAsyncLifetime
{
    private const string PgdogImage = "ghcr.io/pgdogdev/pgdog:latest";
    private const string PostgresUser = "postgres";
    private const string PostgresPassword = "postgres";
    private const string PostgresDb = "postgres";

    // Each test uses its own database name so the suite can run independently
    // even if parallelisation is relaxed in future.
    private static string NewDbName() => "temporal_" + Guid.NewGuid().ToString("N")[..8];

    private string _image = null!;
    private INetwork _network = null!;
    private IContainer _primary = null!;
    private IContainer _standby = null!;

    // -------------------------------------------------------------------------
    // IAsyncLifetime — start primary + standby cluster once for the class
    // -------------------------------------------------------------------------

    public async ValueTask InitializeAsync()
    {
        _image = await CachedTemporalImage.BuildAsync(
            Helpers.PostgresImage, Helpers.PgMajor);

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync(TestContext.Current.CancellationToken);

        _primary = BuildPrimary(_image, _network);
        await _primary.StartAsync(TestContext.Current.CancellationToken);
        await WaitForReadyConnectionAsync(_primary, "primary");

        await PgReplicaFixture.ShellOrThrowAsync(_primary,
            "echo 'host replication all all trust' >> \"$PGDATA/pg_hba.conf\" && " +
            "su postgres -c \"pg_ctl -D '$PGDATA' reload\"",
            "enable replication host auth on primary");

        _standby = BuildStandby(_image, _network);
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
    // 1. Write through pgdog primary route → history row created
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WriteThroughPgdogPrimaryRoute_CreatesHistory()
    {
        var dbName = NewDbName();
        var configDir = Path.Combine(Path.GetTempPath(),
            $"pgtemporal_pgdog_primary_{Guid.NewGuid():N}");

        IContainer? pgdog = null;
        try
        {
            // Bootstrap extension + table on the real primary
            await using (var admin = await ConnectAsync(_primary, PostgresDb))
            {
                await ExecSqlAsync(admin, $"CREATE DATABASE \"{dbName}\"");
            }
            await using (var db = await ConnectAsync(_primary, dbName))
            {
                await ExecSqlAsync(db,
                    "CREATE EXTENSION pg_temporal_tables;" +
                    "CREATE TABLE t (id int PRIMARY KEY, name text);" +
                    "SELECT temporal.enable('t', combine_interval => interval '0')");
            }

            // Start pgdog routing to the primary
            pgdog = BuildPgdog(
                _network,
                backendAlias: "pg-primary",
                backendRole: "primary",
                dbName,
                routeAlias: "pgdog-primary-route",
                configDir: configDir);
            await pgdog.StartAsync(TestContext.Current.CancellationToken);
            await WaitForPgdogConnectionAsync(pgdog, dbName, "primary route");

            // Write through pgdog
            await using var through = await ConnectPgdogAsync(pgdog, dbName);
            await ExecSqlAsync(through, "SET temporal.user_id = 'alice'");
            await ExecSqlAsync(through, "INSERT INTO t VALUES (1, 'v0')");
            await ExecSqlAsync(through, "SET temporal.user_id = 'bob'");
            await ExecSqlAsync(through, "UPDATE t SET name = 'v1' WHERE id = 1");

            // Verify history through pgdog as well
            var histCount = await ScalarAsync<long>(through,
                "SELECT count(*) FROM t__history WHERE id = 1");
            Assert.Equal(1L, histCount);

            var changedBy = await ScalarAsync<string?>(through,
                "SELECT changed_by FROM t__history WHERE id = 1");
            Assert.Equal("alice", changedBy);
        }
        finally
        {
            if (pgdog is not null) await pgdog.DisposeAsync();
            DeleteDirectoryQuietly(configDir);
        }
    }

    // -------------------------------------------------------------------------
    // 2. Read through pgdog replica route after catch-up → as_of returns old value
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AsOfReadThroughPgdogReplicaRoute_Works()
    {
        var dbName = NewDbName();
        var configDir = Path.Combine(Path.GetTempPath(),
            $"pgtemporal_pgdog_replica_{Guid.NewGuid():N}");

        IContainer? pgdog = null;
        try
        {
            // Bootstrap extension + table on the real primary
            await using (var admin = await ConnectAsync(_primary, PostgresDb))
            {
                await ExecSqlAsync(admin, $"CREATE DATABASE \"{dbName}\"");
            }
            await using (var db = await ConnectAsync(_primary, dbName))
            {
                await ExecSqlAsync(db,
                    "CREATE EXTENSION pg_temporal_tables;" +
                    "CREATE TABLE t (id int PRIMARY KEY, name text);" +
                    "SELECT temporal.enable('t', combine_interval => interval '0')");
            }

            // Write first version as alice
            string m1;
            await using (var db = await ConnectAsync(_primary, dbName))
            {
                await ExecSqlAsync(db, "SET temporal.user_id = 'alice'");
                await ExecSqlAsync(db, "INSERT INTO t VALUES (1, 'original')");

                await Task.Delay(50, TestContext.Current.CancellationToken);
                m1 = (await ScalarAsync<string>(db, "SELECT clock_timestamp()::text"))!;
                await Task.Delay(50, TestContext.Current.CancellationToken);

                await ExecSqlAsync(db, "SET temporal.user_id = 'bob'");
                await ExecSqlAsync(db, "UPDATE t SET name = 'updated' WHERE id = 1");
            }

            // Wait for standby to have replayed the writes
            string lsn;
            await using (var db = await ConnectAsync(_primary, PostgresDb))
            {
                lsn = (await ScalarAsync<string>(db, "SELECT pg_current_wal_lsn()::text"))!;
            }
            await WaitForConditionAsync(async () =>
            {
                try
                {
                    await using var s = await ConnectAsync(_standby, PostgresDb);
                    return await ScalarAsync<bool>(s,
                        $"SELECT pg_last_wal_replay_lsn() >= '{lsn}'::pg_lsn");
                }
                catch { return false; }
            }, "standby replay catch-up");

            // Start pgdog routing to the standby replica
            pgdog = BuildPgdog(
                _network,
                backendAlias: "pg-standby",
                backendRole: "replica",
                dbName,
                routeAlias: "pgdog-replica-route",
                configDir: configDir);
            await pgdog.StartAsync(TestContext.Current.CancellationToken);
            await WaitForPgdogConnectionAsync(pgdog, dbName, "replica route");

            // Read through pgdog replica route with as_of = m1
            await using var through = await ConnectPgdogAsync(pgdog, dbName);
            await using var setCmd = through.CreateCommand();
            setCmd.CommandText = "SELECT set_config('temporal.as_of', $1, false)";
            setCmd.Parameters.AddWithValue(m1);
            await setCmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            var historicName = await ScalarAsync<string?>(through,
                "SELECT name FROM t WHERE id = 1");
            Assert.Equal("original", historicName);
        }
        finally
        {
            if (pgdog is not null) await pgdog.DisposeAsync();
            DeleteDirectoryQuietly(configDir);
        }
    }

    // -------------------------------------------------------------------------
    // Container builders — adapted from reference PgdogTests.cs
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

    private static IContainer BuildPgdog(
        INetwork network,
        string backendAlias,
        string backendRole,
        string dbName,
        string routeAlias,
        string configDir)
    {
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, "pgdog.toml"),
            PgdogConfig(backendAlias, backendRole, dbName),
            Encoding.ASCII);
        File.WriteAllText(
            Path.Combine(configDir, "users.toml"),
            PgdogUsers(dbName),
            Encoding.ASCII);

        return new ContainerBuilder(PgdogImage)
            .WithNetwork(network)
            .WithNetworkAliases(routeAlias)
            .WithPortBinding(6432, true)
            .WithBindMount(configDir, "/config", AccessMode.ReadOnly)
            .WithCommand(
                "pgdog",
                "-c", "/config/pgdog.toml",
                "-u", "/config/users.toml",
                "run")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .AddCustomWaitStrategy(new ImmediateReadyWait()))
            .Build();
    }

    private static string PgdogConfig(
        string backendAlias, string backendRole, string dbName) =>
        $"""
        [general]
        port = 6432
        default_pool_size = 2
        min_pool_size = 0

        [[databases]]
        name = "{dbName}"
        host = "{backendAlias}"
        port = 5432
        database_name = "{dbName}"
        role = "{backendRole}"
        """;

    private static string PgdogUsers(string dbName) =>
        $"""
        [[users]]
        name = "{PostgresUser}"
        database = "{dbName}"
        password = "{PostgresPassword}"
        """;

    // -------------------------------------------------------------------------
    // Connection helpers
    // -------------------------------------------------------------------------

    private static string PostgresConnectionString(
        IContainer container, string database = PostgresDb) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = container.GetMappedPublicPort(5432),
            Username = PostgresUser,
            Password = PostgresPassword,
            Database = database,
            Pooling = false,
            IncludeErrorDetail = true,
            Timeout = 5,
        }.ConnectionString;

    private static string PgdogConnectionString(
        IContainer container, string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = container.GetMappedPublicPort(6432),
            Username = PostgresUser,
            Password = PostgresPassword,
            Database = database,
            Pooling = false,
            IncludeErrorDetail = true,
            Timeout = 5,
        }.ConnectionString;

    private static async Task<NpgsqlConnection> ConnectAsync(
        IContainer container, string database)
    {
        var conn = new NpgsqlConnection(
            PostgresConnectionString(container, database));
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    private static async Task<NpgsqlConnection> ConnectPgdogAsync(
        IContainer container, string database)
    {
        var conn = new NpgsqlConnection(
            PgdogConnectionString(container, database));
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    // -------------------------------------------------------------------------
    // Wait helpers
    // -------------------------------------------------------------------------

    private static async Task WaitForReadyConnectionAsync(
        IContainer container, string label)
    {
        var cs = PostgresConnectionString(container);
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
        throw new InvalidOperationException(
            $"PostgreSQL {label} was not ready in time{await LogsAsync(container)}", last);
    }

    private static async Task WaitForPgdogConnectionAsync(
        IContainer container, string database, string label)
    {
        var cs = PgdogConnectionString(container, database);
        var deadline = DateTime.UtcNow.AddSeconds(90);
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
        throw new InvalidOperationException(
            $"Pgdog {label} was not ready in time{await LogsAsync(container)}", last);
    }

    private static async Task WaitForConditionAsync(
        Func<Task<bool>> predicate,
        string label,
        int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
                return;
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"Timed out waiting for: {label}");
    }

    // -------------------------------------------------------------------------
    // SQL helpers
    // -------------------------------------------------------------------------

    private static async Task ExecSqlAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        if (result is null or DBNull)
            return default;
        return (T)result;
    }

    private static async Task<string> LogsAsync(IContainer container)
    {
        try
        {
            var (stdout, stderr) = await container.GetLogsAsync();
            return $"\n--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}";
        }
        catch (Exception e)
        {
            return $"\n(could not fetch container logs: {e.Message})";
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}

