using System.Net.Http.Headers;
using CloudHttp;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

builder.Services.AddDistributedHttpClient(
    name: "httpbin",
    configureOptions: opts =>
    {
        opts.Mode = DistributionMode.RoundRobin;
        opts.ClientCount = 4;
    },
    configureClient: c =>
    {
        c.BaseAddress = new Uri("https://httpbin.org");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    });

builder.Services.AddHostedService<DemoWorker>();

var app = builder.Build();
await app.RunAsync();

internal sealed partial class DemoWorker(
    [FromKeyedServices("httpbin")] DistributedHttpClient http,
    ILogger<DemoWorker> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogDistributedHttpClientCount(logger, http.ClientCount);

        for (var i = 0; i < 8 && !stoppingToken.IsCancellationRequested; i++)
        {
            try
            {
                var result = await http.GetAsync<UuidResponse>("/uuid", stoppingToken);
                if (result?.Uuid != null) LogAttemptAndUuid(logger, i, result.Uuid);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{Attempt}] failed", i);
            }
        }

        lifetime.StopApplication();
    }

    [LoggerMessage(LogLevel.Information, "DistributedHttpClient has {count} underlying clients.")]
    static partial void LogDistributedHttpClientCount(ILogger<DemoWorker> logger, int count);

    [LoggerMessage(LogLevel.Information, "[{attempt}] uuid = {uuid}")]
    static partial void LogAttemptAndUuid(ILogger<DemoWorker> logger, int attempt, string uuid);
}

internal sealed class UuidResponse
{
    public string? Uuid { get; set; }
}