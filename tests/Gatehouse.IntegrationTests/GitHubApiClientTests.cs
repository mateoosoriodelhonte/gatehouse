using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Gatehouse.Application;
using Gatehouse.Domain;
using Gatehouse.Infrastructure.GitHub;

namespace Gatehouse.IntegrationTests;

public sealed class GitHubApiClientTests
{
    private static readonly DateTimeOffset FetchedAt =
        new(2026, 8, 24, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Conditional_public_refresh_returns_not_modified_without_sending_a_token()
    {
        var handler = new RecordingHandler((_, _) => Response(
            HttpStatusCode.NotModified,
            headers: new Dictionary<string, string>
            {
                ["ETag"] = "\"etag-2\"",
                ["X-RateLimit-Limit"] = "60",
                ["X-RateLimit-Remaining"] = "59",
                ["X-RateLimit-Reset"] = "1787583600",
            }));
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            "\"etag-1\"",
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.NotModified, result.Status);
        Assert.Empty(result.PullRequests);
        Assert.False(result.IsAuthenticated);
        Assert.Equal("\"etag-2\"", result.ETag);
        Assert.Equal(59, result.RateLimit.Remaining);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "/repos/acme/payments/pulls?state=open&per_page=100&page=1",
            request.PathAndQuery);
        Assert.Equal("\"etag-1\"", request.IfNoneMatch);
        Assert.Null(request.Authorization);
        Assert.Equal(GitHubClientOptions.CurrentRestApiVersion, request.ApiVersion);
        Assert.Equal("application/vnd.github+json", request.Accept);
        Assert.StartsWith("Gatehouse/", request.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Primary_rate_limit_is_reported_without_retrying()
    {
        var handler = new RecordingHandler((_, _) => Response(
            HttpStatusCode.Forbidden,
            "{\"message\":\"API rate limit exceeded\"}",
            new Dictionary<string, string>
            {
                ["X-RateLimit-Limit"] = "60",
                ["X-RateLimit-Remaining"] = "0",
                ["X-RateLimit-Reset"] = "1787583600",
            }));
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.RateLimited, result.Status);
        Assert.Empty(result.PullRequests);
        Assert.Equal(0, result.RateLimit.Remaining);
        Assert.NotNull(result.RateLimit.ResetsAt);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(
            result.Warnings,
            warning => warning.Contains("API rate limit exceeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Authenticated_refresh_uses_graphql_for_complete_review_evidence()
    {
        var restPull = ReadFixture("rest-pull.json").Replace(
            "\"number\": 144",
            "\"number\": 145",
            StringComparison.Ordinal);
        var graphQlPull = ReadFixture("graphql-pull.json");
        var handler = new RecordingHandler((request, number) => number switch
        {
            1 => Response(
                HttpStatusCode.OK,
                $"[{restPull}]",
                new Dictionary<string, string>
                {
                    ["ETag"] = "\"etag-auth\"",
                    ["X-RateLimit-Remaining"] = "4999",
                }),
            2 => Response(
                HttpStatusCode.OK,
                "{\"strict\":true,\"contexts\":[\"build\"],\"checks\":[{\"context\":\"build\"}]}"),
            3 => Response(HttpStatusCode.OK, graphQlPull),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });
        var client = CreateClient(handler, token: "test-token");

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.Success, result.Status);
        Assert.True(result.IsAuthenticated);
        Assert.Equal("\"etag-auth\"", result.ETag);
        var snapshot = Assert.Single(result.PullRequests);
        Assert.Equal(145, snapshot.Number);
        Assert.Equal(1, snapshot.UnresolvedReviewThreadCount);
        Assert.Contains(snapshot.Checks, check => check.Name == "build" && check.IsRequired);
        Assert.All(handler.Requests, request =>
            Assert.Equal("Bearer test-token", request.Authorization));
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal("/graphql", handler.Requests[2].PathAndQuery);
        Assert.Contains("reviewThreads", handler.Requests[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_refresh_uses_rest_and_keeps_review_threads_unknown()
    {
        var pull = ReadFixture("rest-pull.json");
        var checks = ReadFixture("rest-checks.json");
        var reviews = ReadFixture("rest-reviews.json");
        var compare = ReadFixture("rest-compare.json");
        var files = ReadFixture("rest-files.json");
        var handler = new RecordingHandler((request, number) => number switch
        {
            1 => Response(HttpStatusCode.OK, $"[{pull}]"),
            2 => Response(HttpStatusCode.NotFound),
            3 => Response(HttpStatusCode.OK, pull),
            4 => Response(HttpStatusCode.OK, checks),
            5 => Response(HttpStatusCode.OK, reviews),
            6 => Response(HttpStatusCode.OK, compare),
            7 => Response(HttpStatusCode.OK, files),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.Success, result.Status);
        var snapshot = Assert.Single(result.PullRequests);
        Assert.Equal(144, snapshot.Number);
        Assert.Null(snapshot.UnresolvedReviewThreadCount);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("required checks", StringComparison.OrdinalIgnoreCase));
        Assert.All(handler.Requests, request => Assert.Null(request.Authorization));
    }

    [Fact]
    public async Task Mergeability_calculation_is_retried_with_a_fixed_bound()
    {
        var pendingPull = ReadFixture("rest-pull.json").Replace(
            "\"mergeable\": true",
            "\"mergeable\": null",
            StringComparison.Ordinal);
        var completePull = ReadFixture("rest-pull.json");
        var checks = ReadFixture("rest-checks.json");
        var reviews = ReadFixture("rest-reviews.json");
        var compare = ReadFixture("rest-compare.json");
        var files = ReadFixture("rest-files.json");
        var handler = new RecordingHandler((request, number) => number switch
        {
            1 => Response(HttpStatusCode.OK, $"[{pendingPull}]"),
            2 => Response(HttpStatusCode.NotFound),
            3 => Response(HttpStatusCode.OK, pendingPull),
            4 => Response(HttpStatusCode.OK, completePull),
            5 => Response(HttpStatusCode.OK, checks),
            6 => Response(HttpStatusCode.OK, reviews),
            7 => Response(HttpStatusCode.OK, compare),
            8 => Response(HttpStatusCode.OK, files),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.Success, result.Status);
        Assert.Equal(Mergeability.Clean, Assert.Single(result.PullRequests).Mergeability);
        Assert.Equal(2, handler.Requests.Count(request =>
            request.PathAndQuery == "/repos/acme/payments/pulls/144"));
    }

    [Fact]
    public async Task Pagination_never_follows_a_provider_supplied_url()
    {
        var handler = new RecordingHandler((_, number) => number switch
        {
            1 => Response(
                HttpStatusCode.OK,
                "[]",
                new Dictionary<string, string>
                {
                    ["Link"] = "<https://evil.example/collect>; rel=\"next\"",
                }),
            2 => Response(HttpStatusCode.OK, "[]"),
            _ => throw new InvalidOperationException("Unexpected request."),
        });
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.Success, result.Status);
        Assert.Collection(
            handler.Requests,
            first => Assert.Equal(
                "/repos/acme/payments/pulls?state=open&per_page=100&page=1",
                first.PathAndQuery),
            second => Assert.Equal(
                "/repos/acme/payments/pulls?state=open&per_page=100&page=2",
                second.PathAndQuery));
    }

    [Fact]
    public async Task Revoked_access_is_distinct_and_does_not_copy_the_response_body()
    {
        var handler = new RecordingHandler((_, _) => Response(
            HttpStatusCode.NotFound,
            "{\"message\":\"private detail test-secret\"}"));
        var client = CreateClient(handler, token: "test-token");

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.AccessDenied, result.Status);
        Assert.Empty(result.PullRequests);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("access", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            result.Warnings,
            warning => warning.Contains("test-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Provider_outage_has_bounded_retries_and_keeps_no_partial_snapshot()
    {
        var handler = new RecordingHandler((_, _) => Response(
            HttpStatusCode.ServiceUnavailable,
            "{\"message\":\"temporary failure\"}"));
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.Unavailable, result.Status);
        Assert.Empty(result.PullRequests);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Rate_limit_during_policy_fetch_discards_partial_evidence()
    {
        var pull = ReadFixture("rest-pull.json");
        var handler = new RecordingHandler((_, number) => number switch
        {
            1 => Response(HttpStatusCode.OK, $"[{pull}]"),
            2 => Response(
                HttpStatusCode.Forbidden,
                headers: new Dictionary<string, string>
                {
                    ["X-RateLimit-Remaining"] = "0",
                    ["X-RateLimit-Reset"] = "1787583600",
                }),
            _ => throw new InvalidOperationException("Unexpected request."),
        });
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.RateLimited, result.Status);
        Assert.Empty(result.PullRequests);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Incomplete_graphql_page_fails_closed()
    {
        var restPull = ReadFixture("rest-pull.json").Replace(
            "\"number\": 144",
            "\"number\": 145",
            StringComparison.Ordinal);
        var incompleteGraphQl = ReadFixture("graphql-pull.json").Replace(
            "\"reviewThreads\": {\n          \"pageInfo\": { \"hasNextPage\": false },",
            "\"reviewThreads\": {\n          \"pageInfo\": { \"hasNextPage\": true },",
            StringComparison.Ordinal);
        var handler = new RecordingHandler((_, number) => number switch
        {
            1 => Response(HttpStatusCode.OK, $"[{restPull}]"),
            2 => Response(HttpStatusCode.NotFound),
            3 => Response(HttpStatusCode.OK, incompleteGraphQl),
            _ => throw new InvalidOperationException("Unexpected request."),
        });
        var client = CreateClient(handler, token: "test-token");

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.Unavailable, result.Status);
        Assert.Empty(result.PullRequests);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public void Client_rejects_a_non_github_base_address()
    {
        var handler = new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "[]"));
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test/"),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new GitHubApiClient(httpClient, new GitHubClientOptions()));

        Assert.Contains("api.github.com", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Secondary_rate_limit_is_retried_then_reported_as_rate_limited()
    {
        var handler = new RecordingHandler((_, _) => Response(
            HttpStatusCode.Forbidden,
            headers: new Dictionary<string, string>
            {
                ["Retry-After"] = "0",
                ["X-RateLimit-Remaining"] = "42",
            }));
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.RateLimited, result.Status);
        Assert.Empty(result.PullRequests);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Missing_graphql_page_info_fails_closed()
    {
        var graphQl = ReadFixture("graphql-pull.json").Replace(
            "          \"pageInfo\": { \"hasNextPage\": false },\n          \"nodes\": [\n            {\n              \"isResolved\": false",
            "          \"nodes\": [\n            {\n              \"isResolved\": false",
            StringComparison.Ordinal);

        var result = await RunAuthenticatedGraphQlAsync(graphQl);

        Assert.Equal(PullRequestFetchStatus.Unavailable, result.Status);
        Assert.Empty(result.PullRequests);
    }

    [Fact]
    public async Task Missing_graphql_nodes_fails_closed()
    {
        var graphQl = ReadFixture("graphql-pull.json").Replace(
            "          \"nodes\": [\n            {\n              \"isResolved\": false\n            },\n            {\n              \"isResolved\": true\n            }\n          ]",
            "          \"missingNodes\": []",
            StringComparison.Ordinal);

        var result = await RunAuthenticatedGraphQlAsync(graphQl);

        Assert.Equal(PullRequestFetchStatus.Unavailable, result.Status);
        Assert.Empty(result.PullRequests);
    }

    [Fact]
    public async Task Provider_timeout_returns_unavailable_but_caller_cancellation_is_not_hidden()
    {
        var handler = new RecordingHandler((_, _) => throw new TaskCanceledException("timeout"));
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.Unavailable, result.Status);
        Assert.Empty(result.PullRequests);
    }

    [Fact]
    public async Task Invalid_rate_reset_header_is_ignored()
    {
        var handler = new RecordingHandler((_, _) => Response(
            HttpStatusCode.OK,
            "[]",
            new Dictionary<string, string>
            {
                ["X-RateLimit-Reset"] = long.MaxValue.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            }));
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);

        Assert.Equal(PullRequestFetchStatus.Success, result.Status);
        Assert.Null(result.RateLimit.ResetsAt);
    }

    private static async Task<PullRequestFetchResult> RunAuthenticatedGraphQlAsync(
        string graphQl)
    {
        var restPull = ReadFixture("rest-pull.json").Replace(
            "\"number\": 144",
            "\"number\": 145",
            StringComparison.Ordinal);
        var handler = new RecordingHandler((_, number) => number switch
        {
            1 => Response(HttpStatusCode.OK, $"[{restPull}]"),
            2 => Response(HttpStatusCode.NotFound),
            3 => Response(HttpStatusCode.OK, graphQl),
            _ => throw new InvalidOperationException("Unexpected request."),
        });
        var client = CreateClient(handler, token: "test-token");
        return await client.GetOpenPullRequestsAsync(
            new RepositorySlug("acme", "payments"),
            null,
            CancellationToken.None);
    }

    private static GitHubApiClient CreateClient(RecordingHandler handler, string? token = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/"),
        };
        var options = new GitHubClientOptions
        {
            Token = token,
            RetryDelay = TimeSpan.Zero,
        };
        return new GitHubApiClient(httpClient, options, () => FetchedAt);
    }

    private static string ReadFixture(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Gatehouse.IntegrationTests.Fixtures.GitHub.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Fixture not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static HttpResponseMessage Response(
        HttpStatusCode statusCode,
        string content = "",
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private int requestNumber;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Headers.IfNoneMatch.SingleOrDefault()?.ToString(),
                request.Headers.TryGetValues("X-GitHub-Api-Version", out var versions)
                    ? versions.Single()
                    : null,
                request.Headers.Accept.SingleOrDefault()?.MediaType,
                request.Headers.UserAgent.ToString(),
                body));
            return responder(request, Interlocked.Increment(ref requestNumber));
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string PathAndQuery,
        string? Authorization,
        string? IfNoneMatch,
        string? ApiVersion,
        string? Accept,
        string UserAgent,
        string Body);
}
