namespace Gatehouse.Domain;

public sealed record CheckSnapshot(
    string Name,
    CheckState State,
    bool IsRequired,
    string? Url);

public sealed record IssueLink(
    int Number,
    IssueLinkKind Kind,
    bool IsClosed,
    string? Url);

public sealed record ChangedFile(
    string Path,
    string ChangeType,
    int Additions,
    int Deletions,
    string? Url);

public sealed record PullRequestSnapshot
{
    public required RepositorySlug Repository { get; init; }

    public required int Number { get; init; }

    public required string Title { get; init; }

    public required string Author { get; init; }

    public required PullRequestState State { get; init; }

    public required bool IsDraft { get; init; }

    public required Mergeability Mergeability { get; init; }

    public required ReviewDecision ReviewDecision { get; init; }

    public required int ApprovalCount { get; init; }

    public required int RequestedReviewerCount { get; init; }

    public IReadOnlyList<string> RequestedReviewers { get; init; } = [];

    public required int? UnresolvedReviewThreadCount { get; init; }

    public required BranchFreshness BranchFreshness { get; init; }

    public required IReadOnlyList<CheckSnapshot> Checks { get; init; }

    public required IReadOnlyList<IssueLink> IssueLinks { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    public required DateTimeOffset UpdatedAt { get; init; }

    public required DateTimeOffset FetchedAt { get; init; }

    public required string Url { get; init; }

    public required string BaseBranch { get; init; }

    public required string HeadBranch { get; init; }

    public required string BaseSha { get; init; }

    public required string HeadSha { get; init; }

    public required int ChangedFiles { get; init; }

    public required int Additions { get; init; }

    public required int Deletions { get; init; }

    public required IReadOnlyList<ChangedFile> Files { get; init; }
}
