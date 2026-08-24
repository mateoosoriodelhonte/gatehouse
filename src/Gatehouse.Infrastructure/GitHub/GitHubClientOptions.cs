namespace Gatehouse.Infrastructure.GitHub;

public sealed class GitHubClientOptions
{
    public const string CurrentRestApiVersion = "2026-03-10";

    public string? Token { get; init; }

    public int MaxRequestsPerRefresh { get; init; } = 200;

    public int MaxPagesPerEndpoint { get; init; } = 10;

    public int MaxPullRequests { get; init; } = 25;

    public int MaxResponseBytes { get; init; } = 5 * 1024 * 1024;

    public int MaxRetryAttempts { get; init; } = 3;

    public int MergeabilityAttempts { get; init; } = 3;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}
