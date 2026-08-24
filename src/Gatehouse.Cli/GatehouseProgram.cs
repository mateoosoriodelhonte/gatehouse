using System.Diagnostics.CodeAnalysis;
using Gatehouse.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Gatehouse.Cli;

public static class GatehouseProgram
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The process boundary must return a stable code without exposing secrets.")]
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var bootstrap = CliBootstrapOptions.Parse(args);
        if (bootstrap.Error is not null)
        {
            await Console.Error.WriteLineAsync(bootstrap.Error);
            return CliExitCodes.InvalidInput;
        }

        try
        {
            await using var runtime = CliApplication.NeedsStore(bootstrap.Arguments)
                ? await CliRuntime.CreateAsync(bootstrap.DataPath, cancellationToken)
                : null;
            var application = new CliApplication(
                runtime?.Store,
                Console.Out,
                Console.Error,
                (port, token) => ServeAsync(bootstrap.DataPath, port, token));
            return await application.RunAsync(bootstrap.Arguments, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("Gatehouse was cancelled.");
            return CliExitCodes.Cancelled;
        }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync("Gatehouse could not start safely.");
            return CliExitCodes.InternalFailure;
        }
    }

    private static async Task<int> ServeAsync(
        string dataPath,
        int port,
        CancellationToken cancellationToken)
    {
        var options = new WebApplicationOptions
        {
            ApplicationName = typeof(GatehouseProgram).Assembly.GetName().Name,
            EnvironmentName = "Production",
        };
        await using var app = await GatehouseHost.BuildAsync(
            options,
            builder => builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Gatehouse"] = CliRuntime.ConnectionString(dataPath),
                    ["Gatehouse:Port"] = port.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                }),
            configureServices: null,
            cancellationToken);
        await app.StartAsync(cancellationToken);
        await app.WaitForShutdownAsync(cancellationToken);
        return CliExitCodes.Success;
    }
}

internal sealed record CliBootstrapOptions(
    string DataPath,
    string[] Arguments,
    string? Error)
{
    public static CliBootstrapOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? suppliedPath = null;
        var remaining = new List<string>(args.Count);
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], "--data", StringComparison.Ordinal))
            {
                remaining.Add(args[index]);
                continue;
            }

            if (suppliedPath is not null)
            {
                return Invalid("--data can be used only once.");
            }

            if (++index >= args.Count ||
                string.IsNullOrWhiteSpace(args[index]) ||
                args[index].StartsWith('-'))
            {
                return Invalid("--data requires a database file path.");
            }

            suppliedPath = args[index];
        }

        var configuredPath = suppliedPath ??
            Environment.GetEnvironmentVariable("GATEHOUSE_DATA_PATH");
        try
        {
            var dataPath = string.IsNullOrWhiteSpace(configuredPath)
                ? CliRuntime.DefaultDataPath()
                : Path.GetFullPath(configuredPath);
            return new CliBootstrapOptions(dataPath, [.. remaining], null);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid("The Gatehouse data path is invalid.");
        }

        CliBootstrapOptions Invalid(string error) => new(string.Empty, [], error);
    }
}
