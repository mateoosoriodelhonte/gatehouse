namespace Gatehouse.Domain;

public static class ReadinessReportGenerator
{
    public static string Generate(
        PullRequestSnapshot snapshot,
        ReadinessEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(evaluation);

        return evaluation.Status switch
        {
            ReadinessStatus.Go => GenerateGoReport(snapshot, evaluation),
            ReadinessStatus.Blocked => GenerateBlockedReport(snapshot, evaluation),
            ReadinessStatus.Review => GenerateReviewReport(snapshot, evaluation),
            ReadinessStatus.Draft =>
                $"DRAFT.{Environment.NewLine}PR #{snapshot.Number} is not ready for review.{Environment.NewLine}" +
                "Recommendation: finish the change and mark it ready for review.",
            _ =>
                $"UNKNOWN.{Environment.NewLine}PR #{snapshot.Number} does not have enough evidence for a readiness decision." +
                $"{Environment.NewLine}Recommendation: refresh GitHub evidence and try again.",
        };
    }

    private static string GenerateGoReport(
        PullRequestSnapshot snapshot,
        ReadinessEvaluation evaluation)
    {
        var checksLine = snapshot.Checks.Count == 0
            ? "No checks were reported."
            : "All required checks are green.";
        var linkedIssue = snapshot.IssueLinks
            .Where(link => link.Kind == IssueLinkKind.Explicit)
            .OrderBy(link => link.Number)
            .FirstOrDefault();
        var issueLine = linkedIssue is null
            ? "No explicit issue link was reported."
            : linkedIssue.IsClosed
                ? $"Linked issue: #{linkedIssue.Number} (closed)."
                : $"Linked issue: #{linkedIssue.Number}.";
        var mergeabilityLine = snapshot.Mergeability switch
        {
            Mergeability.Clean => "Mergeability: clean.",
            Mergeability.Conflicting => "Mergeability: conflict (advisory).",
            _ => "Mergeability: unknown (advisory).",
        };
        var freshnessLine = snapshot.BranchFreshness switch
        {
            BranchFreshness.Current => "Branch freshness: current.",
            BranchFreshness.Behind => "Branch freshness: behind (advisory).",
            _ => "Branch freshness: unknown (advisory).",
        };

        return string.Join(
            Environment.NewLine,
            "GO for review.",
            $"PR #{snapshot.Number} is open and non-draft.",
            mergeabilityLine,
            freshnessLine,
            checksLine,
            "Required review is satisfied.",
            "No unresolved review threads remain.",
            issueLine,
            $"Recommendation: {LowercaseFirst(evaluation.NextAction)}");
    }

    private static string GenerateBlockedReport(
        PullRequestSnapshot snapshot,
        ReadinessEvaluation evaluation)
    {
        var blockers = evaluation.Blockers
            .Where(blocker => blocker.Impact == ReadinessImpact.Blocked)
            .ToArray();
        var noun = blockers.Length == 1 ? "merge blocker" : "merge blockers";
        var lines = new List<string>
        {
            "NO-GO.",
            $"PR #{snapshot.Number} has {blockers.Length} {noun}:",
        };

        lines.AddRange(blockers.Select(blocker => $"- {blocker.Summary}"));
        lines.Add(blockers.Length switch
        {
            1 => "Do not merge until this blocker is resolved.",
            2 => "Do not merge until both blockers are resolved.",
            _ => "Do not merge until all blockers are resolved.",
        });

        return string.Join(Environment.NewLine, lines);
    }

    private static string GenerateReviewReport(
        PullRequestSnapshot snapshot,
        ReadinessEvaluation evaluation)
    {
        var waiting = evaluation.Blockers
            .Where(blocker => blocker.Impact == ReadinessImpact.Review)
            .Select(blocker => $"- {blocker.Summary}");

        return string.Join(
            Environment.NewLine,
            new[]
            {
                "REVIEW.",
                $"PR #{snapshot.Number} is waiting on review gates:",
            }.Concat(waiting).Append(
                "Recommendation: complete the listed gates before merge consideration."));
    }

    private static string LowercaseFirst(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
