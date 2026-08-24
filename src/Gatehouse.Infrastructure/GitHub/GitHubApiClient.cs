using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Gatehouse.Application;
using Gatehouse.Domain;

namespace Gatehouse.Infrastructure.GitHub;

public sealed class GitHubApiClient : IPullRequestSource
{
    private const string UserAgent = "Gatehouse/1.0";

    private const string PullRequestQuery = """
        query PullRequestReadiness($owner: String!, $name: String!, $number: Int!) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              number
              title
              body
              state
              isDraft
              url
              updatedAt
              baseRefName
              baseRefOid
              headRefName
              headRefOid
              additions
              deletions
              changedFiles
              mergeable
              mergeStateStatus
              reviewDecision
              author { login }
              labels(first: 100) {
                nodes { name }
                pageInfo { hasNextPage }
              }
              reviewRequests(first: 100) {
                nodes {
                  requestedReviewer {
                    __typename
                    ... on User { login }
                    ... on Team { slug }
                  }
                }
                pageInfo { hasNextPage }
              }
              reviews(first: 100, states: [APPROVED, CHANGES_REQUESTED]) {
                nodes {
                  author { login }
                  state
                  submittedAt
                  url
                }
                pageInfo { hasNextPage }
              }
              reviewThreads(first: 100) {
                nodes { isResolved }
                pageInfo { hasNextPage }
              }
              closingIssuesReferences(first: 100) {
                nodes { number state url }
                pageInfo { hasNextPage }
              }
              statusCheckRollup {
                contexts(first: 100) {
                  nodes {
                    __typename
                    ... on CheckRun { name status conclusion detailsUrl }
                    ... on StatusContext { context state targetUrl }
                  }
                  pageInfo { hasNextPage }
                }
              }
              files(first: 100) {
                nodes { path changeType additions deletions }
                pageInfo { hasNextPage }
              }
            }
          }
        }
        """;

    private readonly HttpClient httpClient;
    private readonly GitHubClientOptions options;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly AuthenticationHeaderValue? authorization;

    public GitHubApiClient(
        HttpClient httpClient,
        GitHubClientOptions options,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ValidateBaseAddress(httpClient.BaseAddress);
        ValidateOptions(options);

        this.httpClient = httpClient;
        this.options = options;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        authorization = string.IsNullOrEmpty(options.Token)
            ? null
            : new AuthenticationHeaderValue("Bearer", options.Token);
    }

    public async Task<PullRequestFetchResult> GetOpenPullRequestsAsync(
        RepositorySlug repository,
        string? etag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ValidateRepository(repository);
        ValidateEtag(etag);

        var fetchedAt = utcNow();
        var context = new RequestContext(options.MaxRequestsPerRefresh);
        var warnings = new List<string>();
        var responseEtag = etag;

        try
        {
            var pulls = await GetPullRequestListAsync(
                repository,
                etag,
                context,
                cancellationToken);
            responseEtag = pulls.ETag ?? responseEtag;

            if (pulls.Status != PullRequestFetchStatus.Success)
            {
                AddStatusWarning(pulls.Status, warnings);
                return Result(
                    pulls.Status,
                    [],
                    responseEtag,
                    context,
                    fetchedAt,
                    warnings);
            }

            if (pulls.PullRequests.Count > options.MaxPullRequests)
            {
                throw new GitHubDataException(
                    "GitHub returned more open pull requests than one safe refresh can process.");
            }

            var requiredChecksByBranch = new Dictionary<string, IReadOnlySet<string>>(
                StringComparer.Ordinal);
            var snapshots = new List<PullRequestSnapshot>(pulls.PullRequests.Count);

            foreach (var pullRequest in pulls.PullRequests)
            {
                var number = RequiredInt32(pullRequest, "number");
                var baseBranch = RequiredString(RequiredObject(pullRequest, "base"), "ref");
                if (!requiredChecksByBranch.TryGetValue(baseBranch, out var requiredChecks))
                {
                    requiredChecks = await GetRequiredCheckNamesAsync(
                        repository,
                        baseBranch,
                        context,
                        warnings,
                        cancellationToken);
                    requiredChecksByBranch.Add(baseBranch, requiredChecks);
                }

                PullRequestSnapshot? snapshot = null;
                if (authorization is not null)
                {
                    snapshot = await TryGetGraphQlSnapshotAsync(
                        repository,
                        number,
                        requiredChecks,
                        fetchedAt,
                        context,
                        warnings,
                        cancellationToken);
                }

                snapshot ??= await GetRestSnapshotAsync(
                    repository,
                    number,
                    requiredChecks,
                    fetchedAt,
                    context,
                    cancellationToken);
                snapshots.Add(snapshot);
            }

            return Result(
                PullRequestFetchStatus.Success,
                snapshots.OrderBy(snapshot => snapshot.Number).ToArray(),
                responseEtag,
                context,
                fetchedAt,
                warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            warnings.Add(
                "GitHub timed out before it provided a complete readiness snapshot. Keep the last saved data and try again.");
            return Result(
                PullRequestFetchStatus.Unavailable,
                [],
                responseEtag,
                context,
                fetchedAt,
                warnings);
        }
        catch (GitHubRateLimitException)
        {
            warnings.Add("GitHub rate-limited this refresh. Try again after the reset time.");
            return Result(
                PullRequestFetchStatus.RateLimited,
                [],
                responseEtag,
                context,
                fetchedAt,
                warnings);
        }
        catch (Exception exception) when (exception is
            GitHubDataException or
            HttpRequestException or
            JsonException or
            InvalidDataException)
        {
            warnings.Add(
                "GitHub could not provide a complete readiness snapshot. Keep the last saved data and try again.");
            return Result(
                PullRequestFetchStatus.Unavailable,
                [],
                responseEtag,
                context,
                fetchedAt,
                warnings);
        }
    }

    private async Task<PullRequestListResult> GetPullRequestListAsync(
        RepositorySlug repository,
        string? etag,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? responseEtag = null;
        var hasNextPage = true;

        for (var page = 1; hasNextPage; page++)
        {
            if (page > options.MaxPagesPerEndpoint)
            {
                throw new GitHubDataException("GitHub pull request pagination exceeded the safe limit.");
            }

            var path = $"{RepositoryPath(repository)}/pulls?state=open&per_page=100&page={page}";
            using var response = await SendAsync(
                context,
                () => CreateRequest(HttpMethod.Get, path, page == 1 ? etag : null),
                cancellationToken);

            if (page == 1)
            {
                responseEtag = response.Headers.ETag?.ToString();
                var status = MapListStatus(response, context);
                if (status != PullRequestFetchStatus.Success)
                {
                    return new PullRequestListResult(status, [], responseEtag);
                }
            }
            else
            {
                EnsureSuccessful(response, context);
            }

            using var document = await ReadJsonAsync(response, cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubDataException("GitHub returned an invalid pull request list.");
            }

            items.AddRange(document.RootElement.EnumerateArray().Select(item => item.Clone()));
            hasNextPage = HasNextPage(response);
        }

        return new PullRequestListResult(
            PullRequestFetchStatus.Success,
            items,
            responseEtag);
    }

    private async Task<IReadOnlySet<string>> GetRequiredCheckNamesAsync(
        RepositorySlug repository,
        string branch,
        RequestContext context,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var path = $"{RepositoryPath(repository)}/branches/{Segment(branch)}/protection/required_status_checks";
        using var response = await SendAsync(
            context,
            () => CreateRequest(HttpMethod.Get, path),
            cancellationToken);

        if (IsRateLimitResponse(response, context))
        {
            throw new GitHubRateLimitException();
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            warnings.Add(
                $"GitHub did not expose required checks for the {branch} branch. All reported checks remain visible.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        EnsureSuccessful(response, context);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddStringArray(document.RootElement, "contexts", names);
        if (document.RootElement.TryGetProperty("checks", out var checks) &&
            checks.ValueKind == JsonValueKind.Array)
        {
            foreach (var check in checks.EnumerateArray())
            {
                if (check.TryGetProperty("context", out var name) &&
                    name.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(name.GetString()))
                {
                    names.Add(name.GetString()!);
                }
            }
        }

        return names;
    }

    private async Task<PullRequestSnapshot?> TryGetGraphQlSnapshotAsync(
        RepositorySlug repository,
        int number,
        IReadOnlySet<string> requiredCheckNames,
        DateTimeOffset fetchedAt,
        RequestContext context,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            query = PullRequestQuery,
            variables = new
            {
                owner = repository.Owner,
                name = repository.Name,
                number,
            },
        });
        using var response = await SendAsync(
            context,
            () => CreateRequest(HttpMethod.Post, "/graphql", content: body),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if (IsRateLimitResponse(response, context))
            {
                throw new GitHubRateLimitException();
            }

            warnings.Add(
                $"GitHub GraphQL evidence was not available for pull request #{number}. Gatehouse used REST evidence instead.");
            return null;
        }

        using var document = await ReadJsonAsync(response, cancellationToken);
        if (document.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            warnings.Add(
                $"GitHub GraphQL evidence was incomplete for pull request #{number}. Gatehouse used REST evidence instead.");
            return null;
        }

        if (!TryGetGraphQlPullRequest(document.RootElement, out var pullRequest))
        {
            warnings.Add(
                $"GitHub GraphQL did not return pull request #{number}. Gatehouse used REST evidence instead.");
            return null;
        }

        EnsureGraphQlConnectionsAreComplete(pullRequest);
        PullRequestSnapshot snapshot;
        try
        {
            snapshot = GitHubSnapshotNormalizer.FromGraphQl(
                repository,
                pullRequest,
                requiredCheckNames,
                fetchedAt);
        }
        catch (Exception exception) when (exception is
            KeyNotFoundException or
            InvalidOperationException)
        {
            throw new GitHubDataException(
                "GitHub returned malformed GraphQL evidence.",
                exception);
        }
        if (snapshot.Number != number)
        {
            throw new GitHubDataException(
                "GitHub returned pull request evidence for the wrong pull request.");
        }

        return snapshot;
    }

    private async Task<PullRequestSnapshot> GetRestSnapshotAsync(
        RepositorySlug repository,
        int number,
        IReadOnlySet<string> requiredCheckNames,
        DateTimeOffset fetchedAt,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        using var pullRequest = await GetRestPullRequestAsync(
            repository,
            number,
            context,
            cancellationToken);
        var baseObject = RequiredObject(pullRequest.RootElement, "base");
        var headObject = RequiredObject(pullRequest.RootElement, "head");
        var baseSha = RequiredString(baseObject, "sha");
        var headSha = RequiredString(headObject, "sha");

        using var checkRuns = await GetCheckRunsAsync(
            repository,
            headSha,
            context,
            cancellationToken);
        using var reviews = await GetArrayDocumentAsync(
            context,
            page => $"{RepositoryPath(repository)}/pulls/{number}/reviews?per_page=100&page={page}",
            cancellationToken);
        using var comparison = await GetDocumentAsync(
            context,
            $"{RepositoryPath(repository)}/compare/{Segment($"{baseSha}...{headSha}")}",
            cancellationToken);
        using var files = await GetArrayDocumentAsync(
            context,
            page => $"{RepositoryPath(repository)}/pulls/{number}/files?per_page=100&page={page}",
            cancellationToken);

        return GitHubSnapshotNormalizer.FromRest(
            repository,
            pullRequest.RootElement,
            checkRuns.RootElement,
            reviews.RootElement,
            comparison.RootElement,
            files.RootElement,
            requiredCheckNames,
            fetchedAt);
    }

    private async Task<JsonDocument> GetRestPullRequestAsync(
        RepositorySlug repository,
        int number,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        JsonDocument? lastDocument = null;
        try
        {
            for (var attempt = 1; attempt <= options.MergeabilityAttempts; attempt++)
            {
                lastDocument?.Dispose();
                lastDocument = await GetDocumentAsync(
                    context,
                    $"{RepositoryPath(repository)}/pulls/{number}",
                    cancellationToken);

                if (lastDocument.RootElement.TryGetProperty("mergeable", out var mergeable) &&
                    mergeable.ValueKind != JsonValueKind.Null)
                {
                    return lastDocument;
                }

                if (attempt < options.MergeabilityAttempts)
                {
                    await DelayAsync(options.RetryDelay, cancellationToken);
                }
            }

            return lastDocument ?? throw new GitHubDataException(
                "GitHub did not return pull request details.");
        }
        catch
        {
            lastDocument?.Dispose();
            throw;
        }
    }

    private async Task<JsonDocument> GetCheckRunsAsync(
        RepositorySlug repository,
        string headSha,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var checkRuns = new List<JsonElement>();
        var hasNextPage = true;

        for (var page = 1; hasNextPage; page++)
        {
            if (page > options.MaxPagesPerEndpoint)
            {
                throw new GitHubDataException("GitHub check pagination exceeded the safe limit.");
            }

            var path = $"{RepositoryPath(repository)}/commits/{Segment(headSha)}/check-runs?per_page=100&page={page}";
            using var response = await SendAsync(
                context,
                () => CreateRequest(HttpMethod.Get, path),
                cancellationToken);
            EnsureSuccessful(response, context);
            using var pageDocument = await ReadJsonAsync(response, cancellationToken);
            if (!pageDocument.RootElement.TryGetProperty("check_runs", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubDataException("GitHub returned invalid check data.");
            }

            checkRuns.AddRange(items.EnumerateArray().Select(item => item.Clone()));
            hasNextPage = HasNextPage(response);
        }

        return JsonSerializer.SerializeToDocument(new
        {
            total_count = checkRuns.Count,
            check_runs = checkRuns,
        });
    }

    private async Task<JsonDocument> GetArrayDocumentAsync(
        RequestContext context,
        Func<int, string> pathForPage,
        CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var hasNextPage = true;

        for (var page = 1; hasNextPage; page++)
        {
            if (page > options.MaxPagesPerEndpoint)
            {
                throw new GitHubDataException("GitHub pagination exceeded the safe limit.");
            }

            using var response = await SendAsync(
                context,
                () => CreateRequest(HttpMethod.Get, pathForPage(page)),
                cancellationToken);
            EnsureSuccessful(response, context);
            using var pageDocument = await ReadJsonAsync(response, cancellationToken);
            if (pageDocument.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubDataException("GitHub returned an invalid list.");
            }

            items.AddRange(pageDocument.RootElement.EnumerateArray().Select(item => item.Clone()));
            hasNextPage = HasNextPage(response);
        }

        return JsonSerializer.SerializeToDocument(items);
    }

    private async Task<JsonDocument> GetDocumentAsync(
        RequestContext context,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            context,
            () => CreateRequest(HttpMethod.Get, path),
            cancellationToken);
        EnsureSuccessful(response, context);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        RequestContext context,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            context.StartRequest();
            using var request = requestFactory();
            var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            context.UpdateRateLimit(response.Headers);

            if (attempt >= options.MaxRetryAttempts || !ShouldRetry(response, context))
            {
                return response;
            }

            var delay = RetryDelay(response, attempt);
            response.Dispose();
            await DelayAsync(delay, cancellationToken);
        }
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string? etag = null,
        string? content = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Add("X-GitHub-Api-Version", GitHubClientOptions.CurrentRestApiVersion);
        request.Headers.Authorization = authorization;
        if (etag is not null)
        {
            request.Headers.IfNoneMatch.ParseAdd(etag);
        }

        if (content is not null)
        {
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > options.MaxResponseBytes)
        {
            throw new GitHubDataException("GitHub returned a response that exceeded the safe size limit.");
        }

        await response.Content.LoadIntoBufferAsync(options.MaxResponseBytes, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { MaxDepth = 64 },
            cancellationToken);
    }

    private static PullRequestFetchStatus MapListStatus(
        HttpResponseMessage response,
        RequestContext context)
    {
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return PullRequestFetchStatus.NotModified;
        }

        if (IsRateLimitResponse(response, context))
        {
            return PullRequestFetchStatus.RateLimited;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or
            HttpStatusCode.NotFound)
        {
            return PullRequestFetchStatus.AccessDenied;
        }

        return response.IsSuccessStatusCode
            ? PullRequestFetchStatus.Success
            : PullRequestFetchStatus.Unavailable;
    }

    private static void EnsureSuccessful(
        HttpResponseMessage response,
        RequestContext context)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (IsRateLimitResponse(response, context))
        {
            throw new GitHubRateLimitException();
        }

        throw new GitHubDataException(
            $"GitHub returned HTTP {(int)response.StatusCode} for required evidence.");
    }

    private static bool IsPrimaryRateLimit(
        HttpResponseMessage response,
        RequestContext context) =>
        (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) &&
        context.RateLimit.Remaining == 0;

    private static bool IsRateLimitResponse(
        HttpResponseMessage response,
        RequestContext context) =>
        IsPrimaryRateLimit(response, context) ||
        response.StatusCode == HttpStatusCode.TooManyRequests ||
        response.StatusCode == HttpStatusCode.Forbidden &&
        response.Headers.RetryAfter is not null;

    private static bool ShouldRetry(
        HttpResponseMessage response,
        RequestContext context)
    {
        if (IsPrimaryRateLimit(response, context))
        {
            return false;
        }

        return response.StatusCode is
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout ||
            response.StatusCode == HttpStatusCode.Forbidden &&
            response.Headers.RetryAfter is not null;
    }

    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? serverDelay = retryAfter?.Delta;
        if (serverDelay is null && retryAfter?.Date is { } retryAt)
        {
            serverDelay = retryAt - utcNow();
        }

        var delay = serverDelay ?? TimeSpan.FromTicks(
            options.RetryDelay.Ticks * (1L << Math.Min(attempt - 1, 8)));
        return delay <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(Math.Min(delay.Ticks, TimeSpan.FromSeconds(30).Ticks));
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, cancellationToken);

    private PullRequestFetchResult Result(
        PullRequestFetchStatus status,
        IReadOnlyList<PullRequestSnapshot> pullRequests,
        string? etag,
        RequestContext context,
        DateTimeOffset fetchedAt,
        IReadOnlyList<string> warnings) =>
        new(
            status,
            pullRequests,
            etag,
            context.RateLimit,
            fetchedAt,
            authorization is not null,
            warnings.ToArray());

    private static void AddStatusWarning(
        PullRequestFetchStatus status,
        List<string> warnings)
    {
        var warning = status switch
        {
            PullRequestFetchStatus.RateLimited =>
                "GitHub rate-limited this refresh. Try again after the reset time.",
            PullRequestFetchStatus.AccessDenied =>
                "GitHub did not grant access to this repository. Check its name and token permissions.",
            PullRequestFetchStatus.Unavailable =>
                "GitHub is not available. Keep the last saved data and try again.",
            _ => null,
        };
        if (warning is not null)
        {
            warnings.Add(warning);
        }
    }

    private static bool HasNextPage(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Link", out var values) &&
        values.Any(value => value.Split(',').Any(segment =>
            segment.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)));

    private static bool TryGetGraphQlPullRequest(
        JsonElement root,
        out JsonElement pullRequest)
    {
        pullRequest = default;
        return root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("repository", out var repository) &&
            repository.ValueKind == JsonValueKind.Object &&
            repository.TryGetProperty("pullRequest", out pullRequest) &&
            pullRequest.ValueKind == JsonValueKind.Object;
    }

    private static void EnsureGraphQlConnectionsAreComplete(JsonElement pullRequest)
    {
        foreach (var connectionName in new[]
        {
            "labels",
            "reviewRequests",
            "reviews",
            "reviewThreads",
            "closingIssuesReferences",
            "files",
        })
        {
            EnsureConnectionIsComplete(RequiredObject(pullRequest, connectionName));
        }

        if (pullRequest.TryGetProperty("statusCheckRollup", out var rollup) &&
            rollup.ValueKind == JsonValueKind.Object)
        {
            EnsureConnectionIsComplete(RequiredObject(rollup, "contexts"));
        }
    }

    private static void EnsureConnectionIsComplete(JsonElement connection)
    {
        if (!connection.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array ||
            !connection.TryGetProperty("pageInfo", out var pageInfo) ||
            pageInfo.ValueKind != JsonValueKind.Object ||
            !pageInfo.TryGetProperty("hasNextPage", out var hasNextPage) ||
            hasNextPage.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new GitHubDataException(
                "GitHub returned incomplete GraphQL paging data.");
        }

        if (hasNextPage.GetBoolean())
        {
            throw new GitHubDataException(
                "GitHub returned more evidence than the safe GraphQL page can contain.");
        }
    }

    private static JsonElement RequiredObject(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : throw new GitHubDataException(
                $"GitHub returned an invalid or missing {propertyName} field.");

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new GitHubDataException(
                $"GitHub returned an invalid or missing {propertyName} field.");

    private static int RequiredInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.TryGetInt32(out var result) && result > 0
            ? result
            : throw new GitHubDataException(
                $"GitHub returned an invalid or missing {propertyName} field.");

    private static void AddStringArray(
        JsonElement element,
        string propertyName,
        HashSet<string> target)
    {
        if (!element.TryGetProperty(propertyName, out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                target.Add(value.GetString()!);
            }
        }
    }

    private static string RepositoryPath(RepositorySlug repository) =>
        $"/repos/{Segment(repository.Owner)}/{Segment(repository.Name)}";

    private static string Segment(string value) => Uri.EscapeDataString(value);

    private static void ValidateBaseAddress(Uri? baseAddress)
    {
        if (baseAddress is null ||
            !baseAddress.IsAbsoluteUri ||
            !string.Equals(baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.Equals(baseAddress.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
            baseAddress.Port != 443 ||
            baseAddress.AbsolutePath != "/")
        {
            throw new ArgumentException(
                "GitHub HttpClient BaseAddress must be https://api.github.com/.",
                nameof(baseAddress));
        }
    }

    private static void ValidateOptions(GitHubClientOptions options)
    {
        if (options.Token is { Length: > 0 } token &&
            (string.IsNullOrWhiteSpace(token) ||
             token.Length > 1024 ||
             token.Contains('\r') ||
             token.Contains('\n')))
        {
            throw new ArgumentException("The GitHub token format is invalid.", nameof(options));
        }

        if (options.MaxRequestsPerRefresh is < 1 or > 1000 ||
            options.MaxPagesPerEndpoint is < 1 or > 100 ||
            options.MaxPullRequests is < 1 or > 500 ||
            options.MaxResponseBytes is < 1024 or > 50 * 1024 * 1024 ||
            options.MaxRetryAttempts is < 1 or > 5 ||
            options.MergeabilityAttempts is < 1 or > 5 ||
            options.RetryDelay < TimeSpan.Zero ||
            options.RetryDelay > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "One or more GitHub client safety limits are invalid.");
        }
    }

    private static void ValidateRepository(RepositorySlug repository)
    {
        if (string.IsNullOrWhiteSpace(repository.Owner) ||
            string.IsNullOrWhiteSpace(repository.Name) ||
            repository.Owner.Length > 100 ||
            repository.Name.Length > 100)
        {
            throw new ArgumentException("The GitHub repository name is invalid.", nameof(repository));
        }
    }

    private static void ValidateEtag(string? etag)
    {
        if (etag is { Length: > 512 } ||
            etag?.Contains('\r') == true ||
            etag?.Contains('\n') == true ||
            etag is not null && !EntityTagHeaderValue.TryParse(etag, out _))
        {
            throw new ArgumentException("The GitHub ETag format is invalid.", nameof(etag));
        }
    }

    private sealed record PullRequestListResult(
        PullRequestFetchStatus Status,
        IReadOnlyList<JsonElement> PullRequests,
        string? ETag);

    private sealed class RequestContext(int requestLimit)
    {
        private int requests;

        public ProviderRateLimit RateLimit { get; private set; } = new(null, null, null);

        public void StartRequest()
        {
            requests++;
            if (requests > requestLimit)
            {
                throw new GitHubDataException("The GitHub request budget was exhausted.");
            }
        }

        public void UpdateRateLimit(HttpResponseHeaders headers)
        {
            var limit = HeaderInt32(headers, "X-RateLimit-Limit") ?? RateLimit.Limit;
            var remaining = HeaderInt32(headers, "X-RateLimit-Remaining") ?? RateLimit.Remaining;
            var resetSeconds = HeaderInt64(headers, "X-RateLimit-Reset");
            var resetsAt = SafeUnixTime(resetSeconds) ?? RateLimit.ResetsAt;
            RateLimit = new ProviderRateLimit(limit, remaining, resetsAt);
        }

        private static DateTimeOffset? SafeUnixTime(long? seconds)
        {
            if (seconds is null)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static int? HeaderInt32(HttpResponseHeaders headers, string name) =>
            headers.TryGetValues(name, out var values) &&
            int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var result)
                ? result
                : null;

        private static long? HeaderInt64(HttpResponseHeaders headers, string name) =>
            headers.TryGetValues(name, out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var result)
                ? result
                : null;
    }

    private sealed class GitHubDataException : Exception
    {
        public GitHubDataException(string message)
            : base(message)
        {
        }

        public GitHubDataException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed class GitHubRateLimitException : Exception;
}
