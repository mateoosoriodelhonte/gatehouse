using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gatehouse.Application;
using Gatehouse.Domain;
using Gatehouse.Infrastructure.Persistence;
using Gatehouse.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gatehouse.IntegrationTests;

public sealed class LocalStoreAndApiTests
{
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly DateTimeOffset Now =
        new(2026, 8, 24, 17, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Initial_migration_creates_a_clean_local_database()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = await database.Factory.CreateDbContextAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20260824170000_InitialLocalStore", await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Repositories.ToArrayAsync());
        Assert.Empty(await db.Snapshots.ToArrayAsync());
    }

    [Fact]
    public async Task Refresh_failure_and_policy_change_preserve_immutable_cached_evidence()
    {
        await using var database = await TestDatabase.CreateAsync();
        var source = new QueuePullRequestSource(
            SuccessResult(CreateReadySnapshot(Now.AddMinutes(-20))),
            new PullRequestFetchResult(
                PullRequestFetchStatus.Unavailable,
                [],
                "\"etag-1\"",
                new ProviderRateLimit(5000, 4990, Now.AddHours(1)),
                Now,
                true,
                ["GitHub is unavailable."]));
        var store = CreateStore(database.Factory, source);

        var repository = await store.AddRepositoryAsync(new RepositoryRegistration(
            "acme",
            "payments",
            RepositoryPolicy.SafeDefaults));
        var firstRefresh = await store.RefreshRepositoryAsync(repository.Id);
        var firstDetail = Assert.IsType<LocalRepositoryDetail>(firstRefresh?.Repository);
        var cached = Assert.Single(firstDetail.PullRequests);
        Assert.Equal(ReadinessStatus.Go, cached.Evaluation.Status);
        Assert.True(cached.IsStale);
        Assert.Equal(1200, cached.CacheAgeSeconds);

        var secondRefresh = await store.RefreshRepositoryAsync(repository.Id);
        Assert.Equal(PullRequestFetchStatus.Unavailable, secondRefresh?.Status);
        var preserved = Assert.Single(secondRefresh?.Repository.PullRequests ?? []);
        Assert.Equal(ReadinessStatus.Go, preserved.Evaluation.Status);
        Assert.Contains("GitHub is unavailable.", secondRefresh!.Repository.Repository.Warnings);

        Assert.True(await store.UpdatePolicyAsync(
            repository.Id,
            RepositoryPolicy.SafeDefaults with { RequireLinkedIssue = true }));
        var reevaluated = await store.GetRepositoryAsync(repository.Id);
        Assert.Equal(
            ReadinessStatus.Review,
            Assert.Single(reevaluated?.PullRequests ?? []).Evaluation.Status);

        await using var db = await database.Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.Snapshots.CountAsync());
        Assert.DoesNotContain(
            "test-token",
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database.Path)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_selection_and_removal_keep_one_selected_repository()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = CreateStore(database.Factory, new QueuePullRequestSource());
        var first = await store.AddRepositoryAsync(new RepositoryRegistration(
            "acme",
            "one",
            RepositoryPolicy.SafeDefaults));
        var second = await store.AddRepositoryAsync(new RepositoryRegistration(
            "acme",
            "two",
            RepositoryPolicy.SafeDefaults));

        Assert.True(first.IsSelected);
        Assert.False(second.IsSelected);
        Assert.True(await store.SelectRepositoryAsync(second.Id));
        Assert.True(await store.SelectRepositoryAsync(second.Id));
        Assert.True((await store.ListRepositoriesAsync()).Single(item => item.Id == second.Id).IsSelected);
        await Assert.ThrowsAsync<DuplicateRepositoryException>(() =>
            store.AddRepositoryAsync(new RepositoryRegistration(
                "ACME",
                "ONE",
                RepositoryPolicy.SafeDefaults)));

        Assert.True(await store.RemoveRepositoryAsync(second.Id));
        var remaining = Assert.Single(await store.ListRepositoriesAsync());
        Assert.Equal(first.Id, remaining.Id);
        Assert.True(remaining.IsSelected);
    }

    [Fact]
    public async Task Versioned_api_validates_input_and_returns_cached_readiness()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"gatehouse-api-{Guid.NewGuid():N}.db");
        var source = new QueuePullRequestSource(SuccessResult(CreateReadySnapshot(Now)));
        var port = GetAvailablePort();
        var options = new WebApplicationOptions
        {
            ApplicationName = typeof(GatehouseHost).Assembly.FullName,
            EnvironmentName = "Testing",
        };
        await using var app = await GatehouseHost.BuildAsync(
            options,
            builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Gatehouse"] = $"Data Source={databasePath}",
                ["Gatehouse:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }),
            services =>
            {
                services.RemoveAll<IPullRequestSource>();
                services.AddSingleton<IPullRequestSource>(source);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            });
        try
        {
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = ServerAddress(app) };

            var missingHeader = await client.PostAsync(
                $"/api/v1/repositories/{Guid.NewGuid()}/refresh",
                content: null);
            Assert.Equal(HttpStatusCode.BadRequest, missingHeader.StatusCode);
            client.DefaultRequestHeaders.Add("X-Gatehouse-Request", "1");

            var invalid = await client.PostAsJsonAsync(
                "/api/v1/repositories",
                new { owner = "bad/name", name = "repo" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            var addedResponse = await client.PostAsJsonAsync(
                "/api/v1/repositories",
                new { owner = "acme", name = "payments" });
            Assert.Equal(HttpStatusCode.Created, addedResponse.StatusCode);
            var added = await addedResponse.Content.ReadFromJsonAsync<LocalRepositorySummary>();
            Assert.NotNull(added);

            var refresh = await client.PostAsync(
                $"/api/v1/repositories/{added.Id}/refresh",
                content: null);
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

            var filtered = await client.GetAsync(
                $"/api/v1/repositories/{added.Id}/pull-requests?status=go&stale=false");
            Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
            var cached = await filtered.Content.ReadFromJsonAsync<CachedPullRequestReadiness[]>(
                ApiJsonOptions);
            Assert.Single(cached ?? []);

            var badFilter = await client.GetAsync(
                $"/api/v1/repositories/{added.Id}/pull-requests?status=maybe");
            Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
            var numericFilter = await client.GetAsync(
                $"/api/v1/repositories/{added.Id}/pull-requests?status=1");
            Assert.Equal(HttpStatusCode.BadRequest, numericFilter.StatusCode);
            Assert.DoesNotContain(
                "test-token",
                await refresh.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static LocalReadinessStore CreateStore(
        IDbContextFactory<GatehouseDbContext> factory,
        IPullRequestSource source) =>
        new(
            factory,
            source,
            new LocalStoreOptions
            {
                FreshnessMinutes = 15,
                RetentionDays = 30,
                MaxSnapshotsPerPullRequest = 50,
            },
            new FixedTimeProvider(Now));

    private static PullRequestFetchResult SuccessResult(PullRequestSnapshot snapshot) =>
        new(
            PullRequestFetchStatus.Success,
            [snapshot],
            "\"etag-1\"",
            new ProviderRateLimit(5000, 4999, Now.AddHours(1)),
            Now,
            true,
            []);

    private static PullRequestSnapshot CreateReadySnapshot(DateTimeOffset fetchedAt) => new()
    {
        Repository = new RepositorySlug("acme", "payments"),
        Number = 42,
        Title = "Ready change",
        Author = "octo-dev",
        State = PullRequestState.Open,
        IsDraft = false,
        Mergeability = Mergeability.Clean,
        ReviewDecision = ReviewDecision.Approved,
        ApprovalCount = 1,
        RequestedReviewerCount = 0,
        UnresolvedReviewThreadCount = 0,
        BranchFreshness = BranchFreshness.Current,
        Checks = [new CheckSnapshot("build", CheckState.Success, true, "https://example.test/check")],
        IssueLinks = [],
        UpdatedAt = fetchedAt.AddMinutes(-1),
        FetchedAt = fetchedAt,
        Url = "https://github.com/acme/payments/pull/42",
        BaseBranch = "main",
        HeadBranch = "feature/ready",
        BaseSha = "base",
        HeadSha = "head",
        ChangedFiles = 1,
        Additions = 10,
        Deletions = 2,
        Files = [new ChangedFile("src/change.cs", "modified", 10, 2, null)],
    };

    private static Uri ServerAddress(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        return new Uri(Assert.Single(addresses ?? []));
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var candidate in new[] { path, $"{path}-shm", $"{path}-wal" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    private sealed class QueuePullRequestSource(params PullRequestFetchResult[] results)
        : IPullRequestSource
    {
        private readonly Queue<PullRequestFetchResult> queue = new(results);

        public Task<PullRequestFetchResult> GetOpenPullRequestsAsync(
            RepositorySlug repository,
            string? etag,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(queue.Dequeue());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(string path, TestDbContextFactory factory)
        {
            Path = path;
            Factory = factory;
        }

        public string Path { get; }

        public TestDbContextFactory Factory { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"gatehouse-store-{Guid.NewGuid():N}.db");
            var factory = new TestDbContextFactory(path);
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
            return new TestDatabase(path, factory);
        }

        public ValueTask DisposeAsync()
        {
            DeleteDatabaseFiles(Path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(string path) : IDbContextFactory<GatehouseDbContext>
    {
        private readonly DbContextOptions<GatehouseDbContext> options =
            new DbContextOptionsBuilder<GatehouseDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;

        public GatehouseDbContext CreateDbContext() => new(options);

        public Task<GatehouseDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
