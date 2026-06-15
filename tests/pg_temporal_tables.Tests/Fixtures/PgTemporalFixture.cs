using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace PgTemporalTables.Tests.Fixtures;

internal sealed class ImmediateReadyWait : IWaitUntil
{
    public Task<bool> UntilAsync(IContainer container) => Task.FromResult(true);
}

public sealed class PgTemporalFixture : IAsyncLifetime
{
    private static string PostgresImage => Helpers.PostgresImage;
    private const string PostgresUser = "postgres";
    private const string PostgresPassword = "postgres";
    private const string PostgresDb = "postgres";

    private PostgreSqlContainer _container = null!;

    public string ConnectionString
    {
        get
        {
            var b = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
            {
                IncludeErrorDetail = true,
            };
            return b.ConnectionString;
        }
    }

    public static string ProjectRoot { get; } = LocateProjectRoot();

    public async ValueTask InitializeAsync()
    {
        var image = await CachedTemporalImage.BuildAsync(PostgresImage, Helpers.PgMajor);

        _container = new PostgreSqlBuilder(image)
            .WithUsername(PostgresUser)
            .WithPassword(PostgresPassword)
            .WithDatabase(PostgresDb)
            .WithCommand(
                "-c", "shared_preload_libraries=timescaledb,pg_temporal_tables",
                "-c", "wal_level=replica",
                "-c", "max_wal_senders=10",
                "-c", "wal_keep_size=64")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .AddCustomWaitStrategy(new ImmediateReadyWait()))
            .Build();

        await _container.StartAsync(TestContext.Current.CancellationToken);
        await WaitForReadyConnectionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public NpgsqlConnection CreateConnection() => new(ConnectionString);

    public async Task<NpgsqlConnection> AdminAsync()
    {
        var conn = CreateConnection();
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    /// <summary>
    /// Creates a uniquely-named database on the container, opens an
    /// NpgsqlDataSource to it, and runs CREATE EXTENSION pg_temporal_tables.
    /// Callers own the returned <see cref="TemporalDb"/> and must dispose it.
    /// </summary>
    public async Task<TemporalDb> CreateFreshDbWithExtensionAsync()
    {
        var dbName = "test_" + Guid.NewGuid().ToString("N")[..8];

        await using (var admin = CreateConnection())
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        var csb = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = dbName,
            IncludeErrorDetail = true,
        };

        var dataSource = NpgsqlDataSource.Create(csb.ConnectionString);

        await using (var conn = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE EXTENSION pg_temporal_tables";
            await cmd.ExecuteNonQueryAsync();
        }

        return new TemporalDb(dataSource, dbName, this);
    }

    public async Task DropDbAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = await AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task WaitForReadyConnectionAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var c = CreateConnection();
                await c.OpenAsync(TestContext.Current.CancellationToken);
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync();
                return;
            }
            catch (Exception e)
            {
                last = e;
                await Task.Delay(1000, TestContext.Current.CancellationToken);
            }
        }

        string logs;
        try
        {
            var (stdout, stderr) = await _container.GetLogsAsync();
            logs = $"\n--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}";
        }
        catch (Exception le)
        {
            logs = $"\n(could not fetch logs: {le.Message})";
        }

        throw new InvalidOperationException(
            $"PostgreSQL not accepting connections on {ConnectionString}{logs}",
            last);
    }

    private static string LocateProjectRoot()
    {
        // Test project lives at <root>/tests/pg_temporal_tables.Tests/
        // so we walk up from AppContext.BaseDirectory looking for the sentinel files.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pg_temporal_tables.control")) &&
                File.Exists(Path.Combine(dir.FullName, "Makefile")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate project root (pg_temporal_tables.control + Makefile) walking up from " + AppContext.BaseDirectory);
    }

    public readonly record struct ExecResult(long ExitCode, string Stdout, string Stderr);
}

/// <summary>
/// Scoped handle to a freshly-created database with pg_temporal_tables installed.
/// Dispose to drop the database and release the data source.
/// </summary>
public sealed class TemporalDb : IAsyncDisposable
{
    private readonly PgTemporalFixture _fixture;

    public NpgsqlDataSource DataSource { get; }
    public string DbName { get; }

    internal TemporalDb(NpgsqlDataSource dataSource, string dbName, PgTemporalFixture fixture)
    {
        DataSource = dataSource;
        DbName = dbName;
        _fixture = fixture;
    }

    /// <summary>Executes a non-query SQL statement.</summary>
    public async Task ExecAsync(string sql, int? timeoutSeconds = null)
    {
        await using var conn = await DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (timeoutSeconds.HasValue)
            cmd.CommandTimeout = timeoutSeconds.Value;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Executes a scalar query and returns the result cast to <typeparamref name="T"/>.</summary>
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = await DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return (T)result!;
    }

    /// <summary>
    /// Executes a query and projects each row using <paramref name="map"/>.
    /// </summary>
    public async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var conn = await DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(map(reader));
        return rows;
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _fixture.DropDbAsync(DbName);
    }
}
