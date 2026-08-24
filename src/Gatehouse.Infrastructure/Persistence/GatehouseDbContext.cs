using Microsoft.EntityFrameworkCore;

namespace Gatehouse.Infrastructure.Persistence;

public sealed class GatehouseDbContext(DbContextOptions<GatehouseDbContext> options)
    : DbContext(options)
{
    public DbSet<RepositoryRecord> Repositories => Set<RepositoryRecord>();

    public DbSet<ReadinessSnapshotRecord> Snapshots => Set<ReadinessSnapshotRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<RepositoryRecord>(entity =>
        {
            entity.ToTable("Repositories");
            entity.HasKey(repository => repository.Id);
            entity.Property(repository => repository.Owner)
                .HasMaxLength(100)
                .UseCollation("NOCASE");
            entity.Property(repository => repository.Name)
                .HasMaxLength(100)
                .UseCollation("NOCASE");
            entity.Property(repository => repository.PolicyJson).IsRequired();
            entity.Property(repository => repository.WarningsJson).IsRequired();
            entity.HasIndex(repository => new { repository.Owner, repository.Name })
                .IsUnique();
            entity.HasIndex(repository => repository.IsSelected);
            entity.HasMany(repository => repository.Snapshots)
                .WithOne(snapshot => snapshot.Repository)
                .HasForeignKey(snapshot => snapshot.RepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReadinessSnapshotRecord>(entity =>
        {
            entity.ToTable("ReadinessSnapshots");
            entity.HasKey(snapshot => snapshot.Id);
            entity.Property(snapshot => snapshot.Status).HasMaxLength(20);
            entity.Property(snapshot => snapshot.SnapshotJson).IsRequired();
            entity.Property(snapshot => snapshot.EvaluationJson).IsRequired();
            entity.Property(snapshot => snapshot.ReportMarkdown).IsRequired();
            entity.HasIndex(snapshot => new
            {
                snapshot.RepositoryId,
                snapshot.PullRequestNumber,
                snapshot.FetchedAtUnixMilliseconds,
            });
            entity.HasIndex(snapshot => new
            {
                snapshot.RepositoryId,
                snapshot.RefreshId,
                snapshot.PullRequestNumber,
            });
        });
    }
}

public sealed class RepositoryRecord
{
    public Guid Id { get; set; }

    public required string Owner { get; set; }

    public required string Name { get; set; }

    public required string PolicyJson { get; set; }

    public bool IsSelected { get; set; }

    public long AddedAtUnixMilliseconds { get; set; }

    public long? LastRefreshAttemptAtUnixMilliseconds { get; set; }

    public long? LastSuccessfulRefreshAtUnixMilliseconds { get; set; }

    public int? LastFetchStatus { get; set; }

    public string? ETag { get; set; }

    public Guid? CurrentRefreshId { get; set; }

    public required string WarningsJson { get; set; }

    public ICollection<ReadinessSnapshotRecord> Snapshots { get; } =
        new List<ReadinessSnapshotRecord>();
}

public sealed class ReadinessSnapshotRecord
{
    public long Id { get; set; }

    public Guid RepositoryId { get; set; }

    public Guid RefreshId { get; set; }

    public RepositoryRecord? Repository { get; set; }

    public int PullRequestNumber { get; set; }

    public long FetchedAtUnixMilliseconds { get; set; }

    public long GitHubUpdatedAtUnixMilliseconds { get; set; }

    public int PolicyVersion { get; set; }

    public required string Status { get; set; }

    public required string SnapshotJson { get; set; }

    public required string EvaluationJson { get; set; }

    public required string ReportMarkdown { get; set; }
}
