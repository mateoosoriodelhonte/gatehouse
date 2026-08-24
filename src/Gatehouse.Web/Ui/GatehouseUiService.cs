using Gatehouse.Application;
using Gatehouse.Domain;
using Gatehouse.Infrastructure.GitHub;

namespace Gatehouse.Web.Ui;

public sealed class GatehouseUiService(
    ILocalReadinessStore store,
    GitHubClientOptions gitHubOptions)
{
    public bool IsGitHubTokenConfigured => !string.IsNullOrWhiteSpace(gitHubOptions.Token);

    public async Task<IReadOnlyList<LocalRepositorySummary>> ListRepositoriesAsync(
        CancellationToken cancellationToken = default) =>
        await store.ListRepositoriesAsync(cancellationToken);

    public async Task<LocalRepositoryDetail?> GetRepositoryAsync(
        Guid repositoryId,
        CancellationToken cancellationToken = default) =>
        repositoryId == DemoReadinessCatalog.RepositoryId
            ? DemoReadinessCatalog.Create()
            : await store.GetRepositoryAsync(repositoryId, cancellationToken);

    public async Task<LocalRepositorySummary> AddRepositoryAsync(
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!RepositoryInputValidator.TryValidateRepository(owner, name, out var error))
        {
            throw new ArgumentException(error, nameof(owner));
        }

        var repository = await store.AddRepositoryAsync(
            new RepositoryRegistration(owner, name, RepositoryPolicy.SafeDefaults),
            cancellationToken);
        await store.SelectRepositoryAsync(repository.Id, cancellationToken);
        return repository with { IsSelected = true };
    }

    public async Task<RepositoryRefreshResult?> RefreshAsync(
        Guid repositoryId,
        CancellationToken cancellationToken = default) =>
        repositoryId == DemoReadinessCatalog.RepositoryId
            ? null
            : await store.RefreshRepositoryAsync(repositoryId, cancellationToken);

    public async Task<bool> UpdatePolicyAsync(
        Guid repositoryId,
        RepositoryPolicy policy,
        CancellationToken cancellationToken = default) =>
        repositoryId != DemoReadinessCatalog.RepositoryId &&
        await store.UpdatePolicyAsync(repositoryId, policy, cancellationToken);

    public async Task<bool> RemoveRepositoryAsync(
        Guid repositoryId,
        CancellationToken cancellationToken = default) =>
        repositoryId != DemoReadinessCatalog.RepositoryId &&
        await store.RemoveRepositoryAsync(repositoryId, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        store.ClearAsync(cancellationToken);

    public static string? SafeEvidenceUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
}
