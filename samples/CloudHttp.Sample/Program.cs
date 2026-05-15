using System.Collections.Concurrent;
using CloudHttp;

var mode = args.FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("CLOUDHTTP_SAMPLE_MODE")
    ?? "client";

if (mode.Equals("server", StringComparison.OrdinalIgnoreCase))
{
    await RunServerAsync(args);
    return;
}

await RunClientAsync(args);
return;

static async Task RunServerAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });

    var app = builder.Build();
    var instance = Environment.GetEnvironmentVariable("INSTANCE_NAME")
        ?? Environment.GetEnvironmentVariable("HOSTNAME")
        ?? Environment.MachineName;

    var failEvery = ReadInt("FAIL_EVERY", 0);
    var requestCount = 0;

    app.MapGet("/whoami", () =>
    {
        var count = Interlocked.Increment(ref requestCount);
        app.Logger.LogInformation("200 /whoami from {Instance}, request #{RequestCount}", instance, count);

        return Results.Ok(new BackendReply(
            Instance: instance,
            RequestCount: count,
            Status: "ok",
            At: DateTimeOffset.UtcNow));
    });

    app.MapGet("/unstable", () =>
    {
        var count = Interlocked.Increment(ref requestCount);
        if (failEvery > 0 && count % failEvery == 0)
        {
            app.Logger.LogWarning("503 /unstable from {Instance}, request #{RequestCount}", instance, count);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        app.Logger.LogInformation("200 /unstable from {Instance}, request #{RequestCount}", instance, count);

        return Results.Ok(new BackendReply(
            Instance: instance,
            RequestCount: count,
            Status: "ok",
            At: DateTimeOffset.UtcNow));
    });

    app.MapGet("/", () => Results.Redirect("/whoami"));

    await app.RunAsync();
}

static async Task RunClientAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });

    var upstreamUrl = ReadString("UPSTREAM_URL", "http://localhost:8080");
    var clientCount = ReadInt("CLIENT_COUNT", 8);
    var requestCount = ReadInt("REQUESTS", 48);
    var delayMs = ReadInt("DELAY_MS", 100);
    var endpoint = ReadString("ENDPOINT", "/whoami");
    var mode = Enum.TryParse<DistributionMode>(
        ReadString("DISTRIBUTION_MODE", "RoundRobin"),
        ignoreCase: true,
        out var parsedMode)
        ? parsedMode
        : DistributionMode.RoundRobin;

    builder.Services.AddDistributedHttpClient(
        name: "upstream",
        configureOptions: options =>
        {
            options.Mode = mode;
            options.ClientCount = clientCount;
            options.HealthDegradedTimeout = TimeSpan.FromSeconds(10);
            if (mode == DistributionMode.Weighted)
            {
                options.ClientWeights = Enumerable.Range(0, clientCount)
                    .ToDictionary(
                        static index => index,
                        index => index == clientCount - 1 ? 4d : 1d);
            }
        },
        configureClient: client =>
        {
            client.BaseAddress = new Uri(upstreamUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        },
        configurePrimaryHandler: handler =>
        {
            // Make the demo responsive. The library defaults are better for production.
            handler.PooledConnectionLifetime = TimeSpan.FromSeconds(20);
            handler.ConnectTimeout = TimeSpan.FromSeconds(2);
        });

    builder.Services.AddHostedService(provider =>
        new DemoWorker(
            provider.GetRequiredKeyedService<DistributedHttpClient>("upstream"),
            provider.GetRequiredService<ILogger<DemoWorker>>(),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            new DemoSettings(
                UpstreamUrl: upstreamUrl,
                Endpoint: endpoint,
                RequestCount: requestCount,
                Delay: TimeSpan.FromMilliseconds(delayMs),
                Mode: mode,
                ClientCount: clientCount)));

    var app = builder.Build();
    await app.RunAsync();
}

static int ReadInt(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

static string ReadString(string name, string fallback) =>
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
        ? fallback
        : Environment.GetEnvironmentVariable(name)!;

internal sealed partial class DemoWorker(
    DistributedHttpClient http,
    ILogger<DemoWorker> logger,
    IHostApplicationLifetime lifetime,
    DemoSettings settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogDemoStart(
            logger,
            settings.RequestCount,
            settings.Endpoint,
            settings.UpstreamUrl,
            settings.Mode,
            settings.ClientCount,
            http.ClientCount);

        var counts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var failures = 0;

        for (var i = 1; i <= settings.RequestCount && !stoppingToken.IsCancellationRequested; i++)
        {
            try
            {
                var result = await http.GetAsync<BackendReply>(settings.Endpoint, stoppingToken);
                if (result is null)
                {
                    failures++;
                    LogNullResponse(logger, i);
                }
                else
                {
                    counts.AddOrUpdate(result.Instance, 1, static (_, current) => current + 1);
                    LogSuccess(logger, i, result.Instance, result.RequestCount, result.Status);
                }
            }
            catch (Exception ex)
            {
                failures++;
                LogFailure(logger, ex, i);
            }

            if (settings.Delay > TimeSpan.Zero)
            {
                await Task.Delay(settings.Delay, stoppingToken);
            }
        }

        logger.LogInformation("");
        logger.LogInformation("Summary");
        logger.LogInformation("-------");
        foreach (var (instance, count) in counts.OrderBy(static entry => entry.Key))
        {
            LogSummary(logger, instance, count);
        }

        LogFailureSummary(logger, failures);
        lifetime.StopApplication();
    }

    [LoggerMessage(
        LogLevel.Information,
        "Sending {RequestCount} requests to {Endpoint} at {UpstreamUrl}. Mode={Mode}, configured clients={ConfiguredClientCount}, actual clients={ActualClientCount}.")]
    static partial void LogDemoStart(
        ILogger logger,
        int requestCount,
        string endpoint,
        string upstreamUrl,
        DistributionMode mode,
        int configuredClientCount,
        int actualClientCount);

    [LoggerMessage(LogLevel.Information, "[{Attempt}] {Instance} handled backend request #{BackendRequestCount} ({Status})")]
    static partial void LogSuccess(
        ILogger logger,
        int attempt,
        string instance,
        int backendRequestCount,
        string status);

    [LoggerMessage(LogLevel.Warning, "[{Attempt}] response body was empty")]
    static partial void LogNullResponse(ILogger logger, int attempt);

    [LoggerMessage(LogLevel.Warning, "[{Attempt}] request failed")]
    static partial void LogFailure(ILogger logger, Exception exception, int attempt);

    [LoggerMessage(LogLevel.Information, "{Instance}: {Count} responses")]
    static partial void LogSummary(ILogger logger, string instance, int count);

    [LoggerMessage(LogLevel.Information, "Failures observed by client: {Failures}")]
    static partial void LogFailureSummary(ILogger logger, int failures);
}

internal sealed record DemoSettings(
    string UpstreamUrl,
    string Endpoint,
    int RequestCount,
    TimeSpan Delay,
    DistributionMode Mode,
    int ClientCount);

internal sealed record BackendReply(
    string Instance,
    int RequestCount,
    string Status,
    DateTimeOffset At);