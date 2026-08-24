using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gatehouse.Domain;

namespace Gatehouse.Infrastructure.GitHub;

public static class GitHubSnapshotNormalizer
{
    private static readonly Regex ClosingReferencePattern = new(
        @"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#(?<number>\d+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex IssueReferencePattern = new(
        @"(?<![\w/])#(?<number>\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static PullRequestSnapshot FromRest(
        RepositorySlug repository,
        JsonElement pullRequest,
        JsonElement checkRuns,
        JsonElement reviews,
        JsonElement comparison,
        JsonElement files,
        IReadOnlySet<string> requiredCheckNames,
        DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(requiredCheckNames);

        var body = OptionalString(pullRequest, "body") ?? string.Empty;
        var requestedReviewers = ArrayLength(pullRequest, "requested_reviewers");
        var requestedTeams = ArrayLength(pullRequest, "requested_teams");
        var normalizedReviews = NormalizeRestReviews(reviews);

        return new PullRequestSnapshot
        {
            Repository = repository,
            Number = RequiredInt32(pullRequest, "number"),
            Title = RequiredString(pullRequest, "title"),
            Author = RequiredString(RequiredObject(pullRequest, "user"), "login"),
            State = RestPullRequestState(pullRequest),
            IsDraft = RequiredBoolean(pullRequest, "draft"),
            Mergeability = RestMergeability(pullRequest),
            ReviewDecision = normalizedReviews.Decision,
            ApprovalCount = normalizedReviews.ApprovalCount,
            RequestedReviewerCount = requestedReviewers + requestedTeams,
            RequestedReviewers = NamedArray(pullRequest, "requested_reviewers", "login")
                .Concat(NamedArray(pullRequest, "requested_teams", "slug"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToArray(),
            UnresolvedReviewThreadCount = null,
            BranchFreshness = RestFreshness(comparison),
            Checks = NormalizeRestChecks(checkRuns, requiredCheckNames),
            IssueLinks = ParseIssueLinks(body),
            Labels = NamedArray(pullRequest, "labels", "name"),
            UpdatedAt = RequiredDateTimeOffset(pullRequest, "updated_at"),
            FetchedAt = fetchedAt,
            Url = RequiredString(pullRequest, "html_url"),
            BaseBranch = RequiredString(RequiredObject(pullRequest, "base"), "ref"),
            HeadBranch = RequiredString(RequiredObject(pullRequest, "head"), "ref"),
            BaseSha = RequiredString(RequiredObject(pullRequest, "base"), "sha"),
            HeadSha = RequiredString(RequiredObject(pullRequest, "head"), "sha"),
            ChangedFiles = RequiredInt32(pullRequest, "changed_files"),
            Additions = RequiredInt32(pullRequest, "additions"),
            Deletions = RequiredInt32(pullRequest, "deletions"),
            Files = NormalizeRestFiles(files),
        };
    }

    public static PullRequestSnapshot FromGraphQl(
        RepositorySlug repository,
        JsonElement pullRequest,
        IReadOnlySet<string> requiredCheckNames,
        DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(requiredCheckNames);

        var reviews = RequiredObject(pullRequest, "reviews").GetProperty("nodes");
        var reviewRequests = RequiredObject(pullRequest, "reviewRequests").GetProperty("nodes");

        return new PullRequestSnapshot
        {
            Repository = repository,
            Number = RequiredInt32(pullRequest, "number"),
            Title = RequiredString(pullRequest, "title"),
            Author = RequiredString(RequiredObject(pullRequest, "author"), "login"),
            State = GraphQlPullRequestState(RequiredString(pullRequest, "state")),
            IsDraft = RequiredBoolean(pullRequest, "isDraft"),
            Mergeability = GraphQlMergeability(RequiredString(pullRequest, "mergeable")),
            ReviewDecision = GraphQlReviewDecision(OptionalString(pullRequest, "reviewDecision")),
            ApprovalCount = reviews.EnumerateArray().Count(review =>
                string.Equals(OptionalString(review, "state"), "APPROVED", StringComparison.Ordinal)),
            RequestedReviewerCount = reviewRequests.GetArrayLength(),
            RequestedReviewers = NormalizeGraphQlReviewers(reviewRequests),
            UnresolvedReviewThreadCount = CountUnresolvedThreads(pullRequest),
            BranchFreshness = GraphQlFreshness(OptionalString(pullRequest, "mergeStateStatus")),
            Checks = NormalizeGraphQlChecks(pullRequest, requiredCheckNames),
            IssueLinks = NormalizeGraphQlIssueLinks(pullRequest),
            Labels = NamedNodes(pullRequest, "labels", "name"),
            UpdatedAt = RequiredDateTimeOffset(pullRequest, "updatedAt"),
            FetchedAt = fetchedAt,
            Url = RequiredString(pullRequest, "url"),
            BaseBranch = RequiredString(pullRequest, "baseRefName"),
            HeadBranch = RequiredString(pullRequest, "headRefName"),
            BaseSha = RequiredString(pullRequest, "baseRefOid"),
            HeadSha = RequiredString(pullRequest, "headRefOid"),
            ChangedFiles = RequiredInt32(pullRequest, "changedFiles"),
            Additions = RequiredInt32(pullRequest, "additions"),
            Deletions = RequiredInt32(pullRequest, "deletions"),
            Files = NormalizeGraphQlFiles(pullRequest),
        };
    }

    private static CheckSnapshot[] NormalizeRestChecks(
        JsonElement payload,
        IReadOnlySet<string> requiredCheckNames) =>
        RequiredArray(payload, "check_runs")
            .Select(check => new CheckSnapshot(
                RequiredString(check, "name"),
                NormalizeCheckRun(
                    RequiredString(check, "status"),
                    OptionalString(check, "conclusion")),
                requiredCheckNames.Contains(RequiredString(check, "name")),
                OptionalString(check, "details_url")))
            .OrderBy(check => check.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(check => check.Name, StringComparer.Ordinal)
            .ToArray();

    private static CheckSnapshot[] NormalizeGraphQlChecks(
        JsonElement pullRequest,
        IReadOnlySet<string> requiredCheckNames)
    {
        if (!pullRequest.TryGetProperty("statusCheckRollup", out var rollup) ||
            rollup.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        var nodes = RequiredObject(rollup, "contexts").GetProperty("nodes");
        return nodes.EnumerateArray()
            .Select(node => NormalizeGraphQlCheck(node, requiredCheckNames))
            .OrderBy(check => check.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(check => check.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static CheckSnapshot NormalizeGraphQlCheck(
        JsonElement node,
        IReadOnlySet<string> requiredCheckNames)
    {
        var type = RequiredString(node, "__typename");
        if (string.Equals(type, "StatusContext", StringComparison.Ordinal))
        {
            var name = RequiredString(node, "context");
            return new CheckSnapshot(
                name,
                NormalizeStatusContext(RequiredString(node, "state")),
                requiredCheckNames.Contains(name),
                OptionalString(node, "targetUrl"));
        }

        var checkName = RequiredString(node, "name");
        return new CheckSnapshot(
            checkName,
            NormalizeCheckRun(
                RequiredString(node, "status"),
                OptionalString(node, "conclusion")),
            requiredCheckNames.Contains(checkName),
            OptionalString(node, "detailsUrl"));
    }

    private static CheckState NormalizeCheckRun(string status, string? conclusion)
    {
        if (string.Equals(status, "WAITING", StringComparison.OrdinalIgnoreCase))
        {
            return CheckState.ActionRequired;
        }

        if (!string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return CheckState.Pending;
        }

        return conclusion?.ToUpperInvariant() switch
        {
            "SUCCESS" => CheckState.Success,
            "NEUTRAL" => CheckState.Neutral,
            "SKIPPED" => CheckState.Skipped,
            "CANCELLED" => CheckState.Cancelled,
            "ACTION_REQUIRED" => CheckState.ActionRequired,
            "FAILURE" or "TIMED_OUT" or "STARTUP_FAILURE" or "STALE" => CheckState.Failure,
            null => CheckState.NotExecuted,
            _ => CheckState.Unknown,
        };
    }

    private static CheckState NormalizeStatusContext(string state) => state.ToUpperInvariant() switch
    {
        "SUCCESS" => CheckState.Success,
        "FAILURE" or "ERROR" => CheckState.Failure,
        "PENDING" or "EXPECTED" => CheckState.Pending,
        _ => CheckState.Unknown,
    };

    private static (ReviewDecision Decision, int ApprovalCount) NormalizeRestReviews(
        JsonElement reviews)
    {
        var latestByReviewer = reviews.EnumerateArray()
            .Where(review =>
                OptionalString(review, "state") is "APPROVED" or "CHANGES_REQUESTED")
            .GroupBy(
                review => RequiredString(RequiredObject(review, "user"), "login"),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(review => RequiredDateTimeOffset(review, "submitted_at"))
                .First())
            .ToArray();
        var approvals = latestByReviewer.Count(review =>
            string.Equals(OptionalString(review, "state"), "APPROVED", StringComparison.Ordinal));

        if (latestByReviewer.Any(review =>
            string.Equals(OptionalString(review, "state"), "CHANGES_REQUESTED", StringComparison.Ordinal)))
        {
            return (ReviewDecision.ChangesRequested, approvals);
        }

        return approvals > 0
            ? (ReviewDecision.Approved, approvals)
            : (ReviewDecision.ReviewRequired, 0);
    }

    private static IssueLink[] ParseIssueLinks(string body)
    {
        var explicitNumbers = ClosingReferencePattern.Matches(body).Cast<Match>()
            .Select(match => ParsePositiveInt(match.Groups["number"].Value))
            .ToHashSet();
        var links = explicitNumbers
            .Select(number => new IssueLink(number, IssueLinkKind.Explicit, false, null))
            .ToList();

        links.AddRange(IssueReferencePattern.Matches(body).Cast<Match>()
            .Select(match => ParsePositiveInt(match.Groups["number"].Value))
            .Where(number => !explicitNumbers.Contains(number))
            .Distinct()
            .Select(number => new IssueLink(number, IssueLinkKind.PossibleReference, false, null)));

        return links.OrderBy(link => link.Number).ToArray();
    }

    private static IssueLink[] NormalizeGraphQlIssueLinks(JsonElement pullRequest)
    {
        var nodes = RequiredObject(pullRequest, "closingIssuesReferences").GetProperty("nodes");
        return nodes.EnumerateArray()
            .Select(issue => new IssueLink(
                RequiredInt32(issue, "number"),
                IssueLinkKind.Explicit,
                string.Equals(RequiredString(issue, "state"), "CLOSED", StringComparison.Ordinal),
                OptionalString(issue, "url")))
            .OrderBy(link => link.Number)
            .ToArray();
    }

    private static string[] NormalizeGraphQlReviewers(JsonElement nodes) =>
        nodes.EnumerateArray()
            .Select(node => RequiredObject(node, "requestedReviewer"))
            .Select(reviewer =>
                OptionalString(reviewer, "login") ?? OptionalString(reviewer, "slug"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string[] NamedNodes(
        JsonElement element,
        string connectionName,
        string propertyName) =>
        RequiredObject(element, connectionName)
            .GetProperty("nodes")
            .EnumerateArray()
            .Select(node => RequiredString(node, propertyName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string[] NamedArray(
        JsonElement element,
        string arrayName,
        string propertyName) =>
        RequiredArray(element, arrayName)
            .Select(item => RequiredString(item, propertyName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static ChangedFile[] NormalizeRestFiles(JsonElement files) =>
        files.EnumerateArray()
            .Select(file => new ChangedFile(
                RequiredString(file, "filename"),
                RequiredString(file, "status"),
                RequiredInt32(file, "additions"),
                RequiredInt32(file, "deletions"),
                OptionalString(file, "blob_url")))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

    private static ChangedFile[] NormalizeGraphQlFiles(JsonElement pullRequest)
    {
        var nodes = RequiredObject(pullRequest, "files").GetProperty("nodes");
        return nodes.EnumerateArray()
            .Select(file => new ChangedFile(
                RequiredString(file, "path"),
                RequiredString(file, "changeType").ToLowerInvariant(),
                RequiredInt32(file, "additions"),
                RequiredInt32(file, "deletions"),
                null))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static int CountUnresolvedThreads(JsonElement pullRequest)
    {
        var nodes = RequiredObject(pullRequest, "reviewThreads").GetProperty("nodes");
        return nodes.EnumerateArray().Count(thread => !RequiredBoolean(thread, "isResolved"));
    }

    private static PullRequestState RestPullRequestState(JsonElement pullRequest)
    {
        var state = RequiredString(pullRequest, "state");
        if (string.Equals(state, "open", StringComparison.OrdinalIgnoreCase))
        {
            return PullRequestState.Open;
        }

        return pullRequest.TryGetProperty("merged_at", out var mergedAt) &&
            mergedAt.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? PullRequestState.Merged
                : PullRequestState.Closed;
    }

    private static PullRequestState GraphQlPullRequestState(string state) => state switch
    {
        "OPEN" => PullRequestState.Open,
        "MERGED" => PullRequestState.Merged,
        "CLOSED" => PullRequestState.Closed,
        _ => PullRequestState.Unknown,
    };

    private static Mergeability RestMergeability(JsonElement pullRequest)
    {
        if (!pullRequest.TryGetProperty("mergeable", out var mergeable) ||
            mergeable.ValueKind == JsonValueKind.Null)
        {
            return Mergeability.Unknown;
        }

        return mergeable.ValueKind == JsonValueKind.True
            ? Mergeability.Clean
            : Mergeability.Conflicting;
    }

    private static Mergeability GraphQlMergeability(string mergeability) => mergeability switch
    {
        "MERGEABLE" => Mergeability.Clean,
        "CONFLICTING" => Mergeability.Conflicting,
        _ => Mergeability.Unknown,
    };

    private static ReviewDecision GraphQlReviewDecision(string? decision) => decision switch
    {
        "APPROVED" => ReviewDecision.Approved,
        "CHANGES_REQUESTED" => ReviewDecision.ChangesRequested,
        "REVIEW_REQUIRED" => ReviewDecision.ReviewRequired,
        _ => ReviewDecision.Unknown,
    };

    private static BranchFreshness RestFreshness(JsonElement comparison) =>
        RequiredInt32(comparison, "behind_by") > 0
            ? BranchFreshness.Behind
            : BranchFreshness.Current;

    private static BranchFreshness GraphQlFreshness(string? mergeStateStatus) => mergeStateStatus switch
    {
        "BEHIND" => BranchFreshness.Behind,
        "CLEAN" or "BLOCKED" or "HAS_HOOKS" or "UNSTABLE" => BranchFreshness.Current,
        _ => BranchFreshness.Unknown,
    };

    private static JsonElement RequiredObject(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Object
            ? value
            : throw InvalidProperty(propertyName);
    }

    private static JsonElement.ArrayEnumerator RequiredArray(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : throw InvalidProperty(propertyName);
    }

    private static int ArrayLength(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : throw InvalidProperty(propertyName);
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        OptionalString(element, propertyName) ?? throw InvalidProperty(propertyName);

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int RequiredInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : throw InvalidProperty(propertyName);

    private static bool RequiredBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw InvalidProperty(propertyName);

    private static DateTimeOffset RequiredDateTimeOffset(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.TryGetDateTimeOffset(out var result)
            ? result
            : throw InvalidProperty(propertyName);

    private static int ParsePositiveInt(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : throw new InvalidDataException("GitHub returned an invalid issue reference.");

    private static InvalidDataException InvalidProperty(string propertyName) =>
        new($"GitHub returned an invalid or missing {propertyName} field.");
}
