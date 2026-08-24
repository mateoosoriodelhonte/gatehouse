namespace Gatehouse.Domain;

public sealed record RepositoryPolicy
{
    public static RepositoryPolicy SafeDefaults { get; } = new();

    public int Version { get; init; } = 1;

    public bool RequireLinkedIssue { get; init; }

    public bool RequireAllChecks { get; init; } = true;

    public bool RequireApproval { get; init; } = true;

    public bool RequireNoUnresolvedThreads { get; init; } = true;

    public bool RequireMergeable { get; init; } = true;

    public bool RequireCurrentBranch { get; init; }

    public bool BlockOnChangesRequested { get; init; } = true;
}
