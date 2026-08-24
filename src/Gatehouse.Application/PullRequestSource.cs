using Gatehouse.Domain;

namespace Gatehouse.Application;

public interface IPullRequestSource
{
    Task<PullRequestFetchResult> GetOpenPullRequestsAsync(
        RepositorySlug repository,
        string? etag,
        CancellationToken cancellationToken = default);
}

public enum PullRequestFetchStatus
{
    Success,
    NotModified,
    RateLimited,
    AccessDenied,
    Unavailable,
}

public sealed record ProviderRateLimit(
    int? Limit,
    int? Remaining,
    DateTimeOffset? ResetsAt);

public sealed record PullRequestFetchResult(
    PullRequestFetchStatus Status,
    IReadOnlyList<PullRequestSnapshot> PullRequests,
    string? ETag,
    ProviderRateLimit RateLimit,
    DateTimeOffset FetchedAt,
    bool IsAuthenticated,
    IReadOnlyList<string> Warnings);
