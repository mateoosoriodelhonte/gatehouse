using System.Text.Json;
using Gatehouse.Application;
using Gatehouse.Domain;

namespace Gatehouse.Application.Tests;

public sealed class ReadinessDocumentTests
{
    [Fact]
    public void Creates_versioned_agent_friendly_document()
    {
        var snapshot = CreateSnapshot();
        var evaluation = ReadinessEngine.Evaluate(snapshot, RepositoryPolicy.SafeDefaults);

        var document = ReadinessDocumentFactory.Create(snapshot, evaluation);

        Assert.Equal("1.0", document.SchemaVersion);
        Assert.Equal("acme/payments", document.Repository);
        Assert.Equal(142, document.PullRequest);
        Assert.Equal("blocked", document.Status);
        Assert.Equal("Fix failing required check: build", document.NextAction);
        Assert.Collection(
            document.Blockers,
            blocker =>
            {
                Assert.Equal("ci_failed", blocker.Type);
                Assert.Equal("blocked", blocker.Impact);
                Assert.Equal("build", blocker.Check);
            });
        Assert.NotEmpty(document.Evidence);
    }

    [Fact]
    public void Serializes_stable_camel_case_json_without_enum_numbers()
    {
        var snapshot = CreateSnapshot();
        var evaluation = ReadinessEngine.Evaluate(snapshot, RepositoryPolicy.SafeDefaults);
        var document = ReadinessDocumentFactory.Create(snapshot, evaluation);

        var json = ReadinessJson.Serialize(document, indented: false);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal("1.0", parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("blocked", parsed.RootElement.GetProperty("status").GetString());
        Assert.Equal("ci_failed", parsed.RootElement.GetProperty("blockers")[0].GetProperty("type").GetString());
        Assert.DoesNotContain("ReadinessStatus", json, StringComparison.Ordinal);
    }

    private static PullRequestSnapshot CreateSnapshot() => new()
    {
        Repository = new RepositorySlug("acme", "payments"),
        Number = 142,
        Title = "Add pagination",
        Author = "octo-dev",
        State = PullRequestState.Open,
        IsDraft = false,
        Mergeability = Mergeability.Clean,
        ReviewDecision = ReviewDecision.Approved,
        ApprovalCount = 1,
        RequestedReviewerCount = 0,
        UnresolvedReviewThreadCount = 0,
        BranchFreshness = BranchFreshness.Current,
        Checks = [new CheckSnapshot("build", CheckState.Failure, true, "https://example.test/checks/build")],
        IssueLinks = [],
        UpdatedAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
        FetchedAt = new DateTimeOffset(2026, 8, 24, 12, 5, 0, TimeSpan.Zero),
        Url = "https://example.test/pulls/142",
        BaseBranch = "main",
        HeadBranch = "feature/pagination",
        BaseSha = "base",
        HeadSha = "head",
        ChangedFiles = 2,
        Additions = 20,
        Deletions = 3,
        Files = [],
    };
}
