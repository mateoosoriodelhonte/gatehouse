using Gatehouse.Domain;

namespace Gatehouse.Application;

public sealed record PolicyParseResult(
    RepositoryPolicy? Policy,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Policy is not null && Errors.Count == 0;
}

public static class PolicyConfigurationParser
{
    private static readonly Dictionary<string, Action<PolicyBuilder, bool>> Setters =
        new Dictionary<string, Action<PolicyBuilder, bool>>(StringComparer.Ordinal)
        {
            ["require_linked_issue"] = (builder, value) => builder.RequireLinkedIssue = value,
            ["require_all_checks"] = (builder, value) => builder.RequireAllChecks = value,
            ["require_approval"] = (builder, value) => builder.RequireApproval = value,
            ["require_no_unresolved_threads"] =
                (builder, value) => builder.RequireNoUnresolvedThreads = value,
            ["require_mergeable"] = (builder, value) => builder.RequireMergeable = value,
            ["require_current_branch"] = (builder, value) => builder.RequireCurrentBranch = value,
            ["block_on_changes_requested"] =
                (builder, value) => builder.BlockOnChangesRequested = value,
        };

    public static PolicyParseResult Parse(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new PolicyParseResult(RepositoryPolicy.SafeDefaults, []);
        }

        var errors = new List<string>();
        var builder = new PolicyBuilder(RepositoryPolicy.SafeDefaults);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var rootSeen = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var rawLine = lines[index];
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!rootSeen)
            {
                if (!string.Equals(line, "readiness:", StringComparison.Ordinal))
                {
                    errors.Add("Only the readiness root is supported.");
                    break;
                }

                rootSeen = true;
                continue;
            }

            if (rawLine.Length == line.Length)
            {
                errors.Add($"Readiness key on line {index + 1} must be indented.");
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                errors.Add($"Invalid readiness entry on line {index + 1}.");
                continue;
            }

            var key = line[..separator].Trim();
            var rawValue = line[(separator + 1)..].Trim();
            if (!Setters.TryGetValue(key, out var setter))
            {
                errors.Add($"Unknown readiness key: {key}.");
                continue;
            }

            if (!seenKeys.Add(key))
            {
                errors.Add($"Duplicate readiness key: {key}.");
                continue;
            }

            if (!bool.TryParse(rawValue, out var value))
            {
                errors.Add($"{key} must be true or false.");
                continue;
            }

            setter(builder, value);
        }

        if (!rootSeen && errors.Count == 0)
        {
            errors.Add("The readiness root is required.");
        }

        return errors.Count == 0
            ? new PolicyParseResult(builder.Build(), [])
            : new PolicyParseResult(null, errors);
    }

    private sealed class PolicyBuilder(RepositoryPolicy policy)
    {
        public bool RequireLinkedIssue { get; set; } = policy.RequireLinkedIssue;

        public bool RequireAllChecks { get; set; } = policy.RequireAllChecks;

        public bool RequireApproval { get; set; } = policy.RequireApproval;

        public bool RequireNoUnresolvedThreads { get; set; } = policy.RequireNoUnresolvedThreads;

        public bool RequireMergeable { get; set; } = policy.RequireMergeable;

        public bool RequireCurrentBranch { get; set; } = policy.RequireCurrentBranch;

        public bool BlockOnChangesRequested { get; set; } = policy.BlockOnChangesRequested;

        public RepositoryPolicy Build() => new()
        {
            Version = policy.Version,
            RequireLinkedIssue = RequireLinkedIssue,
            RequireAllChecks = RequireAllChecks,
            RequireApproval = RequireApproval,
            RequireNoUnresolvedThreads = RequireNoUnresolvedThreads,
            RequireMergeable = RequireMergeable,
            RequireCurrentBranch = RequireCurrentBranch,
            BlockOnChangesRequested = BlockOnChangesRequested,
        };
    }
}
