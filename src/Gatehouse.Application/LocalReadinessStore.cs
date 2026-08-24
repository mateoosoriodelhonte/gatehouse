using Gatehouse.Domain;

namespace Gatehouse.Application;

public interface ILocalReadinessStore
{
    Task<LocalRepositorySummary> AddRepositoryAsync(
        RepositoryRegistration registration,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalRepositorySummary>> ListRepositoriesAsync(
        CancellationToken cancellationToken = default);

    Task<LocalRepositoryDetail?> GetRepositoryAsync(
        Guid repositoryId,
        CancellationToken cancellationToken = default);

    Task<bool> SelectRepositoryAsync(
        Guid repositoryId,
        CancellationToken cancellationToken = default);

    Task<bool> UpdatePolicyAsync(
        Guid repositoryId,
        RepositoryPolicy policy,
        CancellationToken cancellationToken = default);

    Task<RepositoryRefreshResult?> RefreshRepositoryAsync(
        Guid repositoryId,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveRepositoryAsync(
        Guid repositoryId,
        CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed record RepositoryRegistration(
    string Owner,
    string Name,
    RepositoryPolicy Policy);

public sealed record LocalRepositorySummary(
    Guid Id,
    string Owner,
    string Name,
    bool IsSelected,
    DateTimeOffset AddedAt,
    DateTimeOffset? LastRefreshAttemptAt,
    DateTimeOffset? LastSuccessfulRefreshAt,
    PullRequestFetchStatus? LastFetchStatus,
    int CachedPullRequestCount,
    IReadOnlyList<string> Warnings);

public sealed record CachedPullRequestReadiness(
    PullRequestSnapshot Snapshot,
    ReadinessEvaluation Evaluation,
    string ReportMarkdown,
    long CacheAgeSeconds,
    bool IsStale);

public sealed record LocalRepositoryDetail(
    LocalRepositorySummary Repository,
    RepositoryPolicy Policy,
    IReadOnlyList<CachedPullRequestReadiness> PullRequests);

public sealed record RepositoryRefreshResult(
    PullRequestFetchStatus Status,
    int StoredSnapshotCount,
    LocalRepositoryDetail Repository,
    ProviderRateLimit RateLimit,
    IReadOnlyList<string> Warnings);

public sealed class DuplicateRepositoryException(string owner, string name) : Exception(
    $"The repository {owner}/{name} is already configured.");
