using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace CloudHttp.Tests;

public class HttpClientBuilderExtensionsTests
{
    [Fact]
    public void ConfigureForCloud_sets_cloud_defaults()
    {
        var options = CreateOptions();
        using var client = CreateConfiguredClient(options);
        var handler = CreatePrimaryHandler(options);

        options.HandlerLifetime.Should().Be(Timeout.InfiniteTimeSpan);
        client.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        handler.PooledConnectionLifetime.Should().Be(TimeSpan.FromMinutes(2));
        handler.ConnectTimeout.Should().Be(TimeSpan.FromSeconds(5));
        handler.MaxConnectionsPerServer.Should().Be(100);
    }

    [Fact]
    public void ConfigureForCloud_returns_same_builder_for_chaining()
    {
        var builder = new ServiceCollection().AddHttpClient("cloud");

        var result = builder.ConfigureForCloud();

        result.Should().BeSameAs(builder);
    }

    [Fact]
    public void ConfigureForCloud_invokes_handler_customize_after_defaults()
    {
        var options = CreateOptions(builder => builder.ConfigureForCloud(handler =>
        {
            handler.MaxConnectionsPerServer = 12;
            handler.ConnectTimeout = TimeSpan.FromSeconds(3);
        }));

        var handler = CreatePrimaryHandler(options);

        handler.MaxConnectionsPerServer.Should().Be(12);
        handler.ConnectTimeout.Should().Be(TimeSpan.FromSeconds(3));
        // unchanged by customize, still cloud default
        handler.PooledConnectionLifetime.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void ConfigureForCloud_invokes_client_customize_after_defaults()
    {
        var options = CreateOptions(builder => builder.ConfigureForCloud(
            customizeClient: client =>
            {
                client.Timeout = TimeSpan.FromSeconds(7);
                client.BaseAddress = new Uri("https://api.example.test");
            }));

        using var client = CreateConfiguredClient(options);

        client.Timeout.Should().Be(TimeSpan.FromSeconds(7));
        client.BaseAddress.Should().Be(new Uri("https://api.example.test"));
    }

    [Fact]
    public void ConfigureForCloud_throws_for_null_builder()
    {
        IHttpClientBuilder? builder = null;

        var act = () => builder!.ConfigureForCloud();

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConfigureForCloud_allows_no_customize()
    {
        var builder = new ServiceCollection().AddHttpClient("cloud");

        var act = () => builder.ConfigureForCloud(
            customizeHandler: null,
            customizeClient: null);

        act.Should().NotThrow();
    }

    private static HttpClientFactoryOptions CreateOptions(Func<IHttpClientBuilder, IHttpClientBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("cloud");
        (configure ?? (httpClientBuilder => httpClientBuilder.ConfigureForCloud()))(builder);

        using var provider = services.BuildServiceProvider();
        return provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get("cloud");
    }

    private static HttpClient CreateConfiguredClient(HttpClientFactoryOptions options)
    {
        var client = new HttpClient();

        foreach (var action in options.HttpClientActions)
        {
            action(client);
        }

        return client;
    }

    private static SocketsHttpHandler CreatePrimaryHandler(HttpClientFactoryOptions options)
    {
        var builder = new TestHttpMessageHandlerBuilder
        {
            Name = "cloud",
        };

        foreach (var action in options.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        return builder.PrimaryHandler.Should().BeOfType<SocketsHttpHandler>().Subject;
    }

    private sealed class TestHttpMessageHandlerBuilder : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }

        public override HttpMessageHandler PrimaryHandler { get; set; } = new SocketsHttpHandler();

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];

        public override IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        public override HttpMessageHandler Build() => throw new NotSupportedException("The tests only apply builder actions.");
    }
}
