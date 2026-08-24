using Gatehouse.Domain;

namespace Gatehouse.Domain.Tests;

internal static class SnapshotFactory
{
    internal static PullRequestSnapshot Ready() => new()
    {
        Repository = new RepositorySlug("acme", "payments"),
        Number = 142,
        Title = "Add pagination to audit endpoint",
        Author = "octo-dev",
        State = PullRequestState.Open,
        IsDraft = false,
        Mergeability = Mergeability.Clean,
        ReviewDecision = ReviewDecision.Approved,
        ApprovalCount = 1,
        RequestedReviewerCount = 0,
        UnresolvedReviewThreadCount = 0,
        BranchFreshness = BranchFreshness.Current,
        Checks =
        [
            new CheckSnapshot("build", CheckState.Success, true, "https://example.test/checks/build"),
            new CheckSnapshot("tests", CheckState.Success, true, "https://example.test/checks/tests"),
        ],
        IssueLinks =
        [
            new IssueLink(139, IssueLinkKind.Explicit, false, "https://example.test/issues/139"),
        ],
        UpdatedAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
        FetchedAt = new DateTimeOffset(2026, 8, 24, 12, 5, 0, TimeSpan.Zero),
        Url = "https://example.test/pulls/142",
        BaseBranch = "main",
        HeadBranch = "feature/pagination",
        BaseSha = "base-sha",
        HeadSha = "head-sha",
        ChangedFiles = 4,
        Additions = 83,
        Deletions = 12,
        Files = [],
    };
}
