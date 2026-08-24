using Gatehouse.Domain;

namespace Gatehouse.Domain.Tests;

public sealed class ReadinessEngineTests
{
    private static readonly RepositoryPolicy DefaultPolicy = RepositoryPolicy.SafeDefaults;

    [Fact]
    public void Ready_pull_request_is_go()
    {
        var evaluation = ReadinessEngine.Evaluate(SnapshotFactory.Ready(), DefaultPolicy);

        Assert.Equal(ReadinessStatus.Go, evaluation.Status);
        Assert.Empty(evaluation.Blockers);
        Assert.Equal("Ready for maintainer review or merge.", evaluation.NextAction);
        Assert.Equal(SnapshotFactory.Ready().FetchedAt, evaluation.EvaluatedAt);
    }

    [Fact]
    public void Draft_takes_precedence_over_other_blockers()
    {
        var snapshot = SnapshotFactory.Ready() with
        {
            IsDraft = true,
            Mergeability = Mergeability.Conflicting,
            Checks = [new CheckSnapshot("build", CheckState.Failure, true, null)],
        };

        var evaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);

        Assert.Equal(ReadinessStatus.Draft, evaluation.Status);
        Assert.Equal("Mark the pull request ready for review when the change is complete.", evaluation.NextAction);
    }

    [Fact]
    public void Merge_conflict_is_blocked()
    {
        var snapshot = SnapshotFactory.Ready() with { Mergeability = Mergeability.Conflicting };

        var evaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);

        Assert.Equal(ReadinessStatus.Blocked, evaluation.Status);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Type == "merge_conflict");
    }

    [Theory]
    [InlineData(CheckState.Failure, ReadinessStatus.Blocked, "ci_failed")]
    [InlineData(CheckState.Pending, ReadinessStatus.Review, "ci_pending")]
    [InlineData(CheckState.Cancelled, ReadinessStatus.Blocked, "ci_cancelled")]
    [InlineData(CheckState.ActionRequired, ReadinessStatus.Blocked, "ci_action_required")]
    [InlineData(CheckState.NotExecuted, ReadinessStatus.Review, "ci_not_executed")]
    [InlineData(CheckState.Unknown, ReadinessStatus.Unknown, "ci_unknown")]
    public void Required_check_states_remain_distinct(
        CheckState checkState,
        ReadinessStatus expectedStatus,
        string expectedBlockerType)
    {
        var snapshot = SnapshotFactory.Ready() with
        {
            Checks = [new CheckSnapshot("Path-Aware QA", checkState, true, "https://example.test/checks/qa")],
        };

        var evaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);

        Assert.Equal(expectedStatus, evaluation.Status);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Type == expectedBlockerType);
    }

    [Fact]
    public void Optional_failed_check_does_not_block_when_policy_requires_only_required_checks()
    {
        var policy = DefaultPolicy with { RequireAllChecks = false };
        var snapshot = SnapshotFactory.Ready() with
        {
            Checks = [new CheckSnapshot("preview", CheckState.Failure, false, null)],
        };

        var evaluation = ReadinessEngine.Evaluate(snapshot, policy);

        Assert.Equal(ReadinessStatus.Go, evaluation.Status);
        Assert.Empty(evaluation.Blockers);
    }

    [Fact]
    public void No_checks_is_not_invented_as_a_failure()
    {
        var snapshot = SnapshotFactory.Ready() with { Checks = [] };

        var evaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);

        Assert.Equal(ReadinessStatus.Go, evaluation.Status);
    }

    [Fact]
    public void Requested_changes_are_blocking()
    {
        var snapshot = SnapshotFactory.Ready() with { ReviewDecision = ReviewDecision.ChangesRequested };

        var evaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);

        Assert.Equal(ReadinessStatus.Blocked, evaluation.Status);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Type == "changes_requested");
    }

    [Fact]
    public void Missing_required_approval_needs_review()
    {
        var snapshot = SnapshotFactory.Ready() with
        {
            ReviewDecision = ReviewDecision.ReviewRequired,
            ApprovalCount = 0,
            RequestedReviewerCount = 2,
        };

        var evaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);

        Assert.Equal(ReadinessStatus.Review, evaluation.Status);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Type == "approval_required");
    }

    [Fact]
    public void Unknown_required_review_state_is_unknown()
    {
        var snapshot = SnapshotFactory.Ready() with { ReviewDecision = ReviewDecision.Unknown };

        var evaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);

        Assert.Equal(ReadinessStatus.Unknown, evaluation.Status);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Type == "review_unknown");
    }

    [Fact]
    public void Required_unresolved_threads_are_blocking()
    {
        var snapshot = SnapshotFactory.Ready() with { UnresolvedReviewThreadCount = 2 };

        var evaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);

        Assert.Equal(ReadinessStatus.Blocked, evaluation.Status);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Type == "unresolved_threads");
    }

    [Fact]
    public void Unknown_mergeability_stays_unknown()
    {
        var snapshot = SnapshotFactory.Ready() with { Mergeability = Mergeability.Unknown };

        var evaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);

        Assert.Equal(ReadinessStatus.Unknown, evaluation.Status);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Type == "mergeability_unknown");
    }

    [Fact]
    public void Possible_issue_reference_does_not_satisfy_explicit_link_policy()
    {
        var policy = DefaultPolicy with { RequireLinkedIssue = true };
        var snapshot = SnapshotFactory.Ready() with
        {
            IssueLinks = [new IssueLink(139, IssueLinkKind.PossibleReference, false, null)],
        };

        var evaluation = ReadinessEngine.Evaluate(snapshot, policy);

        Assert.Equal(ReadinessStatus.Review, evaluation.Status);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Type == "linked_issue_required");
    }

    [Fact]
    public void Behind_branch_is_advisory_until_policy_requires_freshness()
    {
        var snapshot = SnapshotFactory.Ready() with { BranchFreshness = BranchFreshness.Behind };

        var advisoryEvaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);
        var requiredEvaluation = ReadinessEngine.Evaluate(
            snapshot,
            DefaultPolicy with { RequireCurrentBranch = true });

        Assert.Equal(ReadinessStatus.Go, advisoryEvaluation.Status);
        Assert.Equal(ReadinessStatus.Blocked, requiredEvaluation.Status);
        Assert.Contains(requiredEvaluation.Blockers, blocker => blocker.Type == "branch_behind");
    }

    [Fact]
    public void Unknown_freshness_is_unknown_only_when_policy_requires_it()
    {
        var snapshot = SnapshotFactory.Ready() with { BranchFreshness = BranchFreshness.Unknown };

        var advisoryEvaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);
        var requiredEvaluation = ReadinessEngine.Evaluate(
            snapshot,
            DefaultPolicy with { RequireCurrentBranch = true });

        Assert.Equal(ReadinessStatus.Go, advisoryEvaluation.Status);
        Assert.Equal(ReadinessStatus.Unknown, requiredEvaluation.Status);
    }

    [Fact]
    public void Closed_pull_request_is_unknown_not_ready()
    {
        var snapshot = SnapshotFactory.Ready() with { State = PullRequestState.Closed };

        var evaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);

        Assert.Equal(ReadinessStatus.Unknown, evaluation.Status);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Type == "pull_request_not_open");
    }

    [Fact]
    public void Non_open_state_takes_precedence_over_underlying_blockers()
    {
        var snapshot = SnapshotFactory.Ready() with
        {
            State = PullRequestState.Closed,
            IsDraft = true,
            Mergeability = Mergeability.Conflicting,
            Checks = [new CheckSnapshot("build", CheckState.Failure, true, null)],
        };

        var evaluation = ReadinessEngine.Evaluate(snapshot, DefaultPolicy);

        Assert.Equal(ReadinessStatus.Unknown, evaluation.Status);
        Assert.Collection(
            evaluation.Blockers,
            blocker => Assert.Equal("pull_request_not_open", blocker.Type));
    }

    [Fact]
    public void Evaluation_order_is_deterministic_when_provider_order_changes()
    {
        var first = SnapshotFactory.Ready() with
        {
            Checks =
            [
                new CheckSnapshot("zeta", CheckState.Failure, true, "https://example.test/zeta"),
                new CheckSnapshot("alpha", CheckState.Pending, true, "https://example.test/alpha"),
            ],
        };
        var second = first with { Checks = [first.Checks[1], first.Checks[0]] };

        var firstEvaluation = ReadinessEngine.Evaluate(first, DefaultPolicy);
        var secondEvaluation = ReadinessEngine.Evaluate(second, DefaultPolicy);

        Assert.Equal(
            firstEvaluation.Blockers.Select(BlockerIdentity),
            secondEvaluation.Blockers.Select(BlockerIdentity));
        Assert.Equal(
            firstEvaluation.Rules.Select(rule => rule.Id),
            secondEvaluation.Rules.Select(rule => rule.Id));
    }

    private static string BlockerIdentity(ReadinessBlocker blocker) =>
        $"{blocker.Type}:{blocker.Summary}:{blocker.EvidenceUrl}";
}
