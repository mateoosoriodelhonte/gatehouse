using Gatehouse.Domain;

namespace Gatehouse.Application;

public static class DemoReadinessCatalog
{
    public static readonly Guid RepositoryId =
        Guid.Parse("8f61fd03-94e8-4fba-9b5d-a8c8ca4d6f21");

    private static readonly DateTimeOffset RefreshedAt =
        new(2026, 8, 24, 17, 30, 0, TimeSpan.Zero);

    public static LocalRepositoryDetail Create()
    {
        var policy = RepositoryPolicy.SafeDefaults;
        var snapshots = new[]
        {
            ReadyPullRequest(),
            WaitingForReviewPullRequest(),
            FailingCiPullRequest(),
            ConflictingPullRequest(),
            DraftPullRequest(),
        };
        var readiness = snapshots.Select(snapshot =>
        {
            var evaluation = ReadinessEngine.Evaluate(snapshot, policy);
            return new CachedPullRequestReadiness(
                snapshot,
                evaluation,
                ReadinessReportGenerator.Generate(snapshot, evaluation),
                180,
                false);
        }).ToArray();
        var summary = new LocalRepositorySummary(
            RepositoryId,
            "acme",
            "payments",
            true,
            RefreshedAt.AddDays(-30),
            RefreshedAt,
            RefreshedAt,
            PullRequestFetchStatus.Success,
            readiness.Length,
            ["Demo data is synthetic and never leaves this device."]);
        return new LocalRepositoryDetail(summary, policy, readiness);
    }

    private static PullRequestSnapshot ReadyPullRequest() => CreateSnapshot(
        142,
        "Add pagination to audit endpoint",
        "maya-dev",
        "feature/audit-pagination",
        ["api", "ready"],
        ["release-captain"],
        Mergeability.Clean,
        ReviewDecision.Approved,
        approvalCount: 2,
        unresolvedThreads: 0,
        checks:
        [
            Check(142, "build", CheckState.Success),
            Check(142, "security", CheckState.Success),
        ],
        issueNumber: 139);

    private static PullRequestSnapshot WaitingForReviewPullRequest() => CreateSnapshot(
        143,
        "Harden webhook signature checks",
        "noah-dev",
        "security/webhook-signatures",
        ["security", "backend"],
        ["maya-dev"],
        Mergeability.Clean,
        ReviewDecision.ReviewRequired,
        approvalCount: 0,
        unresolvedThreads: 0,
        checks:
        [
            Check(143, "build", CheckState.Success),
            Check(143, "security", CheckState.Success),
        ],
        issueNumber: 140);

    private static PullRequestSnapshot FailingCiPullRequest() => CreateSnapshot(
        144,
        "Fix dashboard route state",
        "octo-dev",
        "fix/route-state",
        ["bug", "frontend"],
        ["maya-dev"],
        Mergeability.Clean,
        ReviewDecision.ChangesRequested,
        approvalCount: 0,
        unresolvedThreads: 2,
        checks:
        [
            Check(144, "build", CheckState.Success),
            Check(144, "Path-Aware QA", CheckState.Failure),
        ],
        issueNumber: 141);

    private static PullRequestSnapshot ConflictingPullRequest() => CreateSnapshot(
        145,
        "Update payment retry policy",
        "riley-dev",
        "feature/retry-policy",
        ["payments", "backend"],
        ["ops-owner"],
        Mergeability.Conflicting,
        ReviewDecision.Approved,
        approvalCount: 1,
        unresolvedThreads: 0,
        checks: [Check(145, "build", CheckState.Success)],
        issueNumber: 142);

    private static PullRequestSnapshot DraftPullRequest() => CreateSnapshot(
        146,
        "Explore faster ledger reconciliation",
        "sam-dev",
        "spike/ledger-reconciliation",
        ["spike", "ledger"],
        [],
        Mergeability.Unknown,
        ReviewDecision.ReviewRequired,
        approvalCount: 0,
        unresolvedThreads: 0,
        checks: [Check(146, "build", CheckState.Pending)],
        issueNumber: null,
        isDraft: true);

    private static PullRequestSnapshot CreateSnapshot(
        int number,
        string title,
        string author,
        string headBranch,
        IReadOnlyList<string> labels,
        IReadOnlyList<string> requestedReviewers,
        Mergeability mergeability,
        ReviewDecision reviewDecision,
        int approvalCount,
        int? unresolvedThreads,
        IReadOnlyList<CheckSnapshot> checks,
        int? issueNumber,
        bool isDraft = false) =>
        new()
        {
            Repository = new RepositorySlug("acme", "payments"),
            Number = number,
            Title = title,
            Author = author,
            State = PullRequestState.Open,
            IsDraft = isDraft,
            Mergeability = mergeability,
            ReviewDecision = reviewDecision,
            ApprovalCount = approvalCount,
            RequestedReviewerCount = requestedReviewers.Count,
            RequestedReviewers = requestedReviewers,
            UnresolvedReviewThreadCount = unresolvedThreads,
            BranchFreshness = number == 143 ? BranchFreshness.Behind : BranchFreshness.Current,
            Checks = checks,
            IssueLinks = issueNumber is null
                ? []
                : [new IssueLink(
                    issueNumber.Value,
                    IssueLinkKind.Explicit,
                    false,
                    EvidenceUrl($"issues/{issueNumber}"))],
            Labels = labels,
            UpdatedAt = RefreshedAt.AddMinutes(-(number - 140) * 9),
            FetchedAt = RefreshedAt,
            Url = EvidenceUrl($"pull/{number}"),
            BaseBranch = "main",
            HeadBranch = headBranch,
            BaseSha = $"base-{number}",
            HeadSha = $"head-{number}",
            ChangedFiles = isDraft ? 7 : number - 140,
            Additions = 20 + number,
            Deletions = number - 130,
            Files =
            [
                new ChangedFile(
                    $"src/changes/change-{number}.cs",
                    "modified",
                    20 + number,
                    number - 130,
                    EvidenceUrl($"pull/{number}/files")),
            ],
        };

    private static CheckSnapshot Check(int number, string name, CheckState state) =>
        new(name, state, true, EvidenceUrl($"actions/runs/{number}-{name}"));

    private static string EvidenceUrl(string path) =>
        $"https://example.com/gatehouse-demo/{path}";
}
