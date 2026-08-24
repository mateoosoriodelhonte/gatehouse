using System.Reflection;
using System.Text.Json;
using Gatehouse.Domain;
using Gatehouse.Infrastructure.GitHub;

namespace Gatehouse.IntegrationTests;

public sealed class GitHubSnapshotNormalizerTests
{
    private static readonly DateTimeOffset FetchedAt =
        new(2026, 8, 24, 13, 5, 0, TimeSpan.Zero);

    [Fact]
    public void Rest_fixture_preserves_partial_evidence_without_inventing_threads()
    {
        using var pull = ReadFixture("rest-pull.json");
        using var checks = ReadFixture("rest-checks.json");
        using var reviews = ReadFixture("rest-reviews.json");
        using var compare = ReadFixture("rest-compare.json");
        using var files = ReadFixture("rest-files.json");

        var snapshot = GitHubSnapshotNormalizer.FromRest(
            new RepositorySlug("acme", "payments"),
            pull.RootElement,
            checks.RootElement,
            reviews.RootElement,
            compare.RootElement,
            files.RootElement,
            new HashSet<string>(["build", "Path-Aware QA"], StringComparer.OrdinalIgnoreCase),
            FetchedAt);

        Assert.Equal(144, snapshot.Number);
        Assert.Equal(Mergeability.Clean, snapshot.Mergeability);
        Assert.Equal(ReviewDecision.ChangesRequested, snapshot.ReviewDecision);
        Assert.Equal(0, snapshot.ApprovalCount);
        Assert.Equal(["maintainer", "payments-team"], snapshot.RequestedReviewers);
        Assert.Equal(["bug"], snapshot.Labels);
        Assert.Null(snapshot.UnresolvedReviewThreadCount);
        Assert.Equal(BranchFreshness.Behind, snapshot.BranchFreshness);
        Assert.Contains(snapshot.Checks, check =>
            check.Name == "Path-Aware QA" &&
            check.State == CheckState.ActionRequired &&
            check.IsRequired);
        Assert.Contains(snapshot.Checks, check =>
            check.Name == "preview" && check.State == CheckState.Skipped);
        Assert.Contains(snapshot.Checks, check =>
            check.Name == "lint" && check.State == CheckState.Pending);
        Assert.Contains(snapshot.IssueLinks, link =>
            link.Number == 143 && link.Kind == IssueLinkKind.Explicit);
        Assert.Contains(snapshot.IssueLinks, link =>
            link.Number == 141 && link.Kind == IssueLinkKind.PossibleReference);
        Assert.Collection(
            snapshot.Files,
            first => Assert.Equal("src/routes/dashboard.cs", first.Path),
            second => Assert.Equal("tests/routes/dashboard-tests.cs", second.Path));
    }

    [Fact]
    public void Graphql_fixture_preserves_threads_issue_state_and_check_kinds()
    {
        using var fixture = ReadFixture("graphql-pull.json");
        var pullRequest = fixture.RootElement
            .GetProperty("data")
            .GetProperty("repository")
            .GetProperty("pullRequest");

        var snapshot = GitHubSnapshotNormalizer.FromGraphQl(
            new RepositorySlug("acme", "payments"),
            pullRequest,
            new HashSet<string>(["build"], StringComparer.OrdinalIgnoreCase),
            FetchedAt);

        Assert.Equal(145, snapshot.Number);
        Assert.Equal(Mergeability.Conflicting, snapshot.Mergeability);
        Assert.Equal(ReviewDecision.Approved, snapshot.ReviewDecision);
        Assert.Equal(1, snapshot.ApprovalCount);
        Assert.Equal(["release-captain"], snapshot.RequestedReviewers);
        Assert.Equal(["payments"], snapshot.Labels);
        Assert.Equal(1, snapshot.UnresolvedReviewThreadCount);
        Assert.Equal(BranchFreshness.Unknown, snapshot.BranchFreshness);
        Assert.Contains(snapshot.Checks, check =>
            check.Name == "build" &&
            check.State == CheckState.Neutral &&
            check.IsRequired);
        Assert.Contains(snapshot.Checks, check =>
            check.Name == "deploy-preview" && check.State == CheckState.Pending);
        Assert.Contains(snapshot.IssueLinks, link =>
            link.Number == 140 && link.Kind == IssueLinkKind.Explicit && link.IsClosed);
        Assert.Collection(snapshot.Files, file => Assert.Equal("src/retry-policy.cs", file.Path));
    }

    [Fact]
    public void Malformed_provider_payload_fails_with_a_safe_field_error()
    {
        using var pull = JsonDocument.Parse("{}");
        using var checks = ReadFixture("rest-checks.json");
        using var reviews = ReadFixture("rest-reviews.json");
        using var compare = ReadFixture("rest-compare.json");
        using var files = ReadFixture("rest-files.json");

        var exception = Assert.Throws<InvalidDataException>(() =>
            GitHubSnapshotNormalizer.FromRest(
                new RepositorySlug("acme", "payments"),
                pull.RootElement,
                checks.RootElement,
                reviews.RootElement,
                compare.RootElement,
                files.RootElement,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                FetchedAt));

        Assert.Equal(
            "GitHub returned an invalid or missing requested_reviewers field.",
            exception.Message);
        Assert.DoesNotContain("{}", exception.Message, StringComparison.Ordinal);
    }

    private static JsonDocument ReadFixture(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Gatehouse.IntegrationTests.Fixtures.GitHub.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Fixture not found: {resourceName}");
        return JsonDocument.Parse(stream);
    }
}
