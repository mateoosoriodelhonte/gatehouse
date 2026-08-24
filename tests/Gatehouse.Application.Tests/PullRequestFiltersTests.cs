using Gatehouse.Application;
using Gatehouse.Domain;

namespace Gatehouse.Application.Tests;

public sealed class PullRequestFiltersTests
{
    public static TheoryData<PullRequestFilter, int> FilterCases => new()
    {
        { new PullRequestFilter(Status: ReadinessStatus.Go), 142 },
        { new PullRequestFilter(Author: "noah"), 143 },
        { new PullRequestFilter(Label: "bug"), 144 },
        { new PullRequestFilter(Branch: "retry"), 145 },
        { new PullRequestFilter(Reviewer: "release"), 142 },
        { new PullRequestFilter(Ci: PullRequestCiFilter.Blocked), 144 },
        { new PullRequestFilter(Draft: PullRequestDraftFilter.Draft), 146 },
    };

    [Theory]
    [MemberData(nameof(FilterCases))]
    public void Filters_return_the_expected_demo_change(
        PullRequestFilter filter,
        int expectedNumber)
    {
        var repository = DemoReadinessCatalog.Create();

        var result = Assert.Single(PullRequestFilters.Apply(repository.PullRequests, filter));

        Assert.Equal(expectedNumber, result.Snapshot.Number);
    }

    [Fact]
    public void Blocked_by_ci_does_not_include_a_conflict_only_change()
    {
        var repository = DemoReadinessCatalog.Create();

        var result = PullRequestFilters.Apply(
            repository.PullRequests,
            new PullRequestFilter(Ci: PullRequestCiFilter.Blocked));

        Assert.Collection(result, item => Assert.Equal(144, item.Snapshot.Number));
        Assert.DoesNotContain(result, item => item.Snapshot.Number == 145);
    }
}
