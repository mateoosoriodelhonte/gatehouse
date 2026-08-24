using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gatehouse.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatehouseDbContext))]
[Migration("20260824170000_InitialLocalStore")]
public sealed class InitialLocalStore : Migration
{
    private static readonly string[] SnapshotIndexColumns =
        ["RepositoryId", "PullRequestNumber", "FetchedAtUnixMilliseconds"];

    private static readonly string[] CurrentSnapshotIndexColumns =
        ["RepositoryId", "RefreshId", "PullRequestNumber"];

    private static readonly string[] RepositoryNameIndexColumns = ["Owner", "Name"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Repositories",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Owner = table.Column<string>(
                    type: "TEXT",
                    maxLength: 100,
                    nullable: false,
                    collation: "NOCASE"),
                Name = table.Column<string>(
                    type: "TEXT",
                    maxLength: 100,
                    nullable: false,
                    collation: "NOCASE"),
                PolicyJson = table.Column<string>(type: "TEXT", nullable: false),
                IsSelected = table.Column<bool>(type: "INTEGER", nullable: false),
                AddedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                LastRefreshAttemptAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                LastSuccessfulRefreshAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                LastFetchStatus = table.Column<int>(type: "INTEGER", nullable: true),
                ETag = table.Column<string>(type: "TEXT", nullable: true),
                CurrentRefreshId = table.Column<Guid>(type: "TEXT", nullable: true),
                WarningsJson = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Repositories", item => item.Id);
            });

        migrationBuilder.CreateTable(
            name: "ReadinessSnapshots",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                RefreshId = table.Column<Guid>(type: "TEXT", nullable: false),
                PullRequestNumber = table.Column<int>(type: "INTEGER", nullable: false),
                FetchedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                GitHubUpdatedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                PolicyVersion = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                EvaluationJson = table.Column<string>(type: "TEXT", nullable: false),
                ReportMarkdown = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReadinessSnapshots", item => item.Id);
                table.ForeignKey(
                    name: "FK_ReadinessSnapshots_Repositories_RepositoryId",
                    column: item => item.RepositoryId,
                    principalTable: "Repositories",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ReadinessSnapshots_RepositoryId_PullRequestNumber_FetchedAtUnixMilliseconds",
            table: "ReadinessSnapshots",
            columns: SnapshotIndexColumns);

        migrationBuilder.CreateIndex(
            name: "IX_ReadinessSnapshots_RepositoryId_RefreshId_PullRequestNumber",
            table: "ReadinessSnapshots",
            columns: CurrentSnapshotIndexColumns);

        migrationBuilder.CreateIndex(
            name: "IX_Repositories_IsSelected",
            table: "Repositories",
            column: "IsSelected");

        migrationBuilder.CreateIndex(
            name: "IX_Repositories_Owner_Name",
            table: "Repositories",
            columns: RepositoryNameIndexColumns,
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ReadinessSnapshots");
        migrationBuilder.DropTable(name: "Repositories");
    }
}
