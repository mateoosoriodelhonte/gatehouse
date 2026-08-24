using Gatehouse.Domain;

namespace Gatehouse.Application;

public static class RepositoryInputValidator
{
    public static bool TryValidateRepository(
        string? owner,
        string? name,
        out string error)
    {
        if (!IsOwner(owner))
        {
            error = "Owner must use 1 to 39 letters, numbers, or single hyphens.";
            return false;
        }

        if (!IsRepositoryName(name))
        {
            error = "Repository must use 1 to 100 letters, numbers, periods, underscores, or hyphens.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryValidatePolicy(RepositoryPolicy? policy, out string error)
    {
        if (policy is null)
        {
            error = "Policy is required.";
            return false;
        }

        if (policy.Version != RepositoryPolicy.SafeDefaults.Version)
        {
            error = $"Policy version must be {RepositoryPolicy.SafeDefaults.Version}.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool IsAuthor(string? author) => IsOwner(author);

    private static bool IsOwner(string? value) =>
        value is { Length: >= 1 and <= 39 } &&
        value[0] != '-' &&
        value[^1] != '-' &&
        !value.Contains("--", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsRepositoryName(string? value) =>
        value is { Length: >= 1 and <= 100 } &&
        value is not "." and not ".." &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
