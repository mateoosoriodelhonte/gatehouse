using Gatehouse.Application;
using Gatehouse.Infrastructure.GitHub;
using Gatehouse.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gatehouse.Cli;

internal sealed class CliRuntime : IAsyncDisposable
{
    private readonly HttpClient httpClient;

    private CliRuntime(
        LocalReadinessStore store,
        HttpClient httpClient)
    {
        Store = store;
        this.httpClient = httpClient;
    }

    public ILocalReadinessStore Store { get; }

    public static async Task<CliRuntime> CreateAsync(
        string dataPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        var fullPath = Path.GetFullPath(dataPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException(
                "The Gatehouse data path must include a directory.",
                nameof(dataPath));
        }

        var directoryExisted = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (!directoryExisted && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var databaseOptions = new DbContextOptionsBuilder<GatehouseDbContext>()
            .UseSqlite(ConnectionString(fullPath))
            .Options;
        var contextFactory = new CliDbContextFactory(databaseOptions);
        await using (var database = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await database.Database.MigrateAsync(cancellationToken);
        }

        if (!OperatingSystem.IsWindows() && File.Exists(fullPath))
        {
            File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var source = new GitHubApiClient(
            httpClient,
            new GitHubClientOptions
            {
                Token = Environment.GetEnvironmentVariable("GATEHOUSE_GITHUB_TOKEN"),
            });
        var store = new LocalReadinessStore(
            contextFactory,
            source,
            new LocalStoreOptions(),
            TimeProvider.System);
        return new CliRuntime(store, httpClient);
    }

    public static string DefaultDataPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "Gatehouse", "gatehouse.db");
    }

    public static string ConnectionString(string dataPath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dataPath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();

    public ValueTask DisposeAsync()
    {
        httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class CliDbContextFactory(
        DbContextOptions<GatehouseDbContext> options)
        : IDbContextFactory<GatehouseDbContext>
    {
        public GatehouseDbContext CreateDbContext() => new(options);

        public Task<GatehouseDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatehouseDbContext(options));
    }
}
