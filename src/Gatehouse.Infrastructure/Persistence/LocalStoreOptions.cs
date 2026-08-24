namespace Gatehouse.Infrastructure.Persistence;

public sealed class LocalStoreOptions
{
    public int FreshnessMinutes { get; init; } = 15;

    public int RetentionDays { get; init; } = 30;

    public int MaxSnapshotsPerPullRequest { get; init; } = 50;
}
