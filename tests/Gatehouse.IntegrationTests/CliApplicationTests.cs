using System.Text.Json;
using Gatehouse.Application;
using Gatehouse.Cli;
using Gatehouse.Domain;

namespace Gatehouse.IntegrationTests;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task Demo_status_writes_only_versioned_filtered_json()
    {
        var result = await RunAsync(
            ["status", "--demo", "--json", "--status", "blocked"]);

        Assert.Equal(CliExitCodes.PolicyBlocked, result.ExitCode);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("acme/payments", root.GetProperty("repository").GetString());
        Assert.Equal(2, root.GetProperty("pullRequestCount").GetInt32());
        Assert.All(root.GetProperty("pullRequests").EnumerateArray(), pullRequest =>
        {
            Assert.Equal("blocked", pullRequest.GetProperty("status").GetString());
            Assert.True(pullRequest.TryGetProperty("blockers", out _));
            Assert.True(pullRequest.TryGetProperty("evidence", out _));
            Assert.False(string.IsNullOrWhiteSpace(
                pullRequest.GetProperty("nextAction").GetString()));
        });
    }

    [Fact]
    public async Task Ready_command_has_stable_concise_human_output()
    {
        var result = await RunAsync(["ready", "--demo"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Equal(
            """
            Ready pull requests for acme/payments: 1
            GO      #142 Add pagination to audit endpoint
                    Next: Ready for maintainer review or merge.
            """.ReplaceLineEndings() + Environment.NewLine,
            result.Output);
    }

    [Fact]
    public async Task Pull_request_json_reports_blockers_and_policy_exit_code()
    {
        var result = await RunAsync(["pr", "144", "--demo", "--json"]);

        Assert.Equal(CliExitCodes.PolicyBlocked, result.ExitCode);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("1.0", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("blocked", json.RootElement.GetProperty("status").GetString());
        Assert.Contains(
            json.RootElement.GetProperty("blockers").EnumerateArray(),
            blocker => blocker.GetProperty("type").GetString() == "ci_failed");
        Assert.NotEmpty(json.RootElement.GetProperty("evidence").EnumerateArray());
    }

    [Fact]
    public async Task Report_uses_the_shared_deterministic_report_generator()
    {
        var result = await RunAsync(["report", "142", "--demo"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.StartsWith("GO for review.", result.Output, StringComparison.Ordinal);
        Assert.Contains("PR #142", result.Output, StringComparison.Ordinal);
        Assert.Contains(
            "Recommendation: ready for maintainer review or merge.",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filters_compose_without_duplicating_readiness_logic()
    {
        var result = await RunAsync(
            ["status", "--demo", "--ci", "blocked", "--label", "frontend", "--json"]);

        Assert.Equal(CliExitCodes.PolicyBlocked, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var pullRequest = Assert.Single(
            json.RootElement.GetProperty("pullRequests").EnumerateArray());
        Assert.Equal(144, pullRequest.GetProperty("pullRequest").GetInt32());
    }

    [Fact]
    public async Task Invalid_input_and_provider_failure_use_distinct_exit_codes()
    {
        var invalid = await RunAsync(["status", "bad/repo/name"]);
        var detail = DemoReadinessCatalog.Create();
        var store = new FakeStore(detail)
        {
            RefreshStatus = PullRequestFetchStatus.AccessDenied,
        };
        var provider = await RunAsync(["status", "acme/payments"], store);

        Assert.Equal(CliExitCodes.InvalidInput, invalid.ExitCode);
        Assert.Contains("OWNER/REPOSITORY", invalid.Error, StringComparison.Ordinal);
        Assert.Equal(CliExitCodes.ProviderFailure, provider.ExitCode);
        Assert.Empty(provider.Output);
        Assert.Contains("denied access", provider.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repo_add_finds_parent_policy_and_never_prints_sensitive_failures()
    {
        var root = Path.Combine(Path.GetTempPath(), $"gatehouse-cli-{Guid.NewGuid():N}");
        var child = Path.Combine(root, "one", "two");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(
            Path.Combine(root, ".gatehouse.yml"),
            "readiness:\n  require_linked_issue: true\n");
        try
        {
            var store = new FakeStore(DemoReadinessCatalog.Create());
            var result = await RunAsync(
                ["repo", "add", "octocat/hello-world", "--json"],
                store,
                () => child);

            Assert.Equal(CliExitCodes.Success, result.ExitCode);
            Assert.Empty(result.Error);
            Assert.NotNull(store.AddedRegistration);
            Assert.True(store.AddedRegistration.Policy.RequireLinkedIssue);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Equal(
                "octocat/hello-world",
                json.RootElement.GetProperty("repository").GetString());
            var policySource = json.RootElement.GetProperty("policySource").GetString();
            Assert.NotNull(policySource);
            Assert.EndsWith(
                ".gatehouse.yml",
                policySource,
                StringComparison.Ordinal);

            var throwingStore = new ThrowingStore("secret-token-value");
            var failure = await RunAsync(
                ["status", "octocat/hello-world"],
                throwingStore);
            Assert.Equal(CliExitCodes.InternalFailure, failure.ExitCode);
            Assert.DoesNotContain("secret-token-value", failure.Error, StringComparison.Ordinal);
            Assert.Empty(failure.Output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Serve_validates_port_and_calls_the_host_boundary()
    {
        var calledPort = 0;
        var output = new StringWriter();
        var error = new StringWriter();
        var application = new CliApplication(
            store: null,
            output,
            error,
            (port, _) =>
            {
                calledPort = port;
                return Task.FromResult(CliExitCodes.Success);
            });

        var exitCode = await application.RunAsync(["serve", "--port", "6543"]);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(6543, calledPort);
        Assert.Contains("http://localhost:6543/", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());

        var invalid = await application.RunAsync(["serve", "--port", "80"]);
        Assert.Equal(CliExitCodes.InvalidInput, invalid);
    }

    private static async Task<CliResult> RunAsync(
        string[] args,
        ILocalReadinessStore? store = null,
        Func<string>? currentDirectory = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = new CliApplication(
            store ?? new FakeStore(DemoReadinessCatalog.Create()),
            output,
            error,
            currentDirectory: currentDirectory);
        var exitCode = await application.RunAsync(args);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);

    private sealed class FakeStore(LocalRepositoryDetail detail) : ILocalReadinessStore
    {
        public PullRequestFetchStatus RefreshStatus { get; init; } = PullRequestFetchStatus.Success;

        public RepositoryRegistration? AddedRegistration { get; private set; }

        public Task<LocalRepositorySummary> AddRepositoryAsync(
            RepositoryRegistration registration,
            CancellationToken cancellationToken = default)
        {
            AddedRegistration = registration;
            return Task.FromResult(detail.Repository with
            {
                Owner = registration.Owner,
                Name = registration.Name,
            });
        }

        public Task<IReadOnlyList<LocalRepositorySummary>> ListRepositoriesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalRepositorySummary>>([detail.Repository]);

        public Task<LocalRepositoryDetail?> GetRepositoryAsync(
            Guid repositoryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalRepositoryDetail?>(detail);

        public Task<bool> SelectRepositoryAsync(
            Guid repositoryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> UpdatePolicyAsync(
            Guid repositoryId,
            RepositoryPolicy policy,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<RepositoryRefreshResult?> RefreshRepositoryAsync(
            Guid repositoryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RepositoryRefreshResult?>(new RepositoryRefreshResult(
                RefreshStatus,
                detail.PullRequests.Count,
                detail,
                new ProviderRateLimit(5000, 4999, null),
                []));

        public Task<bool> RemoveRepositoryAsync(
            Guid repositoryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task ClearAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingStore(string message) : ILocalReadinessStore
    {
        public Task<IReadOnlyList<LocalRepositorySummary>> ListRepositoriesAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(message);

        public Task<LocalRepositorySummary> AddRepositoryAsync(
            RepositoryRegistration registration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LocalRepositoryDetail?> GetRepositoryAsync(
            Guid repositoryId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> SelectRepositoryAsync(
            Guid repositoryId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> UpdatePolicyAsync(
            Guid repositoryId,
            RepositoryPolicy policy,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryRefreshResult?> RefreshRepositoryAsync(
            Guid repositoryId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> RemoveRepositoryAsync(
            Guid repositoryId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ClearAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
