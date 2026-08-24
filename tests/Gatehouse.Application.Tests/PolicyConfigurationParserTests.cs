using Gatehouse.Application;

namespace Gatehouse.Application.Tests;

public sealed class PolicyConfigurationParserTests
{
    [Fact]
    public void Parses_supported_readiness_keys()
    {
        const string yaml = """
            readiness:
              require_linked_issue: true
              require_all_checks: false
              require_approval: true
              require_no_unresolved_threads: false
              require_mergeable: true
              require_current_branch: true
              block_on_changes_requested: true
            """;

        var result = PolicyConfigurationParser.Parse(yaml);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Policy);
        Assert.True(result.Policy.RequireLinkedIssue);
        Assert.False(result.Policy.RequireAllChecks);
        Assert.True(result.Policy.RequireApproval);
        Assert.False(result.Policy.RequireNoUnresolvedThreads);
        Assert.True(result.Policy.RequireMergeable);
        Assert.True(result.Policy.RequireCurrentBranch);
        Assert.True(result.Policy.BlockOnChangesRequested);
    }

    [Fact]
    public void Empty_configuration_uses_safe_defaults()
    {
        var result = PolicyConfigurationParser.Parse(string.Empty);

        Assert.True(result.IsValid);
        Assert.Equal(Gatehouse.Domain.RepositoryPolicy.SafeDefaults, result.Policy);
    }

    [Theory]
    [InlineData("readiness:\n  invented_rule: true", "Unknown readiness key: invented_rule.")]
    [InlineData("readiness:\n  require_approval: sometimes", "require_approval must be true or false.")]
    [InlineData("wrong_root:\n  require_approval: true", "Only the readiness root is supported.")]
    public void Invalid_configuration_fails_closed(string yaml, string expectedError)
    {
        var result = PolicyConfigurationParser.Parse(yaml);

        Assert.False(result.IsValid);
        Assert.Null(result.Policy);
        Assert.Contains(expectedError, result.Errors);
    }
}
