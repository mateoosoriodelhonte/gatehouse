namespace Gatehouse.Domain;

public enum PullRequestState
{
    Unknown,
    Open,
    Closed,
    Merged,
}

public enum ReadinessStatus
{
    Go,
    Review,
    Blocked,
    Draft,
    Unknown,
}

public enum Mergeability
{
    Unknown,
    Clean,
    Conflicting,
}

public enum CheckState
{
    Unknown,
    Success,
    Failure,
    Pending,
    Cancelled,
    ActionRequired,
    NotExecuted,
    Skipped,
    Neutral,
}

public enum ReviewDecision
{
    Unknown,
    Approved,
    ChangesRequested,
    ReviewRequired,
}

public enum BranchFreshness
{
    Unknown,
    Current,
    Behind,
}

public enum IssueLinkKind
{
    Explicit,
    PossibleReference,
}

public enum RuleOutcome
{
    Passed,
    Failed,
    Waiting,
    Unknown,
    Advisory,
}

public enum ReadinessImpact
{
    Blocked,
    Review,
    Unknown,
}
