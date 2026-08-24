using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gatehouse.Application;
using Gatehouse.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gatehouse.Infrastructure.Persistence;

public sealed class LocalReadinessStore : ILocalReadinessStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IDbContextFactory<GatehouseDbContext> contextFactory;
    private readonly IPullRequestSource pullRequestSource;
    private readonly LocalStoreOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> refreshLocks = new();

    public LocalReadinessStore(
        IDbContextFactory<GatehouseDbContext> contextFactory,
        IPullRequestSource pullRequestSource,
        LocalStoreOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(pullRequestSource);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        this.contextFactory = contextFactory;
        this.pullRequestSource = pullRequestSource;
        this.options = options;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LocalRepositorySummary> AddRepositoryAsync(
        RepositoryRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ValidateRegistration(registration);
        var owner = registration.Owner.Trim();
        var name = registration.Name.Trim();

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Repositories.AnyAsync(
            repository => repository.Owner == owner && repository.Name == name,
            cancellationToken))
        {
            throw new DuplicateRepositoryException(owner, name);
        }

        var now = timeProvider.GetUtcNow();
        var repository = new RepositoryRecord
        {
            Id = Guid.NewGuid(),
            Owner = owner,
            Name = name,
            PolicyJson = Serialize(registration.Policy),
            IsSelected = !await db.Repositories.AnyAsync(cancellationToken),
            AddedAtUnixMilliseconds = now.ToUnixTimeMilliseconds(),
            WarningsJson = "[]",
        };
        db.Repositories.Add(repository);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            throw new DuplicateRepositoryException(owner, name);
        }

        return await CreateSummaryAsync(db, repository, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalRepositorySummary>> ListRepositoriesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var repositories = await db.Repositories.AsNoTracking()
            .OrderByDescending(repository => repository.IsSelected)
            .ThenBy(repository => repository.Owner)
            .ThenBy(repository => repository.Name)
            .ToArrayAsync(cancellationToken);
        var summaries = new List<LocalRepositorySummary>(repositories.Length);
        foreach (var repository in repositories)
        {
            summaries.Add(await CreateSummaryAsync(db, repository, cancellationToken));
        }

        return summaries;
    }

    public async Task<LocalRepositoryDetail?> GetRepositoryAsync(
        Guid repositoryId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await GetRepositoryAsync(db, repositoryId, cancellationToken);
    }

    public async Task<bool> SelectRepositoryAsync(
        Guid repositoryId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var repositories = await db.Repositories.ToArrayAsync(cancellationToken);
        var selected = repositories.SingleOrDefault(repository => repository.Id == repositoryId);
        if (selected is null)
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var repository in repositories)
        {
            repository.IsSelected = repository.Id == repositoryId;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdatePolicyAsync(
        Guid repositoryId,
        RepositoryPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (!RepositoryInputValidator.TryValidatePolicy(policy, out var policyError))
        {
            throw new ArgumentException(policyError, nameof(policy));
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var repository = await db.Repositories.SingleOrDefaultAsync(
            item => item.Id == repositoryId,
            cancellationToken);
        if (repository is null)
        {
            return false;
        }

        repository.PolicyJson = Serialize(policy);
        if (repository.CurrentRefreshId is { } currentRefreshId)
        {
            var currentRecords = await db.Snapshots.AsNoTracking()
                .Where(snapshot =>
                    snapshot.RepositoryId == repositoryId &&
                    snapshot.RefreshId == currentRefreshId)
                .OrderBy(snapshot => snapshot.PullRequestNumber)
                .ToArrayAsync(cancellationToken);
            var policyRefreshId = Guid.NewGuid();
            foreach (var currentRecord in currentRecords)
            {
                var snapshot = Deserialize<PullRequestSnapshot>(currentRecord.SnapshotJson);
                db.Snapshots.Add(CreateSnapshotRecord(
                    repositoryId,
                    policyRefreshId,
                    snapshot,
                    policy));
            }

            repository.CurrentRefreshId = policyRefreshId;
        }

        await db.SaveChangesAsync(cancellationToken);
        await PruneSnapshotsAsync(db, repositoryId, cancellationToken);
        return true;
    }

    public async Task<RepositoryRefreshResult?> RefreshRepositoryAsync(
        Guid repositoryId,
        CancellationToken cancellationToken = default)
    {
        var refreshLock = refreshLocks.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            await using var readDb = await contextFactory.CreateDbContextAsync(cancellationToken);
            var current = await readDb.Repositories.AsNoTracking().SingleOrDefaultAsync(
                repository => repository.Id == repositoryId,
                cancellationToken);
            if (current is null)
            {
                return null;
            }

            var fetch = await pullRequestSource.GetOpenPullRequestsAsync(
                new RepositorySlug(current.Owner, current.Name),
                current.ETag,
                cancellationToken);
            var now = timeProvider.GetUtcNow();

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var repository = await db.Repositories.SingleAsync(
                item => item.Id == repositoryId,
                cancellationToken);
            repository.LastRefreshAttemptAtUnixMilliseconds = now.ToUnixTimeMilliseconds();
            repository.LastFetchStatus = (int)fetch.Status;
            repository.WarningsJson = SerializeWarnings(fetch.Warnings);

            var storedCount = 0;
            if (fetch.Status == PullRequestFetchStatus.Success)
            {
                var policy = Deserialize<RepositoryPolicy>(repository.PolicyJson);
                var refreshId = Guid.NewGuid();
                foreach (var snapshot in fetch.PullRequests)
                {
                    db.Snapshots.Add(CreateSnapshotRecord(
                        repositoryId,
                        refreshId,
                        snapshot,
                        policy));
                    storedCount++;
                }

                repository.CurrentRefreshId = refreshId;
                repository.LastSuccessfulRefreshAtUnixMilliseconds = now.ToUnixTimeMilliseconds();
                repository.ETag = fetch.ETag;
            }
            else if (fetch.Status == PullRequestFetchStatus.NotModified)
            {
                repository.LastSuccessfulRefreshAtUnixMilliseconds = now.ToUnixTimeMilliseconds();
                repository.ETag = fetch.ETag ?? repository.ETag;
            }

            await db.SaveChangesAsync(cancellationToken);
            await PruneSnapshotsAsync(db, repositoryId, cancellationToken);

            var detail = await GetRepositoryAsync(db, repositoryId, cancellationToken)
                ?? throw new InvalidOperationException("The refreshed repository was not found.");
            return new RepositoryRefreshResult(
                fetch.Status,
                storedCount,
                detail,
                fetch.RateLimit,
                fetch.Warnings);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public async Task<bool> RemoveRepositoryAsync(
        Guid repositoryId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var repository = await db.Repositories.SingleOrDefaultAsync(
            item => item.Id == repositoryId,
            cancellationToken);
        if (repository is null)
        {
            return false;
        }

        var wasSelected = repository.IsSelected;
        db.Repositories.Remove(repository);
        await db.SaveChangesAsync(cancellationToken);
        if (wasSelected)
        {
            var next = await db.Repositories.OrderBy(item => item.AddedAtUnixMilliseconds)
                .FirstOrDefaultAsync(cancellationToken);
            if (next is not null)
            {
                next.IsSelected = true;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        refreshLocks.TryRemove(repositoryId, out _);
        return true;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Repositories.ExecuteDeleteAsync(cancellationToken);
        refreshLocks.Clear();
    }

    private async Task<LocalRepositoryDetail?> GetRepositoryAsync(
        GatehouseDbContext db,
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        var repository = await db.Repositories.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == repositoryId,
            cancellationToken);
        if (repository is null)
        {
            return null;
        }

        ReadinessSnapshotRecord[] records = [];
        if (repository.CurrentRefreshId is { } currentRefreshId)
        {
            records = await db.Snapshots.AsNoTracking()
                .Where(snapshot =>
                    snapshot.RepositoryId == repositoryId &&
                    snapshot.RefreshId == currentRefreshId)
                .OrderBy(snapshot => snapshot.PullRequestNumber)
                .ToArrayAsync(cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var snapshots = records.Select(record =>
        {
            var snapshot = Deserialize<PullRequestSnapshot>(record.SnapshotJson);
            var evaluation = Deserialize<ReadinessEvaluation>(record.EvaluationJson);
            var age = now - snapshot.FetchedAt;
            var ageSeconds = Math.Max(0, (long)age.TotalSeconds);
            return new CachedPullRequestReadiness(
                snapshot,
                evaluation,
                record.ReportMarkdown,
                ageSeconds,
                age > TimeSpan.FromMinutes(options.FreshnessMinutes));
        }).ToArray();

        var summary = CreateSummary(repository, snapshots.Length);
        return new LocalRepositoryDetail(
            summary,
            Deserialize<RepositoryPolicy>(repository.PolicyJson),
            snapshots);
    }

    private static async Task<LocalRepositorySummary> CreateSummaryAsync(
        GatehouseDbContext db,
        RepositoryRecord repository,
        CancellationToken cancellationToken)
    {
        var count = repository.CurrentRefreshId is { } refreshId
            ? await db.Snapshots.CountAsync(
                snapshot =>
                    snapshot.RepositoryId == repository.Id &&
                    snapshot.RefreshId == refreshId,
                cancellationToken)
            : 0;
        return CreateSummary(repository, count);
    }

    private static LocalRepositorySummary CreateSummary(
        RepositoryRecord repository,
        int cachedPullRequestCount) =>
        new(
            repository.Id,
            repository.Owner,
            repository.Name,
            repository.IsSelected,
            FromUnixTime(repository.AddedAtUnixMilliseconds),
            FromUnixTime(repository.LastRefreshAttemptAtUnixMilliseconds),
            FromUnixTime(repository.LastSuccessfulRefreshAtUnixMilliseconds),
            ParseFetchStatus(repository.LastFetchStatus),
            cachedPullRequestCount,
            DeserializeWarnings(repository.WarningsJson));

    private static ReadinessSnapshotRecord CreateSnapshotRecord(
        Guid repositoryId,
        Guid refreshId,
        PullRequestSnapshot snapshot,
        RepositoryPolicy policy)
    {
        var evaluation = ReadinessEngine.Evaluate(snapshot, policy);
        return new ReadinessSnapshotRecord
        {
            RepositoryId = repositoryId,
            RefreshId = refreshId,
            PullRequestNumber = snapshot.Number,
            FetchedAtUnixMilliseconds = snapshot.FetchedAt.ToUnixTimeMilliseconds(),
            GitHubUpdatedAtUnixMilliseconds = snapshot.UpdatedAt.ToUnixTimeMilliseconds(),
            PolicyVersion = policy.Version,
            Status = evaluation.Status.ToString(),
            SnapshotJson = Serialize(snapshot),
            EvaluationJson = Serialize(evaluation),
            ReportMarkdown = ReadinessReportGenerator.Generate(snapshot, evaluation),
        };
    }

    private async Task PruneSnapshotsAsync(
        GatehouseDbContext db,
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-options.RetentionDays)
            .ToUnixTimeMilliseconds();
        var currentRefreshId = await db.Repositories
            .Where(repository => repository.Id == repositoryId)
            .Select(repository => repository.CurrentRefreshId)
            .SingleAsync(cancellationToken);
        var records = await db.Snapshots
            .Where(snapshot => snapshot.RepositoryId == repositoryId)
            .OrderByDescending(snapshot => snapshot.FetchedAtUnixMilliseconds)
            .ThenByDescending(snapshot => snapshot.Id)
            .ToArrayAsync(cancellationToken);
        var expired = records.Where(snapshot =>
            snapshot.RefreshId != currentRefreshId &&
            snapshot.FetchedAtUnixMilliseconds < cutoff);
        var overLimit = records.GroupBy(snapshot => snapshot.PullRequestNumber)
            .SelectMany(group => group.Skip(options.MaxSnapshotsPerPullRequest));
        db.Snapshots.RemoveRange(expired.Concat(overLimit).DistinctBy(snapshot => snapshot.Id));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException("Local Gatehouse data is invalid.");

    private static string SerializeWarnings(IReadOnlyList<string> warnings) =>
        Serialize(warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Select(warning => warning.Length <= 500 ? warning : warning[..500])
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray());

    private static string[] DeserializeWarnings(string json)
    {
        try
        {
            return Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return ["Saved warning data could not be read."];
        }
    }

    private static DateTimeOffset FromUnixTime(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static DateTimeOffset? FromUnixTime(long? value) =>
        value is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);

    private static PullRequestFetchStatus? ParseFetchStatus(int? value) =>
        value is not null && Enum.IsDefined((PullRequestFetchStatus)value.Value)
            ? (PullRequestFetchStatus)value.Value
            : null;

    private static void ValidateRegistration(RepositoryRegistration registration)
    {
        if (!RepositoryInputValidator.TryValidateRepository(
            registration.Owner,
            registration.Name,
            out var repositoryError))
        {
            throw new ArgumentException(repositoryError, nameof(registration));
        }

        if (!RepositoryInputValidator.TryValidatePolicy(registration.Policy, out var policyError))
        {
            throw new ArgumentException(policyError, nameof(registration));
        }
    }

    private static void ValidateOptions(LocalStoreOptions options)
    {
        if (options.FreshnessMinutes is < 1 or > 1440 ||
            options.RetentionDays is < 1 or > 3650 ||
            options.MaxSnapshotsPerPullRequest is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Local store retention and freshness limits are invalid.");
        }
    }
}
