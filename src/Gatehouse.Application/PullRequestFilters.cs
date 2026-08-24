using Gatehouse.Domain;

namespace Gatehouse.Application;

public enum PullRequestCiFilter
{
    All,
    Passing,
    Blocked,
    Pending,
    NotRun,
}

public enum PullRequestDraftFilter
{
    All,
    Ready,
    Draft,
}

public sealed record PullRequestFilter(
    ReadinessStatus? Status = null,
    string? Search = null,
    string? Author = null,
    string? Label = null,
    string? Branch = null,
    string? Reviewer = null,
    PullRequestCiFilter Ci = PullRequestCiFilter.All,
    PullRequestDraftFilter Draft = PullRequestDraftFilter.All);

public static class PullRequestFilters
{
    public static IReadOnlyList<CachedPullRequestReadiness> Apply(
        IEnumerable<CachedPullRequestReadiness> pullRequests,
        PullRequestFilter filter)
    {
        ArgumentNullException.ThrowIfNull(pullRequests);
        ArgumentNullException.ThrowIfNull(filter);

        return pullRequests
            .Where(item => filter.Status is null || item.Evaluation.Status == filter.Status)
            .Where(item => Contains(item.Snapshot.Title, filter.Search) ||
                Contains($"#{item.Snapshot.Number}", filter.Search))
            .Where(item => Contains(item.Snapshot.Author, filter.Author))
            .Where(item => AnyContains(item.Snapshot.Labels, filter.Label))
            .Where(item => Contains(item.Snapshot.HeadBranch, filter.Branch))
            .Where(item => AnyContains(item.Snapshot.RequestedReviewers, filter.Reviewer))
            .Where(item => MatchesCi(item, filter.Ci))
            .Where(item => filter.Draft switch
            {
                PullRequestDraftFilter.Draft => item.Snapshot.IsDraft,
                PullRequestDraftFilter.Ready => !item.Snapshot.IsDraft,
                _ => true,
            })
            .OrderBy(item => StatusOrder(item.Evaluation.Status))
            .ThenByDescending(item => item.Snapshot.UpdatedAt)
            .ThenBy(item => item.Snapshot.Number)
            .ToArray();
    }

    public static PullRequestCiFilter ClassifyCi(IReadOnlyList<CheckSnapshot> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        if (checks.Any(check => check.State is
            CheckState.Failure or CheckState.Cancelled or CheckState.ActionRequired))
        {
            return PullRequestCiFilter.Blocked;
        }

        if (checks.Any(check => check.State == CheckState.Pending))
        {
            return PullRequestCiFilter.Pending;
        }

        if (checks.Count == 0 || checks.Any(check => check.State is
            CheckState.Unknown or CheckState.NotExecuted))
        {
            return PullRequestCiFilter.NotRun;
        }

        return PullRequestCiFilter.Passing;
    }

    private static bool MatchesCi(
        CachedPullRequestReadiness item,
        PullRequestCiFilter filter)
    {
        if (filter == PullRequestCiFilter.All)
        {
            return true;
        }

        var ciBlockers = item.Evaluation.Blockers
            .Where(blocker => blocker.Type.StartsWith("ci_", StringComparison.Ordinal))
            .ToArray();
        return filter switch
        {
            PullRequestCiFilter.Blocked => ciBlockers.Any(blocker => blocker.Impact == ReadinessImpact.Blocked),
            PullRequestCiFilter.Pending => ciBlockers.Any(blocker => blocker.Type == "ci_pending"),
            PullRequestCiFilter.NotRun => item.Snapshot.Checks.Count == 0 || ciBlockers.Any(blocker =>
                blocker.Type is "ci_not_executed" or "ci_unknown"),
            PullRequestCiFilter.Passing => item.Snapshot.Checks.Count > 0 && ciBlockers.Length == 0,
            _ => true,
        };
    }

    private static bool AnyContains(IEnumerable<string> values, string? expected) =>
        string.IsNullOrWhiteSpace(expected) || values.Any(value => Contains(value, expected));

    private static bool Contains(string value, string? expected) =>
        string.IsNullOrWhiteSpace(expected) ||
        value.Contains(expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static int StatusOrder(ReadinessStatus status) => status switch
    {
        ReadinessStatus.Go => 0,
        ReadinessStatus.Review => 1,
        ReadinessStatus.Blocked => 2,
        ReadinessStatus.Draft => 3,
        _ => 4,
    };
}
