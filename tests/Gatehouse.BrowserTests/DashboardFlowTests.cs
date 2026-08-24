using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Gatehouse.BrowserTests;

public sealed class DashboardFlowTests(GatehouseBrowserFixture fixture)
    : IClassFixture<GatehouseBrowserFixture>
{
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task Demo_flow_is_accessible_responsive_and_copyable(int width, int height)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
        });
        await using var context = await browser.NewContextAsync(new()
        {
            BaseURL = fixture.BaseAddress.ToString(),
            Permissions = ["clipboard-read", "clipboard-write"],
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });
        var page = await context.NewPageAsync();
        var browserErrors = new ConcurrentQueue<string>();
        page.Console += (_, message) =>
        {
            if (message.Type is "error" or "warning")
            {
                browserErrors.Enqueue($"console {message.Type}: {message.Text}");
            }
        };
        page.PageError += (_, error) => browserErrors.Enqueue($"page error: {error}");
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                browserErrors.Enqueue($"response {response.Status}: {response.Url}");
            }
        };

        await page.GotoAsync("/repositories");
        await WaitForInteractiveAsync(page, browserErrors, fixture.RecentOutput);
        await page.GetByRole(AriaRole.Link, new() { Name = "Open demo repository" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "acme/payments" }))
            .ToBeVisibleAsync();
        Assert.EndsWith("/overview", new Uri(page.Url).AbsolutePath, StringComparison.Ordinal);
        await Expect(page.GetByText("Open changes")).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "Pull requests" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Blocked by CI" }).ClickAsync();
        var rows = page.Locator(".readiness-table tbody tr");
        await Expect(rows).ToHaveCountAsync(1);
        await Expect(rows.First).ToContainTextAsync("#144");
        await Expect(rows.First).Not.ToContainTextAsync("#145");

        await rows.First.GetByRole(AriaRole.Link, new() { Name = "Review packet" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Fix dashboard route state" }))
            .ToBeVisibleAsync();
        await Expect(page.GetByText("Path-Aware QA", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator("pre.report-text")).ToContainTextAsync("NO-GO.");
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Run" }).Last).ToHaveAttributeAsync(
            "href",
            new Regex("^https://example\\.com/gatehouse-demo/"));

        await page.GetByRole(AriaRole.Button, new() { Name = "Copy report" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status).Last).ToContainTextAsync("Copied to clipboard.");
        var clipboard = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        Assert.StartsWith("NO-GO.", clipboard, StringComparison.Ordinal);
        Assert.Contains("Path-Aware QA", clipboard, StringComparison.Ordinal);

        Assert.True(await page.GetByRole(AriaRole.Heading).CountAsync() >= 4);
        Assert.True(await page.GetByRole(AriaRole.Link, new() { Name = "Evidence" }).CountAsync() >= 1);
        var hasHorizontalOverflow = await page.EvaluateAsync<bool>(
            "document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(hasHorizontalOverflow);

        await page.Keyboard.PressAsync("Tab");
        var focusOutline = await page.EvaluateAsync<string>(
            "getComputedStyle(document.activeElement).outlineStyle");
        Assert.NotEqual("none", focusOutline);
        Assert.Empty(browserErrors);
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);

    private static async Task WaitForInteractiveAsync(
        IPage page,
        ConcurrentQueue<string> browserErrors,
        Func<string> serverOutput)
    {
        try
        {
            await Expect(page.Locator(".app-shell")).ToHaveAttributeAsync(
                "data-interactive",
                "true",
                new() { Timeout = 10_000 });
        }
        catch (PlaywrightException exception)
        {
            var runtime = await page.EvaluateAsync<string>(
                "JSON.stringify({ readyState: document.readyState, " +
                "blazorType: typeof window.Blazor, " +
                "scripts: Array.from(document.scripts, script => script.src), " +
                "resources: performance.getEntriesByType('resource')" +
                ".map(resource => resource.name)" +
                ".filter(name => name.includes('_blazor') || name.includes('blazor.web')) })");
            throw new InvalidOperationException(
                $"The Blazor circuit did not become interactive. Runtime: {runtime}. " +
                $"Browser messages: {string.Join(" | ", browserErrors)}. " +
                $"Server output: {serverOutput()}",
                exception);
        }
    }
}

public sealed class GatehouseBrowserFixture : IAsyncLifetime, IDisposable
{
    private readonly ConcurrentQueue<string> output = new();
    private Process? process;
    private string? databasePath;

    public Uri BaseAddress { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var webProjectDirectory = Path.Combine(repositoryRoot, "src", "Gatehouse.Web");
        var port = GetAvailablePort();
        databasePath = Path.Combine(Path.GetTempPath(), $"gatehouse-browser-{Guid.NewGuid():N}.db");
        BaseAddress = new Uri($"http://127.0.0.1:{port}");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = webProjectDirectory,
        };
        startInfo.ArgumentList.Add(
            Path.Combine(webProjectDirectory, "bin", "Release", "net10.0", "Gatehouse.Web.dll"));

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["ReloadStaticAssetsAtRuntime"] = "false";
        startInfo.Environment["ConnectionStrings__Gatehouse"] = $"Data Source={databasePath}";
        startInfo.Environment["Gatehouse__Port"] = port.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment.Remove("GATEHOUSE_GITHUB_TOKEN");
        process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += CaptureOutput;
        process.ErrorDataReceived += CaptureOutput;
        if (!process.Start())
        {
            throw new InvalidOperationException("Gatehouse browser test host did not start.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var client = new HttpClient { BaseAddress = BaseAddress };
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Gatehouse browser test host exited early.{Environment.NewLine}{RecentOutput()}");
            }

            try
            {
                using var response = await client.GetAsync("/api/v1/health");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Kestrel is still starting.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Gatehouse browser test host was not ready.{Environment.NewLine}{RecentOutput()}");
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }

        process?.Dispose();
        process = null;
        if (databasePath is not null)
        {
            foreach (var candidate in new[]
            {
                databasePath,
                $"{databasePath}-shm",
                $"{databasePath}-wal",
            })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }

            databasePath = null;
        }

        GC.SuppressFinalize(this);
    }

    private void CaptureOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is null)
        {
            return;
        }

        output.Enqueue(args.Data);
        while (output.Count > 100)
        {
            output.TryDequeue(out _);
        }
    }

    public string RecentOutput() => string.Join(Environment.NewLine, output);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (var level = 0; level < 8 && directory is not null; level++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Gatehouse.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Gatehouse repository root was not found.");
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
