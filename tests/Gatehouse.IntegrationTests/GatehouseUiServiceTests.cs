using Gatehouse.Application;
using Gatehouse.Domain;
using Gatehouse.Infrastructure.GitHub;
using Gatehouse.Web.Ui;

namespace Gatehouse.IntegrationTests;

public sealed class GatehouseUiServiceTests
{
    [Fact]
    public async Task Demo_repository_never_calls_the_persistent_store()
    {
        var store = new RecordingStore();
        var service = new GatehouseUiService(store, new GitHubClientOptions());

        var repository = await service.GetRepositoryAsync(DemoReadinessCatalog.RepositoryId);

        Assert.NotNull(repository);
        Assert.Equal(5, repository.PullRequests.Count);
        Assert.Equal(0, store.GetRepositoryCalls);
        Assert.False(service.IsGitHubTokenConfigured);
    }

    [Theory]
    [InlineData("https://example.com/evidence", "https://example.com/evidence")]
    [InlineData("http://example.com/evidence", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("/relative", null)]
    public void Evidence_links_allow_only_absolute_https(string value, string? expected)
    {
        Assert.Equal(expected, GatehouseUiService.SafeEvidenceUrl(value));
    }

    private sealed class RecordingStore : ILocalReadinessStore
    {
        public int GetRepositoryCalls { get; private set; }

        public Task<LocalRepositorySummary> AddRepositoryAsync(
            RepositoryRegistration registration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LocalRepositorySummary>> ListRepositoriesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalRepositorySummary>>([]);

        public Task<LocalRepositoryDetail?> GetRepositoryAsync(
            Guid repositoryId,
            CancellationToken cancellationToken = default)
        {
            GetRepositoryCalls++;
            return Task.FromResult<LocalRepositoryDetail?>(null);
        }

        public Task<bool> SelectRepositoryAsync(
            Guid repositoryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> UpdatePolicyAsync(
            Guid repositoryId,
            RepositoryPolicy policy,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<RepositoryRefreshResult?> RefreshRepositoryAsync(
            Guid repositoryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RepositoryRefreshResult?>(null);

        public Task<bool> RemoveRepositoryAsync(
            Guid repositoryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task ClearAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
