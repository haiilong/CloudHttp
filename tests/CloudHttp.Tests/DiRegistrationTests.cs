using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHttp.Tests;

public class DiRegistrationTests
{
    [Fact]
    public void AddDistributedHttpClient_creates_distinct_primary_handlers()
    {
        var captured = new ConcurrentBag<SocketsHttpHandler>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedHttpClient(
            name: "payments",
            configureOptions: opts => opts.ClientCount = 3,
            configurePrimaryHandler: h => captured.Add(h));

        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        // Force handler creation by requesting each named client.
        for (var i = 0; i < 3; i++)
        {
            var client = factory.CreateClient($"payments#{i}");
            client.Should().NotBeNull();
        }

        captured.Should().HaveCount(3);
        captured.Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void AddDistributedHttpClient_applies_configureClient_to_each_underlying_client()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedHttpClient(
            name: "payments",
            configureOptions: opts => opts.ClientCount = 3,
            configureClient: client =>
            {
                client.BaseAddress = new Uri("https://payments.example.test/");
                client.DefaultRequestHeaders.Add("x-client", "distributed");
            });

        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        for (var i = 0; i < 3; i++)
        {
            var client = factory.CreateClient($"payments#{i}");
            client.BaseAddress.Should().Be(new Uri("https://payments.example.test/"));
            client.DefaultRequestHeaders.GetValues("x-client").Should().ContainSingle("distributed");
        }
    }

    [Fact]
    public void AddDistributedHttpClient_invokes_configureBuilder_for_each_underlying_client()
    {
        var names = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddDistributedHttpClient(
            name: "payments",
            configureOptions: opts => opts.ClientCount = 3,
            configureBuilder: builder => names.Add(builder.Name));

        names.Should().BeEquivalentTo(["payments#0", "payments#1", "payments#2"]);
    }

    [Fact]
    public void AddDistributedHttpClient_applies_primary_handler_customization_after_cloud_defaults()
    {
        var captured = new ConcurrentBag<SocketsHttpHandler>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedHttpClient(
            name: "payments",
            configureOptions: opts => opts.ClientCount = 2,
            configurePrimaryHandler: handler =>
            {
                handler.MaxConnectionsPerServer = 12;
                captured.Add(handler);
            });

        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        _ = factory.CreateClient("payments#0");
        _ = factory.CreateClient("payments#1");

        captured.Should().HaveCount(2);
        captured.Should().OnlyContain(handler =>
            handler.MaxConnectionsPerServer == 12 &&
            handler.PooledConnectionLifetime == TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void DistributedHttpClient_resolves_as_keyed_singleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedHttpClient("orders", opts => opts.ClientCount = 2);

        using var sp = services.BuildServiceProvider();
        var first = sp.GetRequiredKeyedService<DistributedHttpClient>("orders");
        var second = sp.GetRequiredKeyedService<DistributedHttpClient>("orders");

        first.Should().BeSameAs(second);
        first.ClientCount.Should().Be(2);
    }

    [Fact]
    public void Multiple_named_distributors_coexist_independently()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedHttpClient("a", opts => opts.ClientCount = 2);
        services.AddDistributedHttpClient("b", opts => opts.ClientCount = 4);

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredKeyedService<DistributedHttpClient>("a").ClientCount.Should().Be(2);
        sp.GetRequiredKeyedService<DistributedHttpClient>("b").ClientCount.Should().Be(4);
    }

    [Fact]
    public void Later_named_options_cannot_change_registered_client_count()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedHttpClient("orders", opts => opts.ClientCount = 2);
        services.Configure<ClientDistributionOptions>("orders", opts => opts.ClientCount = 4);

        using var sp = services.BuildServiceProvider();

        sp.GetRequiredKeyedService<DistributedHttpClient>("orders").ClientCount.Should().Be(2);
    }

    [Fact]
    public void AddWeightedDistribution_sets_mode_and_count()
    {
        var weights = new Dictionary<int, double> { [0] = 1, [1] = 2, [2] = 3 };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWeightedDistribution("w", weights);

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredKeyedService<DistributedHttpClient>("w");
        client.ClientCount.Should().Be(3);
    }

    [Fact]
    public void AddHealthAwareDistribution_registers_correctly()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthAwareDistribution("h", clientCount: 5, degradedTimeout: TimeSpan.FromSeconds(15));

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredKeyedService<DistributedHttpClient>("h");
        client.ClientCount.Should().Be(5);
    }

    [Fact]
    public void Throws_when_weighted_mode_without_weights()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddDistributedHttpClient("x", opts =>
        {
            opts.Mode = DistributionMode.Weighted;
            opts.ClientCount = 2;
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Throws_on_zero_client_count()
    {
        var services = new ServiceCollection();

        var act = () => services.AddDistributedHttpClient("x", opts => opts.ClientCount = 0);

        act.Should().Throw<ArgumentException>();
    }
}
