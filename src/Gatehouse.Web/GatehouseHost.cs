using System.Text.Json;
using System.Text.Json.Serialization;
using Gatehouse.Application;
using Gatehouse.Infrastructure.GitHub;
using Gatehouse.Infrastructure.Persistence;
using Gatehouse.Web.Ui;
using Gatehouse.Web.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gatehouse.Web;

public static class GatehouseHost
{
    public static Task<WebApplication> BuildAsync(
        string[] args,
        CancellationToken cancellationToken = default) =>
        BuildAsync(
            new WebApplicationOptions { Args = args },
            configureBuilder: null,
            configureServices: null,
            cancellationToken);

    public static async Task<WebApplication> BuildAsync(
        WebApplicationOptions options,
        Action<WebApplicationBuilder>? configureBuilder,
        Action<IServiceCollection>? configureServices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateBuilder(options);
        configureBuilder?.Invoke(builder);
        ConfigureLoopbackOnly(builder);

        builder.Services.AddProblemDetails();
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddDataProtection()
            .UseEphemeralDataProtectionProvider();

        var configuredConnectionString = builder.Configuration.GetConnectionString("Gatehouse");
        var connectionString = configuredConnectionString ?? DefaultConnectionString();
        EnsureDataDirectory(connectionString, secureDirectory: configuredConnectionString is null);
        builder.Services.AddPooledDbContextFactory<GatehouseDbContext>(database =>
            database.UseSqlite(connectionString));

        var localStoreOptions = new LocalStoreOptions
        {
            FreshnessMinutes = builder.Configuration.GetValue("Gatehouse:FreshnessMinutes", 15),
            RetentionDays = builder.Configuration.GetValue("Gatehouse:RetentionDays", 30),
            MaxSnapshotsPerPullRequest = builder.Configuration.GetValue(
                "Gatehouse:MaxSnapshotsPerPullRequest",
                50),
        };
        builder.Services.AddSingleton(localStoreOptions);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new GitHubClientOptions
        {
            Token = Environment.GetEnvironmentVariable("GATEHOUSE_GITHUB_TOKEN"),
        });
        builder.Services.AddHttpClient<IPullRequestSource, GitHubApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddSingleton<ILocalReadinessStore, LocalReadinessStore>();
        builder.Services.AddScoped<GatehouseUiService>();
        builder.Services.AddScoped<UiSessionState>();
        configureServices?.Invoke(builder.Services);
        var enableUi = builder.Configuration.GetValue("Gatehouse:EnableUi", true);
        if (enableUi)
        {
            StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
        }

        var app = builder.Build();
        await MigrateDatabaseAsync(app.Services, cancellationToken);
        SecureDatabaseFile(connectionString);

        if (!app.Environment.IsDevelopment())
        {
            app.UseWhen(
                context => context.Request.Path.StartsWithSegments("/api"),
                api => api.UseExceptionHandler(error => error.Run(async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsJsonAsync(new ProblemDetails
                    {
                        Title = "Gatehouse could not complete the request.",
                        Status = StatusCodes.Status500InternalServerError,
                    });
                })));
            app.UseWhen(
                context => !context.Request.Path.StartsWithSegments("/api"),
                pages => pages.UseExceptionHandler("/Error"));
        }
        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments("/api"),
            pages => pages.UseStatusCodePagesWithReExecute(
                "/not-found",
                createScopeForStatusCodePages: true));
        app.UseAntiforgery();
        app.MapGatehouseApi();
        if (enableUi)
        {
            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();
        }

        return app;
    }

    private static void ConfigureLoopbackOnly(WebApplicationBuilder builder)
    {
        var port = builder.Configuration.GetValue("Gatehouse:Port", 5341);
        if (port is < 1024 or > 65535)
        {
            throw new InvalidOperationException("Gatehouse:Port must be from 1024 to 65535.");
        }

        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = 64 * 1024;
            server.ListenLocalhost(port);
        });
    }

    private static async Task MigrateDatabaseAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<
            IDbContextFactory<GatehouseDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }

    private static string DefaultConnectionString()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(root, "Gatehouse", "gatehouse.db");
        return new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    private static void EnsureDataDirectory(string connectionString, bool secureDirectory)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:")
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            if (secureDirectory && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    private static void SecureDatabaseFile(string connectionString)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (!string.IsNullOrWhiteSpace(dataSource) &&
            dataSource != ":memory:" &&
            File.Exists(dataSource))
        {
            File.SetUnixFileMode(dataSource, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
