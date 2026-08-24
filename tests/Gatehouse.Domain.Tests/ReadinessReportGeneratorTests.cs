using Gatehouse.Domain;

namespace Gatehouse.Domain.Tests;

public sealed class ReadinessReportGeneratorTests
{
    [Fact]
    public void Go_report_is_concise_and_evidence_based()
    {
        var snapshot = SnapshotFactory.Ready();
        var evaluation = ReadinessEngine.Evaluate(snapshot, RepositoryPolicy.SafeDefaults);

        var report = ReadinessReportGenerator.Generate(snapshot, evaluation);

        Assert.Equal(
            """
            GO for review.
            PR #142 is open and non-draft.
            Mergeability: clean.
            Branch freshness: current.
            All required checks are green.
            Required review is satisfied.
            No unresolved review threads remain.
            Linked issue: #139.
            Recommendation: ready for maintainer review or merge.
            """,
            report);
    }

    [Fact]
    public void Blocked_report_lists_each_blocker_once()
    {
        var snapshot = SnapshotFactory.Ready() with
        {
            Mergeability = Mergeability.Conflicting,
            Checks = [new CheckSnapshot("build", CheckState.Failure, true, null)],
        };
        var evaluation = ReadinessEngine.Evaluate(snapshot, RepositoryPolicy.SafeDefaults);

        var report = ReadinessReportGenerator.Generate(snapshot, evaluation);

        Assert.Equal(
            """
            NO-GO.
            PR #142 has 2 merge blockers:
            - Required check failed: build.
            - The pull request has a merge conflict.
            Do not merge until both blockers are resolved.
            """,
            report);
    }

    [Fact]
    public void Go_report_marks_a_closed_linked_issue()
    {
        var snapshot = SnapshotFactory.Ready() with
        {
            IssueLinks = [new IssueLink(139, IssueLinkKind.Explicit, true, null)],
        };
        var evaluation = ReadinessEngine.Evaluate(snapshot, RepositoryPolicy.SafeDefaults);

        var report = ReadinessReportGenerator.Generate(snapshot, evaluation);

        Assert.Contains("Linked issue: #139 (closed).", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Go_report_does_not_overstate_advisory_mergeability_or_freshness()
    {
        var snapshot = SnapshotFactory.Ready() with
        {
            Mergeability = Mergeability.Conflicting,
            BranchFreshness = BranchFreshness.Behind,
        };
        var policy = RepositoryPolicy.SafeDefaults with
        {
            RequireMergeable = false,
            RequireCurrentBranch = false,
        };
        var evaluation = ReadinessEngine.Evaluate(snapshot, policy);

        var report = ReadinessReportGenerator.Generate(snapshot, evaluation);

        Assert.Equal(ReadinessStatus.Go, evaluation.Status);
        Assert.Contains("Mergeability: conflict (advisory).", report, StringComparison.Ordinal);
        Assert.Contains("Branch freshness: behind (advisory).", report, StringComparison.Ordinal);
        Assert.Contains(
            "Recommendation: ready for review; resolve the merge conflict before merge.",
            report,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Mergeability: clean.", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Branch freshness: current.", report, StringComparison.Ordinal);
    }
}
