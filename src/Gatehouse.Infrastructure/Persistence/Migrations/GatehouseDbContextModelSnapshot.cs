using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

#nullable disable

namespace Gatehouse.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatehouseDbContext))]
public sealed class GatehouseDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.11");

        modelBuilder.Entity("Gatehouse.Infrastructure.Persistence.ReadinessSnapshotRecord", entity =>
        {
            entity.Property<long>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER")
                .HasAnnotation("Sqlite:Autoincrement", true);
            entity.Property<string>("EvaluationJson").IsRequired().HasColumnType("TEXT");
            entity.Property<long>("FetchedAtUnixMilliseconds").HasColumnType("INTEGER");
            entity.Property<long>("GitHubUpdatedAtUnixMilliseconds").HasColumnType("INTEGER");
            entity.Property<int>("PolicyVersion").HasColumnType("INTEGER");
            entity.Property<int>("PullRequestNumber").HasColumnType("INTEGER");
            entity.Property<Guid>("RefreshId").HasColumnType("TEXT");
            entity.Property<string>("ReportMarkdown").IsRequired().HasColumnType("TEXT");
            entity.Property<Guid>("RepositoryId").HasColumnType("TEXT");
            entity.Property<string>("SnapshotJson").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("Status").IsRequired().HasMaxLength(20).HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.HasIndex("RepositoryId", "PullRequestNumber", "FetchedAtUnixMilliseconds");
            entity.HasIndex("RepositoryId", "RefreshId", "PullRequestNumber");
            entity.ToTable("ReadinessSnapshots");
        });

        modelBuilder.Entity("Gatehouse.Infrastructure.Persistence.RepositoryRecord", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            entity.Property<long>("AddedAtUnixMilliseconds").HasColumnType("INTEGER");
            entity.Property<Guid?>("CurrentRefreshId").HasColumnType("TEXT");
            entity.Property<string>("ETag").HasColumnType("TEXT");
            entity.Property<bool>("IsSelected").HasColumnType("INTEGER");
            entity.Property<int?>("LastFetchStatus").HasColumnType("INTEGER");
            entity.Property<long?>("LastRefreshAttemptAtUnixMilliseconds").HasColumnType("INTEGER");
            entity.Property<long?>("LastSuccessfulRefreshAtUnixMilliseconds").HasColumnType("INTEGER");
            entity.Property<string>("Name").IsRequired().HasMaxLength(100)
                .HasColumnType("TEXT").UseCollation("NOCASE");
            entity.Property<string>("Owner").IsRequired().HasMaxLength(100)
                .HasColumnType("TEXT").UseCollation("NOCASE");
            entity.Property<string>("PolicyJson").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("WarningsJson").IsRequired().HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.HasIndex("IsSelected");
            entity.HasIndex("Owner", "Name").IsUnique();
            entity.ToTable("Repositories");
        });

        modelBuilder.Entity("Gatehouse.Infrastructure.Persistence.ReadinessSnapshotRecord", entity =>
        {
            entity.HasOne("Gatehouse.Infrastructure.Persistence.RepositoryRecord", "Repository")
                .WithMany("Snapshots")
                .HasForeignKey("RepositoryId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.Navigation("Repository");
        });

        modelBuilder.Entity("Gatehouse.Infrastructure.Persistence.RepositoryRecord", entity =>
        {
            entity.Navigation("Snapshots");
        });
    }
}
