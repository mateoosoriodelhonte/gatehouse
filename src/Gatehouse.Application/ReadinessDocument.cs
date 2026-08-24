using System.Text.Json;
using System.Text.Json.Serialization;
using Gatehouse.Domain;

namespace Gatehouse.Application;

public sealed record ReadinessDocument(
    string SchemaVersion,
    string Repository,
    int PullRequest,
    string Status,
    string Summary,
    string NextAction,
    DateTimeOffset EvaluatedAt,
    int PolicyVersion,
    IReadOnlyList<ReadinessBlockerDocument> Blockers,
    IReadOnlyList<ReadinessEvidenceDocument> Evidence);

public sealed record ReadinessBlockerDocument(
    string Type,
    string Impact,
    string Summary,
    string? Check,
    string? Url);

public sealed record ReadinessEvidenceDocument(
    string Id,
    string Label,
    string Outcome,
    string Summary,
    string? Url);

public static class ReadinessDocumentFactory
{
    public const string SchemaVersion = "1.0";

    public static ReadinessDocument Create(
        PullRequestSnapshot snapshot,
        ReadinessEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(evaluation);

        var blockers = evaluation.Blockers
            .Select(blocker => new ReadinessBlockerDocument(
                blocker.Type,
                Lower(blocker.Impact),
                blocker.Summary,
                blocker.Type.StartsWith("ci_", StringComparison.Ordinal) ? blocker.Subject : null,
                blocker.EvidenceUrl))
            .ToArray();
        var evidence = evaluation.Rules
            .Select(rule => new ReadinessEvidenceDocument(
                rule.Id,
                rule.Label,
                Lower(rule.Outcome),
                rule.Summary,
                rule.EvidenceUrl))
            .ToArray();

        return new ReadinessDocument(
            SchemaVersion,
            snapshot.Repository.ToString(),
            snapshot.Number,
            Lower(evaluation.Status),
            evaluation.Summary,
            ResolveNextAction(evaluation),
            evaluation.EvaluatedAt,
            evaluation.PolicyVersion,
            blockers,
            evidence);
    }

    private static string ResolveNextAction(ReadinessEvaluation evaluation)
    {
        var first = evaluation.Blockers.Count == 0 ? null : evaluation.Blockers[0];
        return first?.Type switch
        {
            "ci_failed" => $"Fix failing required check: {first.Subject}",
            "ci_cancelled" => $"Rerun cancelled required check: {first.Subject}",
            "ci_action_required" => $"Approve or act on check: {first.Subject}",
            "merge_conflict" => "Resolve the merge conflict",
            "changes_requested" => "Address the requested review changes",
            "approval_required" => "Get the required approval",
            "unresolved_threads" => "Resolve the open review threads",
            "branch_behind" => "Update the branch with its base",
            "linked_issue_required" => "Link the pull request to an issue",
            _ => evaluation.NextAction,
        };
    }

    private static string Lower<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}

public static class ReadinessJson
{
    private static readonly JsonSerializerOptions IndentedOptions = CreateOptions(true);
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(false);

    public static string Serialize(ReadinessDocument document, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(document);

        return JsonSerializer.Serialize(document, indented ? IndentedOptions : CompactOptions);
    }

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = indented,
    };
}
