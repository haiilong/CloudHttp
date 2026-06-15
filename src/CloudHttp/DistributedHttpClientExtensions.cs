using CloudHttp.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHttp;

/// <summary>
/// <see cref="IServiceCollection"/> extensions to register a <see cref="DistributedHttpClient"/>
/// backed by N independently-pooled named <see cref="HttpClient"/> instances.
/// </summary>
public static class DistributedHttpClientExtensions
{
    /// <summary>
    /// Registers a <see cref="DistributedHttpClient"/> as a keyed singleton under
    /// <paramref name="name"/>, backed by <see cref="ClientDistributionOptions.ClientCount"/>
    /// distinct named <see cref="HttpClient"/> instances (<c>{name}#0</c>, <c>{name}#1</c>, ...),
    /// each with its own <see cref="SocketsHttpHandler"/> configured by
    /// <see cref="SocketsHttpHandlerExtensions.ConfigureForCloud"/>.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="name">
    /// Logical name used to resolve the <see cref="DistributedHttpClient"/> as a keyed service
    /// and as a prefix for the underlying named <see cref="HttpClient"/> registrations.
    /// </param>
    /// <param name="configureOptions">
    /// Configures distribution mode, client count, weights, etc. Runs once at registration
    /// time to size the underlying named-client loop; if you later bind options via
    /// <c>services.Configure&lt;ClientDistributionOptions&gt;(name, ...)</c> with a different
    /// <see cref="ClientDistributionOptions.ClientCount"/>, the bound count will be ignored -
    /// the count must be fixed here.
    /// </param>
    /// <param name="configureClient">
    /// Optional callback applied to <em>each</em> underlying <see cref="HttpClient"/> (e.g. set
    /// <see cref="HttpClient.BaseAddress"/>, default headers).
    /// </param>
    /// <param name="configureBuilder">
    /// Optional callback receiving each underlying client's <see cref="IHttpClientBuilder"/> so
    /// callers can stack message handlers like <c>AddStandardResilienceHandler</c>.
    /// </param>
    /// <param name="configurePrimaryHandler">
    /// Optional callback applied to each underlying <see cref="SocketsHttpHandler"/> after the
    /// cloud defaults are set (forwarded to <see cref="SocketsHttpHandlerExtensions.ConfigureForCloud"/>).
    /// </param>
    public static IServiceCollection AddDistributedHttpClient(
        this IServiceCollection services,
        string name,
        Action<ClientDistributionOptions>? configureOptions = null,
        Action<HttpClient>? configureClient = null,
        Action<IHttpClientBuilder>? configureBuilder = null,
        Action<SocketsHttpHandler>? configurePrimaryHandler = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var probe = new ClientDistributionOptions();
        configureOptions?.Invoke(probe);
        if (probe.ClientCount < 1)
            throw new ArgumentException("ClientCount must be at least 1.", nameof(configureOptions));
        if (probe.Mode == DistributionMode.Weighted && (probe.ClientWeights is null || probe.ClientWeights.Count == 0))
            throw new ArgumentException("ClientWeights is required for Weighted distribution.", nameof(configureOptions));

        var registeredClientCount = probe.ClientCount;

        if (configureOptions is not null)
        {
            services.Configure(name, configureOptions);
        }

        for (var i = 0; i < registeredClientCount; i++)
        {
            var subName = $"{name}#{i}";
            var builder = services.AddHttpClient(subName, c =>
                {
                    configureClient?.Invoke(c);
                })
                .ConfigurePrimaryHttpMessageHandler(() =>
                    new SocketsHttpHandler().ConfigureForCloud(configurePrimaryHandler))
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            configureBuilder?.Invoke(builder);
        }

        services.AddKeyedSingleton<DistributedHttpClient>(name, (sp, key) =>
        {
            var resolvedName = (string)key!;
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var opts = sp.GetRequiredService<IOptionsMonitor<ClientDistributionOptions>>().Get(resolvedName);

            var clients = new HttpClient[registeredClientCount];
            for (var i = 0; i < registeredClientCount; i++)
            {
                clients[i] = factory.CreateClient($"{resolvedName}#{i}");
            }

            IClientSelector selector = opts.Mode switch
            {
                DistributionMode.Weighted => new WeightedSelector(
                    registeredClientCount,
                    opts.ClientWeights ?? throw new InvalidOperationException("ClientWeights required for Weighted mode.")),
                DistributionMode.HealthAware => new HealthAwareSelector(registeredClientCount, opts.HealthDegradedTimeout),
                _ => new RoundRobinSelector(registeredClientCount),
            };

            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<DistributedHttpClient>();
            return new DistributedHttpClient(clients, selector, opts, logger);
        });

        return services;
    }

    /// <summary>Convenience for round-robin distribution.</summary>
    public static IServiceCollection AddRoundRobinDistribution(
        this IServiceCollection services,
        string name,
        int clientCount = 2,
        Action<HttpClient>? configureClient = null,
        Action<IHttpClientBuilder>? configureBuilder = null,
        Action<SocketsHttpHandler>? configurePrimaryHandler = null)
        => services.AddDistributedHttpClient(
            name,
            opts => { opts.Mode = DistributionMode.RoundRobin; opts.ClientCount = clientCount; },
            configureClient,
            configureBuilder,
            configurePrimaryHandler);

    /// <summary>Convenience for weighted distribution.</summary>
    public static IServiceCollection AddWeightedDistribution(
        this IServiceCollection services,
        string name,
        IReadOnlyDictionary<int, double> weights,
        Action<HttpClient>? configureClient = null,
        Action<IHttpClientBuilder>? configureBuilder = null,
        Action<SocketsHttpHandler>? configurePrimaryHandler = null)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count == 0) throw new ArgumentException("At least one weight required.", nameof(weights));
        var maxIdx = weights.Keys.Max();
        return services.AddDistributedHttpClient(
            name,
            opts =>
            {
                opts.Mode = DistributionMode.Weighted;
                opts.ClientWeights = weights;
                opts.ClientCount = Math.Max(opts.ClientCount, maxIdx + 1);
            },
            configureClient,
            configureBuilder,
            configurePrimaryHandler);
    }

    /// <summary>Convenience for health-aware distribution.</summary>
    public static IServiceCollection AddHealthAwareDistribution(
        this IServiceCollection services,
        string name,
        int clientCount = 2,
        TimeSpan? degradedTimeout = null,
        Action<HttpClient>? configureClient = null,
        Action<IHttpClientBuilder>? configureBuilder = null,
        Action<SocketsHttpHandler>? configurePrimaryHandler = null)
        => services.AddDistributedHttpClient(
            name,
            opts =>
            {
                opts.Mode = DistributionMode.HealthAware;
                opts.ClientCount = clientCount;
                if (degradedTimeout.HasValue) opts.HealthDegradedTimeout = degradedTimeout.Value;
            },
            configureClient,
            configureBuilder,
            configurePrimaryHandler);
}
