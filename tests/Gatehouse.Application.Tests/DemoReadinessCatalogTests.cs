using Gatehouse.Application;
using Gatehouse.Domain;

namespace Gatehouse.Application.Tests;

public sealed class DemoReadinessCatalogTests
{
    [Fact]
    public void Demo_catalog_covers_the_dashboard_readiness_workflow()
    {
        var repository = DemoReadinessCatalog.Create();

        Assert.Equal("acme", repository.Repository.Owner);
        Assert.Equal("payments", repository.Repository.Name);
        Assert.Equal(5, repository.PullRequests.Count);
        Assert.Contains(repository.PullRequests, item => item.Evaluation.Status == ReadinessStatus.Go);
        Assert.Contains(repository.PullRequests, item => item.Evaluation.Status == ReadinessStatus.Review);
        Assert.Contains(repository.PullRequests, item => item.Evaluation.Status == ReadinessStatus.Draft);
        Assert.Equal(
            2,
            repository.PullRequests.Count(item =>
                item.Evaluation.Status == ReadinessStatus.Blocked));
        Assert.All(repository.PullRequests, item => Assert.StartsWith(
            "https://example.com/gatehouse-demo/",
            item.Snapshot.Url,
            StringComparison.Ordinal));
    }

    [Fact]
    public void Demo_catalog_contains_filter_and_review_packet_evidence()
    {
        var repository = DemoReadinessCatalog.Create();
        var failingCi = repository.PullRequests.Single(item => item.Snapshot.Number == 144);

        Assert.Contains("bug", failingCi.Snapshot.Labels);
        Assert.Contains("maya-dev", failingCi.Snapshot.RequestedReviewers);
        Assert.Contains(failingCi.Evaluation.Blockers, blocker => blocker.Type == "ci_failed");
        Assert.NotEmpty(failingCi.Snapshot.Files);
        Assert.Contains("NO-GO", failingCi.ReportMarkdown, StringComparison.Ordinal);
    }
}
