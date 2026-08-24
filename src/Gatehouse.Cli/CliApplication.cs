using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gatehouse.Application;
using Gatehouse.Domain;

namespace Gatehouse.Cli;

public static class CliExitCodes
{
    public const int Success = 0;
    public const int PolicyBlocked = 2;
    public const int Unknown = 3;
    public const int InvalidInput = 64;
    public const int ProviderFailure = 69;
    public const int InternalFailure = 70;
    public const int Cancelled = 130;
}

public sealed class CliApplication
{
    private const int MaximumConfigurationBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
    private readonly ILocalReadinessStore? store;
    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly Func<int, CancellationToken, Task<int>> serveAsync;
    private readonly Func<string> currentDirectory;

    public CliApplication(
        ILocalReadinessStore? store,
        TextWriter output,
        TextWriter error,
        Func<int, CancellationToken, Task<int>>? serveAsync = null,
        Func<string>? currentDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        this.store = store;
        this.output = output;
        this.error = error;
        this.serveAsync = serveAsync ?? ((_, _) => Task.FromResult(CliExitCodes.Success));
        this.currentDirectory = currentDirectory ?? Directory.GetCurrentDirectory;
    }

    public static bool NeedsStore(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Contains("--demo", StringComparer.Ordinal) ||
            args.Contains("--help", StringComparer.Ordinal) ||
            args.Contains("-h", StringComparer.Ordinal))
        {
            return false;
        }

        return args.Any(argument => argument is "repo" or "status" or "ready" or "pr" or "report");
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The command boundary must return a stable code without exposing secrets.")]
    public async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsed = ParsedArguments.Parse(args);
            if (parsed.Error is not null)
            {
                return await InvalidAsync(parsed.Error);
            }

            if (parsed.Help || parsed.Positionals.Count == 0 || parsed.Positionals[0] == "help")
            {
                await output.WriteAsync(HelpText);
                return CliExitCodes.Success;
            }

            return parsed.Positionals[0] switch
            {
                "version" => await VersionAsync(parsed),
                "repo" => await AddRepositoryAsync(parsed, cancellationToken),
                "status" => await StatusAsync(parsed, readyOnly: false, cancellationToken),
                "ready" => await StatusAsync(parsed, readyOnly: true, cancellationToken),
                "pr" => await PullRequestAsync(parsed, reportOnly: false, cancellationToken),
                "report" => await PullRequestAsync(parsed, reportOnly: true, cancellationToken),
                "serve" => await ServeAsync(parsed, cancellationToken),
                _ => await InvalidAsync("Unknown command. Run: gatehouse help"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Gatehouse was cancelled.");
            return CliExitCodes.Cancelled;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("Gatehouse could not read or write its local data.");
            return CliExitCodes.InternalFailure;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Gatehouse could not complete the command.");
            return CliExitCodes.InternalFailure;
        }
    }

    private async Task<int> VersionAsync(ParsedArguments parsed)
    {
        if (!parsed.HasOnly("--json") || parsed.Positionals.Count != 1)
        {
            return await InvalidAsync("Usage: gatehouse version [--json]");
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        if (parsed.Json)
        {
            await WriteJsonAsync(new { schemaVersion = "1.0", version });
        }
        else
        {
            await output.WriteLineAsync($"Gatehouse {version}");
        }

        return CliExitCodes.Success;
    }

    private async Task<int> AddRepositoryAsync(
        ParsedArguments parsed,
        CancellationToken cancellationToken)
    {
        if (!parsed.HasOnly("--json", "--config") ||
            parsed.Positionals.Count != 3 ||
            parsed.Positionals[1] != "add")
        {
            return await InvalidAsync(
                "Usage: gatehouse repo add OWNER/REPOSITORY [--config PATH] [--json]");
        }

        if (!TryParseRepository(parsed.Positionals[2], out var repository, out var repositoryError))
        {
            return await InvalidAsync(repositoryError);
        }

        var policyResult = await LoadPolicyAsync(parsed.ConfigPath, cancellationToken);
        if (policyResult.Error is not null)
        {
            return await InvalidAsync(policyResult.Error);
        }

        try
        {
            var added = await RequiredStore.AddRepositoryAsync(
                new RepositoryRegistration(
                    repository.Owner,
                    repository.Name,
                    policyResult.Policy!),
                cancellationToken);
            await RequiredStore.SelectRepositoryAsync(added.Id, cancellationToken);
            if (parsed.Json)
            {
                await WriteJsonAsync(new RepositoryAddedDocument(
                    "1.0",
                    repository.ToString(),
                    added.Id,
                    policyResult.Source));
            }
            else
            {
                await output.WriteLineAsync($"Added {repository}.");
                await output.WriteLineAsync($"Policy: {policyResult.Source}");
                await output.WriteLineAsync($"Next: gatehouse status {repository}");
            }

            return CliExitCodes.Success;
        }
        catch (DuplicateRepositoryException)
        {
            return await InvalidAsync($"Repository {repository} is already configured.");
        }
    }

    private async Task<int> StatusAsync(
        ParsedArguments parsed,
        bool readyOnly,
        CancellationToken cancellationToken)
    {
        var allowed = new[]
        {
            "--json", "--demo", "--cached", "--status", "--search", "--author",
            "--label", "--branch", "--reviewer", "--ci", "--draft",
        };
        if (!parsed.HasOnly(allowed) ||
            !TryResolveRepository(parsed, needsPullRequestNumber: false, out var repository, out _, out var usageError))
        {
            var command = readyOnly ? "ready" : "status";
            return await InvalidAsync(usageError ??
                $"Usage: gatehouse {command} OWNER/REPOSITORY [filters] [--json] [--cached]");
        }

        if (!TryCreateFilter(parsed, out var filter, out var filterError))
        {
            return await InvalidAsync(filterError);
        }

        var load = await LoadRepositoryAsync(repository, parsed, cancellationToken);
        if (load.ExitCode != CliExitCodes.Success)
        {
            return load.ExitCode;
        }

        var results = PullRequestFilters.Apply(load.Repository!.PullRequests, filter!);
        if (readyOnly)
        {
            results = results.Where(item => item.Evaluation.Status == ReadinessStatus.Go).ToArray();
        }

        if (parsed.Json)
        {
            await WriteJsonAsync(CreateStatusDocument(repository, results));
        }
        else
        {
            await WriteStatusAsync(repository, results, readyOnly);
        }

        return readyOnly ? CliExitCodes.Success : ExitCodeFor(results);
    }

    private async Task<int> PullRequestAsync(
        ParsedArguments parsed,
        bool reportOnly,
        CancellationToken cancellationToken)
    {
        if (!parsed.HasOnly("--json", "--demo", "--cached") ||
            !TryResolveRepository(parsed, needsPullRequestNumber: true, out var repository, out var number, out var usageError))
        {
            var command = reportOnly ? "report" : "pr";
            return await InvalidAsync(usageError ??
                $"Usage: gatehouse {command} OWNER/REPOSITORY NUMBER [--json] [--cached]");
        }

        var load = await LoadRepositoryAsync(repository, parsed, cancellationToken);
        if (load.ExitCode != CliExitCodes.Success)
        {
            return load.ExitCode;
        }

        var item = load.Repository!.PullRequests.SingleOrDefault(
            candidate => candidate.Snapshot.Number == number);
        if (item is null)
        {
            return await InvalidAsync($"Pull request #{number} is not in the current open snapshot.");
        }

        var document = ReadinessDocumentFactory.Create(item.Snapshot, item.Evaluation);
        if (parsed.Json)
        {
            await output.WriteLineAsync(ReadinessJson.Serialize(document));
        }
        else if (reportOnly)
        {
            await output.WriteLineAsync(item.ReportMarkdown.TrimEnd());
        }
        else
        {
            await WritePullRequestAsync(item, document);
        }

        return ExitCodeFor([item]);
    }

    private async Task<int> ServeAsync(
        ParsedArguments parsed,
        CancellationToken cancellationToken)
    {
        if (!parsed.HasOnly("--port") || parsed.Positionals.Count != 1)
        {
            return await InvalidAsync("Usage: gatehouse serve [--port 5341]");
        }

        if (parsed.Port is < 1024 or > 65535)
        {
            return await InvalidAsync("Port must be from 1024 to 65535.");
        }

        await output.WriteLineAsync($"Gatehouse is available at http://localhost:{parsed.Port}/");
        await output.WriteLineAsync("Press Ctrl+C to stop.");
        return await serveAsync(parsed.Port, cancellationToken);
    }

    private async Task<RepositoryLoadResult> LoadRepositoryAsync(
        RepositorySlug repository,
        ParsedArguments parsed,
        CancellationToken cancellationToken)
    {
        if (parsed.Demo)
        {
            return new RepositoryLoadResult(DemoReadinessCatalog.Create(), CliExitCodes.Success);
        }

        var summary = (await RequiredStore.ListRepositoriesAsync(cancellationToken))
            .SingleOrDefault(item =>
                string.Equals(item.Owner, repository.Owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Name, repository.Name, StringComparison.OrdinalIgnoreCase));
        if (summary is null)
        {
            await error.WriteLineAsync(
                $"Repository {repository} is not configured. Run: gatehouse repo add {repository}");
            return new RepositoryLoadResult(null, CliExitCodes.InvalidInput);
        }

        LocalRepositoryDetail? detail;
        if (parsed.Cached)
        {
            detail = await RequiredStore.GetRepositoryAsync(summary.Id, cancellationToken);
        }
        else
        {
            var refresh = await RequiredStore.RefreshRepositoryAsync(summary.Id, cancellationToken);
            if (refresh is null)
            {
                await error.WriteLineAsync("Gatehouse could not find the configured repository.");
                return new RepositoryLoadResult(null, CliExitCodes.InvalidInput);
            }

            if (refresh.Status is not PullRequestFetchStatus.Success and
                not PullRequestFetchStatus.NotModified)
            {
                await error.WriteLineAsync(ProviderError(refresh.Status));
                return new RepositoryLoadResult(null, CliExitCodes.ProviderFailure);
            }

            detail = refresh.Repository;
        }

        if (detail is null)
        {
            await error.WriteLineAsync("Gatehouse could not load the configured repository.");
            return new RepositoryLoadResult(null, CliExitCodes.InternalFailure);
        }

        if (detail.PullRequests.Count == 0 && parsed.Cached)
        {
            await error.WriteLineAsync(
                "No cached pull request evidence is available. Rerun without --cached.");
            return new RepositoryLoadResult(null, CliExitCodes.Unknown);
        }

        return new RepositoryLoadResult(detail, CliExitCodes.Success);
    }

    private async Task<PolicyLoadResult> LoadPolicyAsync(
        string? configuredPath,
        CancellationToken cancellationToken)
    {
        string? path = configuredPath;
        if (path is null)
        {
            var directory = new DirectoryInfo(currentDirectory());
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, ".gatehouse.yml");
                if (File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }

                directory = directory.Parent;
            }
        }

        if (path is null)
        {
            return new PolicyLoadResult(RepositoryPolicy.SafeDefaults, "safe defaults", null);
        }

        try
        {
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists)
            {
                return new PolicyLoadResult(null, string.Empty, "The policy file does not exist.");
            }

            if (file.Length > MaximumConfigurationBytes)
            {
                return new PolicyLoadResult(null, string.Empty, "The policy file is larger than 64 KiB.");
            }

            var yaml = await File.ReadAllTextAsync(file.FullName, Encoding.UTF8, cancellationToken);
            var parsed = PolicyConfigurationParser.Parse(yaml);
            if (!parsed.IsValid)
            {
                return new PolicyLoadResult(
                    null,
                    string.Empty,
                    $"Invalid policy: {string.Join(" ", parsed.Errors)}");
            }

            return new PolicyLoadResult(parsed.Policy, file.FullName, null);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PolicyLoadResult(null, string.Empty, "The policy file path is invalid.");
        }
    }

    private async Task WriteStatusAsync(
        RepositorySlug repository,
        IReadOnlyList<CachedPullRequestReadiness> results,
        bool readyOnly)
    {
        var heading = readyOnly ? "Ready pull requests" : "Open pull requests";
        await output.WriteLineAsync($"{heading} for {repository}: {results.Count}");
        foreach (var item in results)
        {
            await output.WriteLineAsync(
                $"{item.Evaluation.Status.ToString().ToUpperInvariant(),-7} " +
                $"#{item.Snapshot.Number} {item.Snapshot.Title}");
            await output.WriteLineAsync($"        Next: {item.Evaluation.NextAction}");
        }
    }

    private async Task WritePullRequestAsync(
        CachedPullRequestReadiness item,
        ReadinessDocument document)
    {
        await output.WriteLineAsync(
            $"{document.Status.ToUpperInvariant()} PR #{item.Snapshot.Number}: {item.Snapshot.Title}");
        await output.WriteLineAsync($"Repository: {document.Repository}");
        await output.WriteLineAsync($"Author: {item.Snapshot.Author}");
        await output.WriteLineAsync($"Branch: {item.Snapshot.HeadBranch} -> {item.Snapshot.BaseBranch}");
        await output.WriteLineAsync(
            $"Change: {item.Snapshot.ChangedFiles} files, +{item.Snapshot.Additions}/-{item.Snapshot.Deletions}");
        await output.WriteLineAsync($"Summary: {document.Summary}");
        await output.WriteLineAsync("Blockers:");
        if (document.Blockers.Count == 0)
        {
            await output.WriteLineAsync("- None");
        }
        else
        {
            foreach (var blocker in document.Blockers)
            {
                await output.WriteLineAsync($"- [{blocker.Impact}] {blocker.Summary}");
            }
        }

        await output.WriteLineAsync("Evidence:");
        foreach (var evidence in document.Evidence)
        {
            await output.WriteLineAsync(
                $"- [{evidence.Outcome}] {evidence.Label}: {evidence.Summary}");
        }

        await output.WriteLineAsync($"Next action: {document.NextAction}");
        await output.WriteLineAsync($"URL: {item.Snapshot.Url}");
    }

    private async Task WriteJsonAsync<T>(T value) =>
        await output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));

    private static RepositoryStatusDocument CreateStatusDocument(
        RepositorySlug repository,
        IReadOnlyList<CachedPullRequestReadiness> items) =>
        new(
            "1.0",
            repository.ToString(),
            items.Count,
            items.Select(item => ReadinessDocumentFactory.Create(
                item.Snapshot,
                item.Evaluation)).ToArray());

    private static bool TryResolveRepository(
        ParsedArguments parsed,
        bool needsPullRequestNumber,
        out RepositorySlug repository,
        out int pullRequestNumber,
        out string? errorMessage)
    {
        repository = new RepositorySlug("acme", "payments");
        pullRequestNumber = 0;
        errorMessage = null;
        var values = parsed.Positionals.Skip(1).ToArray();
        if (parsed.Demo)
        {
            if (!needsPullRequestNumber && values.Length == 0)
            {
                return true;
            }

            if (needsPullRequestNumber && values.Length == 1)
            {
                return TryParsePullRequestNumber(values[0], out pullRequestNumber, out errorMessage);
            }
        }

        var requiredLength = needsPullRequestNumber ? 2 : 1;
        if (values.Length != requiredLength ||
            !TryParseRepository(values[0], out repository, out errorMessage))
        {
            return false;
        }

        if (parsed.Demo && repository != new RepositorySlug("acme", "payments"))
        {
            errorMessage = "The demo repository is acme/payments.";
            return false;
        }

        return !needsPullRequestNumber ||
            TryParsePullRequestNumber(values[1], out pullRequestNumber, out errorMessage);
    }

    private static bool TryParseRepository(
        string value,
        out RepositorySlug repository,
        out string errorMessage)
    {
        repository = new RepositorySlug(string.Empty, string.Empty);
        var parts = value.Split('/');
        if (parts.Length != 2 ||
            !RepositoryInputValidator.TryValidateRepository(parts[0], parts[1], out var error))
        {
            errorMessage = parts.Length == 2
                ? error
                : "Repository must use OWNER/REPOSITORY format.";
            return false;
        }

        repository = new RepositorySlug(parts[0], parts[1]);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryParsePullRequestNumber(
        string value,
        out int number,
        out string? errorMessage)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
            number > 0)
        {
            errorMessage = null;
            return true;
        }

        errorMessage = "Pull request number must be a positive whole number.";
        return false;
    }

    private static bool TryCreateFilter(
        ParsedArguments parsed,
        out PullRequestFilter? filter,
        out string errorMessage)
    {
        filter = null;
        ReadinessStatus? status = null;
        if (parsed.Status is not null)
        {
            if (!TryParseNamedEnum(
                parsed.Status,
                "status",
                out ReadinessStatus parsedStatus,
                out errorMessage))
            {
                return false;
            }

            status = parsedStatus;
        }

        if (!TryParseNamedEnum(parsed.Ci, "CI filter", out PullRequestCiFilter ci, out errorMessage) ||
            !TryParseNamedEnum(parsed.Draft, "draft filter", out PullRequestDraftFilter draft, out errorMessage))
        {
            return false;
        }

        if (parsed.Author is not null && !RepositoryInputValidator.IsAuthor(parsed.Author))
        {
            errorMessage = "Author must be a valid GitHub login.";
            return false;
        }

        foreach (var value in new[]
        {
            parsed.Search, parsed.Label, parsed.Branch, parsed.Reviewer,
        })
        {
            if (value is { Length: > 100 } || value?.Any(char.IsControl) == true)
            {
                errorMessage = "Filter values must use 100 or fewer printable characters.";
                return false;
            }
        }

        filter = new PullRequestFilter(
            status,
            parsed.Search,
            parsed.Author,
            parsed.Label,
            parsed.Branch,
            parsed.Reviewer,
            ci,
            draft);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryParseNamedEnum<T>(
        string? value,
        string label,
        out T parsed,
        out string errorMessage)
        where T : struct, Enum
    {
        if (value is null)
        {
            parsed = default;
            errorMessage = string.Empty;
            return true;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
            Enum.TryParse<T>(value, ignoreCase: true, out parsed) &&
            Enum.IsDefined(parsed))
        {
            errorMessage = string.Empty;
            return true;
        }

        parsed = default;
        errorMessage = $"Invalid {label}.";
        return false;
    }

    private static int ExitCodeFor(IReadOnlyList<CachedPullRequestReadiness> items)
    {
        if (items.Any(item => item.Evaluation.Status == ReadinessStatus.Unknown))
        {
            return CliExitCodes.Unknown;
        }

        return items.Any(item => item.Evaluation.Status != ReadinessStatus.Go)
            ? CliExitCodes.PolicyBlocked
            : CliExitCodes.Success;
    }

    private static string ProviderError(PullRequestFetchStatus status) => status switch
    {
        PullRequestFetchStatus.RateLimited =>
            "GitHub rate-limited the refresh. Keep cached data and try again later.",
        PullRequestFetchStatus.AccessDenied =>
            "GitHub denied access. Check the repository name and token permissions.",
        _ => "GitHub could not provide current readiness evidence.",
    };

    private async Task<int> InvalidAsync(string message)
    {
        await error.WriteLineAsync(message);
        return CliExitCodes.InvalidInput;
    }

    private ILocalReadinessStore RequiredStore => store ??
        throw new InvalidOperationException("This command requires the local readiness store.");

    private const string HelpText = """
        Gatehouse reports deterministic GitHub change readiness.

        Usage:
          gatehouse repo add OWNER/REPOSITORY [--config PATH] [--json]
          gatehouse status OWNER/REPOSITORY [filters] [--json] [--cached]
          gatehouse ready OWNER/REPOSITORY [filters] [--json] [--cached]
          gatehouse pr OWNER/REPOSITORY NUMBER [--json] [--cached]
          gatehouse report OWNER/REPOSITORY NUMBER [--json] [--cached]
          gatehouse serve [--port 5341]
          gatehouse version [--json]

        Demo mode:
          gatehouse status --demo [--json]
          gatehouse pr NUMBER --demo [--json]
          gatehouse report NUMBER --demo [--json]

        Filters:
          --status STATUS   go, review, blocked, draft, or unknown
          --search TEXT     title or pull request number
          --author LOGIN    GitHub author
          --label LABEL     label text
          --branch BRANCH   head branch text
          --reviewer LOGIN  requested reviewer
          --ci STATE        all, passing, blocked, pending, or notrun
          --draft STATE     all, ready, or draft

        Global data option:
          --data PATH       SQLite file; GATEHOUSE_DATA_PATH is the fallback

        Exit codes:
          0 success, 2 policy blocker, 3 unknown evidence, 64 invalid input,
          69 provider failure, 70 internal failure, 130 cancelled

        Gatehouse is read-only for connected GitHub repositories. It never runs Git.
        """;

    private sealed record RepositoryLoadResult(LocalRepositoryDetail? Repository, int ExitCode);

    private sealed record PolicyLoadResult(
        RepositoryPolicy? Policy,
        string Source,
        string? Error);

    private sealed record RepositoryAddedDocument(
        string SchemaVersion,
        string Repository,
        Guid Id,
        string PolicySource);

    private sealed record RepositoryStatusDocument(
        string SchemaVersion,
        string Repository,
        int PullRequestCount,
        IReadOnlyList<ReadinessDocument> PullRequests);

    private sealed class ParsedArguments
    {
        private readonly HashSet<string> usedOptions = new(StringComparer.Ordinal);

        public List<string> Positionals { get; } = [];

        public bool Json { get; private set; }

        public bool Demo { get; private set; }

        public bool Cached { get; private set; }

        public bool Help { get; private set; }

        public string? ConfigPath { get; private set; }

        public string? Status { get; private set; }

        public string? Search { get; private set; }

        public string? Author { get; private set; }

        public string? Label { get; private set; }

        public string? Branch { get; private set; }

        public string? Reviewer { get; private set; }

        public string? Ci { get; private set; }

        public string? Draft { get; private set; }

        public int Port { get; private set; } = 5341;

        public string? Error { get; private set; }

        public bool HasOnly(params string[] allowed) =>
            usedOptions.IsSubsetOf(allowed);

        public static ParsedArguments Parse(IReadOnlyList<string> args)
        {
            ArgumentNullException.ThrowIfNull(args);
            var result = new ParsedArguments();
            for (var index = 0; index < args.Count; index++)
            {
                var argument = args[index];
                switch (argument)
                {
                    case "--json":
                        result.SetFlag(argument, () => result.Json = true);
                        break;
                    case "--demo":
                        result.SetFlag(argument, () => result.Demo = true);
                        break;
                    case "--cached":
                        result.SetFlag(argument, () => result.Cached = true);
                        break;
                    case "--help" or "-h":
                        result.Help = true;
                        break;
                    case "--config":
                        result.SetValue(argument, args, ref index, value => result.ConfigPath = value);
                        break;
                    case "--status":
                        result.SetValue(argument, args, ref index, value => result.Status = value);
                        break;
                    case "--search":
                        result.SetValue(argument, args, ref index, value => result.Search = value);
                        break;
                    case "--author":
                        result.SetValue(argument, args, ref index, value => result.Author = value);
                        break;
                    case "--label":
                        result.SetValue(argument, args, ref index, value => result.Label = value);
                        break;
                    case "--branch":
                        result.SetValue(argument, args, ref index, value => result.Branch = value);
                        break;
                    case "--reviewer":
                        result.SetValue(argument, args, ref index, value => result.Reviewer = value);
                        break;
                    case "--ci":
                        result.SetValue(argument, args, ref index, value => result.Ci = value);
                        break;
                    case "--draft":
                        result.SetValue(argument, args, ref index, value => result.Draft = value);
                        break;
                    case "--port":
                        result.SetValue(argument, args, ref index, value =>
                        {
                            if (!int.TryParse(
                                value,
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var port))
                            {
                                result.Error = "Port must be a positive whole number.";
                            }
                            else
                            {
                                result.Port = port;
                            }
                        });
                        break;
                    default:
                        if (argument.StartsWith('-'))
                        {
                            result.Error = "Unknown option. Run: gatehouse help";
                        }
                        else
                        {
                            result.Positionals.Add(argument);
                        }

                        break;
                }

                if (result.Error is not null)
                {
                    break;
                }
            }

            return result;
        }

        private void SetFlag(string option, Action setter)
        {
            if (!usedOptions.Add(option))
            {
                Error = $"Option {option} can be used only once.";
                return;
            }

            setter();
        }

        private void SetValue(
            string option,
            IReadOnlyList<string> args,
            ref int index,
            Action<string> setter)
        {
            if (!usedOptions.Add(option))
            {
                Error = $"Option {option} can be used only once.";
                return;
            }

            if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            {
                Error = $"Option {option} requires a value.";
                return;
            }

            setter(args[index]);
        }
    }
}
