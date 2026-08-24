namespace Gatehouse.Domain;

public sealed record RuleEvaluation(
    string Id,
    string Label,
    RuleOutcome Outcome,
    string Summary,
    string? EvidenceUrl = null);

public sealed record ReadinessBlocker(
    string Type,
    string Summary,
    ReadinessImpact Impact,
    string? EvidenceUrl = null,
    string? Subject = null);

public sealed record ReadinessEvaluation(
    ReadinessStatus Status,
    string Summary,
    string NextAction,
    IReadOnlyList<ReadinessBlocker> Blockers,
    IReadOnlyList<RuleEvaluation> Rules,
    DateTimeOffset EvaluatedAt,
    int PolicyVersion);
