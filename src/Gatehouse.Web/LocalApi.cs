using System.Globalization;
using Gatehouse.Application;
using Gatehouse.Domain;

namespace Gatehouse.Web;

public static class LocalApi
{
    public static IEndpointRouteBuilder MapGatehouseApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var api = endpoints.MapGroup("/api/v1");

        api.MapGet("/health", () => Results.Ok(new { status = "ok", version = "1" }));
        api.MapGet("/repositories", async (
            ILocalReadinessStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(await store.ListRepositoriesAsync(cancellationToken)));

        api.MapPost("/repositories", AddRepositoryAsync)
            .AddEndpointFilter(RequireMutationHeaderAsync);
        api.MapGet("/repositories/{repositoryId:guid}", GetRepositoryAsync);
        api.MapPut("/repositories/{repositoryId:guid}/selection", SelectRepositoryAsync)
            .AddEndpointFilter(RequireMutationHeaderAsync);
        api.MapPut("/repositories/{repositoryId:guid}/policy", UpdatePolicyAsync)
            .AddEndpointFilter(RequireMutationHeaderAsync);
        api.MapPost("/repositories/{repositoryId:guid}/refresh", RefreshRepositoryAsync)
            .AddEndpointFilter(RequireMutationHeaderAsync);
        api.MapDelete("/repositories/{repositoryId:guid}", RemoveRepositoryAsync)
            .AddEndpointFilter(RequireMutationHeaderAsync);
        api.MapGet("/repositories/{repositoryId:guid}/pull-requests", ListPullRequestsAsync);
        api.MapGet(
            "/repositories/{repositoryId:guid}/pull-requests/{pullRequestNumber:int:min(1)}",
            GetPullRequestAsync);
        api.MapDelete("/local-data", ClearLocalDataAsync)
            .AddEndpointFilter(RequireMutationHeaderAsync);

        return endpoints;
    }

    private static async Task<IResult> AddRepositoryAsync(
        AddRepositoryRequest request,
        ILocalReadinessStore store,
        CancellationToken cancellationToken)
    {
        if (!RepositoryInputValidator.TryValidateRepository(
            request.Owner,
            request.Name,
            out var repositoryError))
        {
            return Validation("repository", repositoryError);
        }

        var policy = request.Policy?.ToPolicy() ?? RepositoryPolicy.SafeDefaults;
        if (!RepositoryInputValidator.TryValidatePolicy(policy, out var policyError))
        {
            return Validation("policy", policyError);
        }

        try
        {
            var repository = await store.AddRepositoryAsync(
                new RepositoryRegistration(request.Owner!, request.Name!, policy),
                cancellationToken);
            return Results.Created($"/api/v1/repositories/{repository.Id}", repository);
        }
        catch (DuplicateRepositoryException exception)
        {
            return Results.Conflict(new
            {
                title = "Repository already exists",
                status = StatusCodes.Status409Conflict,
                detail = exception.Message,
            });
        }
    }

    private static async Task<IResult> GetRepositoryAsync(
        Guid repositoryId,
        ILocalReadinessStore store,
        CancellationToken cancellationToken)
    {
        var repository = await store.GetRepositoryAsync(repositoryId, cancellationToken);
        return repository is null ? Results.NotFound() : Results.Ok(repository);
    }

    private static async Task<IResult> SelectRepositoryAsync(
        Guid repositoryId,
        ILocalReadinessStore store,
        CancellationToken cancellationToken) =>
        await store.SelectRepositoryAsync(repositoryId, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> UpdatePolicyAsync(
        Guid repositoryId,
        PolicyRequest request,
        ILocalReadinessStore store,
        CancellationToken cancellationToken)
    {
        var policy = request.ToPolicy();
        if (!RepositoryInputValidator.TryValidatePolicy(policy, out var error))
        {
            return Validation("policy", error);
        }

        return await store.UpdatePolicyAsync(repositoryId, policy, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> RefreshRepositoryAsync(
        Guid repositoryId,
        ILocalReadinessStore store,
        CancellationToken cancellationToken)
    {
        var result = await store.RefreshRepositoryAsync(repositoryId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> RemoveRepositoryAsync(
        Guid repositoryId,
        ILocalReadinessStore store,
        CancellationToken cancellationToken) =>
        await store.RemoveRepositoryAsync(repositoryId, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> ListPullRequestsAsync(
        Guid repositoryId,
        string? status,
        string? author,
        bool? stale,
        ILocalReadinessStore store,
        CancellationToken cancellationToken)
    {
        ReadinessStatus? parsedStatus = null;
        if (status is not null)
        {
            if (int.TryParse(status, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
                !Enum.TryParse<ReadinessStatus>(status, ignoreCase: true, out var statusValue) ||
                !Enum.IsDefined(statusValue))
            {
                return Validation(
                    "status",
                    "Status must be go, review, blocked, draft, or unknown.");
            }

            parsedStatus = statusValue;
        }

        if (author is not null && !RepositoryInputValidator.IsAuthor(author))
        {
            return Validation("author", "Author must be a valid GitHub login.");
        }

        var repository = await store.GetRepositoryAsync(repositoryId, cancellationToken);
        if (repository is null)
        {
            return Results.NotFound();
        }

        var results = repository.PullRequests
            .Where(item => parsedStatus is null || item.Evaluation.Status == parsedStatus)
            .Where(item => author is null || string.Equals(
                item.Snapshot.Author,
                author,
                StringComparison.OrdinalIgnoreCase))
            .Where(item => stale is null || item.IsStale == stale)
            .ToArray();
        return Results.Ok(results);
    }

    private static async Task<IResult> GetPullRequestAsync(
        Guid repositoryId,
        int pullRequestNumber,
        ILocalReadinessStore store,
        CancellationToken cancellationToken)
    {
        var repository = await store.GetRepositoryAsync(repositoryId, cancellationToken);
        var pullRequest = repository?.PullRequests.SingleOrDefault(
            item => item.Snapshot.Number == pullRequestNumber);
        return pullRequest is null ? Results.NotFound() : Results.Ok(pullRequest);
    }

    private static async Task<IResult> ClearLocalDataAsync(
        ILocalReadinessStore store,
        CancellationToken cancellationToken)
    {
        await store.ClearAsync(cancellationToken);
        return Results.NoContent();
    }

    private static IResult Validation(string key, string error) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [error] });

    private static async ValueTask<object?> RequireMutationHeaderAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var requestHeader = context.HttpContext.Request.Headers["X-Gatehouse-Request"];
        if (requestHeader.Count != 1 ||
            !string.Equals(requestHeader[0], "1", StringComparison.Ordinal))
        {
            return Results.BadRequest(new
            {
                title = "Local request header required",
                status = StatusCodes.Status400BadRequest,
            });
        }

        return await next(context);
    }
}

public sealed record AddRepositoryRequest(
    string? Owner,
    string? Name,
    PolicyRequest? Policy);

public sealed record PolicyRequest(
    int? Version,
    bool? RequireLinkedIssue,
    bool? RequireAllChecks,
    bool? RequireApproval,
    bool? RequireNoUnresolvedThreads,
    bool? RequireMergeable,
    bool? RequireCurrentBranch,
    bool? BlockOnChangesRequested)
{
    public RepositoryPolicy ToPolicy()
    {
        var defaults = RepositoryPolicy.SafeDefaults;
        return new RepositoryPolicy
        {
            Version = Version ?? defaults.Version,
            RequireLinkedIssue = RequireLinkedIssue ?? defaults.RequireLinkedIssue,
            RequireAllChecks = RequireAllChecks ?? defaults.RequireAllChecks,
            RequireApproval = RequireApproval ?? defaults.RequireApproval,
            RequireNoUnresolvedThreads = RequireNoUnresolvedThreads ??
                defaults.RequireNoUnresolvedThreads,
            RequireMergeable = RequireMergeable ?? defaults.RequireMergeable,
            RequireCurrentBranch = RequireCurrentBranch ?? defaults.RequireCurrentBranch,
            BlockOnChangesRequested = BlockOnChangesRequested ?? defaults.BlockOnChangesRequested,
        };
    }
}
