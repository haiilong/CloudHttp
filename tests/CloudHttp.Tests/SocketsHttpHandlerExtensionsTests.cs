using System.Net;

namespace CloudHttp.Tests;

public class SocketsHttpHandlerExtensionsTests
{
    [Fact]
    public void ConfigureForCloud_sets_cloud_defaults()
    {
        var handler = new SocketsHttpHandler();

        handler.ConfigureForCloud();

        handler.PooledConnectionLifetime.Should().Be(TimeSpan.FromMinutes(2));
        handler.PooledConnectionIdleTimeout.Should().Be(TimeSpan.FromMinutes(1));
        handler.ConnectTimeout.Should().Be(TimeSpan.FromSeconds(5));
        handler.MaxConnectionsPerServer.Should().Be(100);
        handler.AutomaticDecompression.Should().Be(DecompressionMethods.All);
        handler.EnableMultipleHttp2Connections.Should().BeTrue();
        handler.InitialHttp2StreamWindowSize.Should().Be(128 * 1024);
        handler.KeepAlivePingDelay.Should().Be(TimeSpan.FromSeconds(30));
        handler.KeepAlivePingTimeout.Should().Be(TimeSpan.FromSeconds(10));
        handler.KeepAlivePingPolicy.Should().Be(HttpKeepAlivePingPolicy.WithActiveRequests);
        handler.ResponseDrainTimeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ConfigureForCloud_returns_same_instance_for_chaining()
    {
        var handler = new SocketsHttpHandler();

        var result = handler.ConfigureForCloud();

        result.Should().BeSameAs(handler);
    }

    [Fact]
    public void ConfigureForCloud_invokes_customize_after_defaults()
    {
        var handler = new SocketsHttpHandler();

        handler.ConfigureForCloud(h =>
        {
            h.MaxConnectionsPerServer = 12;
            h.ConnectTimeout = TimeSpan.FromSeconds(3);
        });

        handler.MaxConnectionsPerServer.Should().Be(12);
        handler.ConnectTimeout.Should().Be(TimeSpan.FromSeconds(3));
        // unchanged by customize, still cloud default
        handler.PooledConnectionLifetime.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void ConfigureForCloud_throws_for_null_handler()
    {
        SocketsHttpHandler? handler = null;

        var act = () => handler!.ConfigureForCloud();

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConfigureForCloud_allows_no_customize()
    {
        var handler = new SocketsHttpHandler();

        var act = () => handler.ConfigureForCloud(customize: null);

        act.Should().NotThrow();
    }
}
