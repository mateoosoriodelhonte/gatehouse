namespace Gatehouse.Domain;

public static class ReadinessEngine
{
    public static ReadinessEvaluation Evaluate(
        PullRequestSnapshot snapshot,
        RepositoryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);

        var context = new EvaluationContext(snapshot, policy);

        if (snapshot.State != PullRequestState.Open)
        {
            context.Flag(
                "pull-request-state",
                "Pull request state",
                RuleOutcome.Unknown,
                $"The pull request state is {snapshot.State}.",
                "pull_request_not_open",
                "The pull request is not open.",
                ReadinessImpact.Unknown,
                snapshot.Url);
            return context.Build(ReadinessStatus.Unknown);
        }

        context.Rule(
            "pull-request-state",
            "Pull request state",
            RuleOutcome.Passed,
            "The pull request is open.",
            snapshot.Url);

        if (snapshot.IsDraft)
        {
            context.Flag(
                "draft",
                "Draft state",
                RuleOutcome.Waiting,
                "The pull request is a draft.",
                "draft",
                "The pull request is still a draft.",
                ReadinessImpact.Review,
                snapshot.Url);
            return context.Build(ReadinessStatus.Draft);
        }

        context.Rule(
            "draft",
            "Draft state",
            RuleOutcome.Passed,
            "The pull request is ready for review.",
            snapshot.Url);

        EvaluateMergeability(context);
        EvaluateChecks(context);
        EvaluateReview(context);
        EvaluateThreads(context);
        EvaluateIssueLinks(context);
        EvaluateFreshness(context);

        return context.Build();
    }

    private static void EvaluateMergeability(EvaluationContext context)
    {
        var snapshot = context.Snapshot;
        if (!context.Policy.RequireMergeable)
        {
            context.Rule(
                "mergeability",
                "Mergeability",
                RuleOutcome.Advisory,
                $"Mergeability is {snapshot.Mergeability}; policy does not require it.",
                snapshot.Url);
            return;
        }

        switch (snapshot.Mergeability)
        {
            case Mergeability.Clean:
                context.Rule(
                    "mergeability",
                    "Mergeability",
                    RuleOutcome.Passed,
                    "GitHub reports the pull request as mergeable.",
                    snapshot.Url);
                break;
            case Mergeability.Conflicting:
                context.Flag(
                    "mergeability",
                    "Mergeability",
                    RuleOutcome.Failed,
                    "GitHub reports a merge conflict.",
                    "merge_conflict",
                    "The pull request has a merge conflict.",
                    ReadinessImpact.Blocked,
                    snapshot.Url);
                break;
            default:
                context.Flag(
                    "mergeability",
                    "Mergeability",
                    RuleOutcome.Unknown,
                    "GitHub is still calculating mergeability.",
                    "mergeability_unknown",
                    "Mergeability is not known yet.",
                    ReadinessImpact.Unknown,
                    snapshot.Url);
                break;
        }
    }

    private static void EvaluateChecks(EvaluationContext context)
    {
        var checks = context.Snapshot.Checks
            .Where(check => context.Policy.RequireAllChecks || check.IsRequired)
            .OrderBy(check => check.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(check => check.Name, StringComparer.Ordinal)
            .ToArray();

        if (checks.Length == 0)
        {
            context.Rule(
                "checks:none",
                "Checks",
                RuleOutcome.Passed,
                "No applicable checks were reported.");
            return;
        }

        foreach (var check in checks)
        {
            if (check.State is CheckState.Success or CheckState.Skipped or CheckState.Neutral)
            {
                var summary = check.State switch
                {
                    CheckState.Skipped => "The check was skipped.",
                    CheckState.Neutral => "The check completed with a neutral conclusion.",
                    _ => "The check passed.",
                };
                context.Rule(
                    $"check:{check.Name}",
                    check.Name,
                    RuleOutcome.Passed,
                    summary,
                    check.Url);
                continue;
            }

            var finding = CheckFinding.For(check);
            context.Flag(
                $"check:{check.Name}",
                check.Name,
                finding.Outcome,
                finding.Summary,
                finding.Type,
                finding.Summary,
                finding.Impact,
                check.Url,
                check.Name);
        }
    }

    private static void EvaluateReview(EvaluationContext context)
    {
        var snapshot = context.Snapshot;
        if (snapshot.ReviewDecision == ReviewDecision.ChangesRequested &&
            context.Policy.BlockOnChangesRequested)
        {
            context.Flag(
                "review-decision",
                "Review decision",
                RuleOutcome.Failed,
                "A reviewer requested changes.",
                "changes_requested",
                "A reviewer requested changes.",
                ReadinessImpact.Blocked,
                snapshot.Url);
            return;
        }

        if (!context.Policy.RequireApproval)
        {
            context.Rule(
                "review-decision",
                "Review decision",
                RuleOutcome.Advisory,
                "Policy does not require approval.",
                snapshot.Url);
            return;
        }

        if (snapshot.ReviewDecision == ReviewDecision.Approved && snapshot.ApprovalCount > 0)
        {
            context.Rule(
                "review-decision",
                "Review decision",
                RuleOutcome.Passed,
                $"Required review is satisfied with {snapshot.ApprovalCount} approval(s).",
                snapshot.Url);
            return;
        }

        if (snapshot.ReviewDecision == ReviewDecision.Unknown)
        {
            context.Flag(
                "review-decision",
                "Review decision",
                RuleOutcome.Unknown,
                "The required review state is unknown.",
                "review_unknown",
                "Required review state is unknown.",
                ReadinessImpact.Unknown,
                snapshot.Url);
            return;
        }

        context.Flag(
            "review-decision",
            "Review decision",
            RuleOutcome.Waiting,
            "Required approval has not been recorded.",
            "approval_required",
            "Required approval is still needed.",
            ReadinessImpact.Review,
            snapshot.Url);
    }

    private static void EvaluateThreads(EvaluationContext context)
    {
        var count = context.Snapshot.UnresolvedReviewThreadCount;
        if (!context.Policy.RequireNoUnresolvedThreads)
        {
            context.Rule(
                "review-threads",
                "Review threads",
                RuleOutcome.Advisory,
                count is null
                    ? "Review thread state is unknown; policy does not require it."
                    : $"{count} unresolved thread(s); policy does not require resolution.",
                context.Snapshot.Url);
        }
        else if (count is null)
        {
            context.Flag(
                "review-threads",
                "Review threads",
                RuleOutcome.Unknown,
                "The unresolved review thread count is unknown.",
                "review_threads_unknown",
                "Unresolved review thread state is unknown.",
                ReadinessImpact.Unknown,
                context.Snapshot.Url);
        }
        else if (count == 0)
        {
            context.Rule(
                "review-threads",
                "Review threads",
                RuleOutcome.Passed,
                "No unresolved review threads remain.",
                context.Snapshot.Url);
        }
        else
        {
            var summary = $"{count} unresolved review thread(s) remain.";
            context.Flag(
                "review-threads",
                "Review threads",
                RuleOutcome.Failed,
                summary,
                "unresolved_threads",
                summary,
                ReadinessImpact.Blocked,
                context.Snapshot.Url);
        }
    }

    private static void EvaluateIssueLinks(EvaluationContext context)
    {
        var explicitLink = context.Snapshot.IssueLinks
            .Where(link => link.Kind == IssueLinkKind.Explicit)
            .OrderBy(link => link.Number)
            .FirstOrDefault();

        if (explicitLink is not null)
        {
            context.Rule(
                "issue-link",
                "Linked issue",
                RuleOutcome.Passed,
                $"Explicit issue link: #{explicitLink.Number}.",
                explicitLink.Url);
        }
        else if (!context.Policy.RequireLinkedIssue)
        {
            context.Rule(
                "issue-link",
                "Linked issue",
                RuleOutcome.Advisory,
                "No explicit issue link; policy does not require one.");
        }
        else
        {
            context.Flag(
                "issue-link",
                "Linked issue",
                RuleOutcome.Waiting,
                "An explicit linked issue is required.",
                "linked_issue_required",
                "An explicit linked issue is required.",
                ReadinessImpact.Review);
        }
    }

    private static void EvaluateFreshness(EvaluationContext context)
    {
        var snapshot = context.Snapshot;
        if (!context.Policy.RequireCurrentBranch)
        {
            context.Rule(
                "branch-freshness",
                "Branch freshness",
                snapshot.BranchFreshness == BranchFreshness.Current
                    ? RuleOutcome.Passed
                    : RuleOutcome.Advisory,
                $"Branch freshness is {snapshot.BranchFreshness}; policy does not require a current branch.",
                snapshot.Url);
            return;
        }

        switch (snapshot.BranchFreshness)
        {
            case BranchFreshness.Current:
                context.Rule(
                    "branch-freshness",
                    "Branch freshness",
                    RuleOutcome.Passed,
                    "The branch is current with its base.",
                    snapshot.Url);
                break;
            case BranchFreshness.Behind:
                context.Flag(
                    "branch-freshness",
                    "Branch freshness",
                    RuleOutcome.Failed,
                    "The branch is behind its base.",
                    "branch_behind",
                    "The branch must be updated with its base.",
                    ReadinessImpact.Blocked,
                    snapshot.Url);
                break;
            default:
                context.Flag(
                    "branch-freshness",
                    "Branch freshness",
                    RuleOutcome.Unknown,
                    "Branch freshness is unknown.",
                    "branch_freshness_unknown",
                    "Branch freshness is unknown.",
                    ReadinessImpact.Unknown,
                    snapshot.Url);
                break;
        }
    }

    private sealed class EvaluationContext(
        PullRequestSnapshot snapshot,
        RepositoryPolicy policy)
    {
        private readonly List<ReadinessBlocker> _blockers = [];
        private readonly List<RuleEvaluation> _rules = [];

        internal PullRequestSnapshot Snapshot { get; } = snapshot;

        internal RepositoryPolicy Policy { get; } = policy;

        internal void Rule(
            string id,
            string label,
            RuleOutcome outcome,
            string summary,
            string? url = null) =>
            _rules.Add(new RuleEvaluation(id, label, outcome, summary, url));

        internal void Flag(
            string ruleId,
            string label,
            RuleOutcome outcome,
            string ruleSummary,
            string blockerType,
            string blockerSummary,
            ReadinessImpact impact,
            string? url = null,
            string? subject = null)
        {
            Rule(ruleId, label, outcome, ruleSummary, url);
            _blockers.Add(new ReadinessBlocker(blockerType, blockerSummary, impact, url, subject));
        }

        internal ReadinessEvaluation Build(ReadinessStatus? status = null)
        {
            var resolvedStatus = status ?? ResolveStatus();
            return new ReadinessEvaluation(
                resolvedStatus,
                SummaryFor(resolvedStatus),
                NextActionFor(resolvedStatus, Snapshot),
                _blockers
                    .OrderBy(blocker => blocker.Type, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(blocker => blocker.Summary, StringComparer.Ordinal)
                    .ToArray(),
                _rules
                    .OrderBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(rule => rule.Id, StringComparer.Ordinal)
                    .ToArray(),
                Snapshot.FetchedAt,
                Policy.Version);
        }

        private ReadinessStatus ResolveStatus()
        {
            if (_blockers.Any(blocker => blocker.Impact == ReadinessImpact.Blocked))
            {
                return ReadinessStatus.Blocked;
            }

            if (_blockers.Any(blocker => blocker.Impact == ReadinessImpact.Unknown))
            {
                return ReadinessStatus.Unknown;
            }

            return _blockers.Any(blocker => blocker.Impact == ReadinessImpact.Review)
                ? ReadinessStatus.Review
                : ReadinessStatus.Go;
        }
    }

    private sealed record CheckFinding(
        RuleOutcome Outcome,
        string Type,
        string Summary,
        ReadinessImpact Impact)
    {
        internal static CheckFinding For(CheckSnapshot check) => check.State switch
        {
            CheckState.Failure => new(
                RuleOutcome.Failed,
                "ci_failed",
                $"Required check failed: {check.Name}.",
                ReadinessImpact.Blocked),
            CheckState.Pending => new(
                RuleOutcome.Waiting,
                "ci_pending",
                $"Check is still running: {check.Name}.",
                ReadinessImpact.Review),
            CheckState.Cancelled => new(
                RuleOutcome.Failed,
                "ci_cancelled",
                $"Required check was cancelled: {check.Name}.",
                ReadinessImpact.Blocked),
            CheckState.ActionRequired => new(
                RuleOutcome.Failed,
                "ci_action_required",
                $"Check requires action or approval: {check.Name}.",
                ReadinessImpact.Blocked),
            CheckState.NotExecuted => new(
                RuleOutcome.Waiting,
                "ci_not_executed",
                $"Check has not executed: {check.Name}.",
                ReadinessImpact.Review),
            _ => new(
                RuleOutcome.Unknown,
                "ci_unknown",
                $"Check state is unknown: {check.Name}.",
                ReadinessImpact.Unknown),
        };
    }

    private static string SummaryFor(ReadinessStatus status) => status switch
    {
        ReadinessStatus.Go => "All configured readiness gates are satisfied.",
        ReadinessStatus.Review => "The change is waiting for one or more review gates.",
        ReadinessStatus.Blocked => "The change has one or more merge blockers.",
        ReadinessStatus.Draft => "The pull request is still a draft.",
        _ => "Required readiness evidence is incomplete or unknown.",
    };

    private static string NextActionFor(
        ReadinessStatus status,
        PullRequestSnapshot snapshot) => status switch
        {
            ReadinessStatus.Go when snapshot.Mergeability == Mergeability.Conflicting =>
                "Ready for review; resolve the merge conflict before merge.",
            ReadinessStatus.Go when snapshot.Mergeability == Mergeability.Unknown =>
                "Ready for review; confirm mergeability before merge.",
            ReadinessStatus.Go => "Ready for maintainer review or merge.",
            ReadinessStatus.Review => "Complete the pending review gates before merge consideration.",
            ReadinessStatus.Blocked => "Resolve every blocking gate before merge consideration.",
            ReadinessStatus.Draft => "Mark the pull request ready for review when the change is complete.",
            _ => "Refresh the pull request evidence before merge consideration.",
        };
}
