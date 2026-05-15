using System.Net;

namespace CloudHttp;

/// <summary>
/// Extension methods for <see cref="SocketsHttpHandler"/> tuned for cloud
/// and dynamic-hosting environments (Kubernetes, ECS, Container Apps, ...).
/// </summary>
public static class SocketsHttpHandlerExtensions
{
    /// <summary>
    /// Applies a set of production-ready defaults to <paramref name="handler"/> aimed at
    /// dynamically-scheduled containerised workloads: short pooled-connection lifetime so
    /// DNS changes (rolling pod restarts) are picked up quickly, HTTP/2 multiplexing across
    /// multiple connections, decompression for all encodings, and keep-alive pings that only
    /// fire while requests are in flight.
    /// </summary>
    /// <param name="handler">The handler to configure. Must not be <see langword="null"/>.</param>
    /// <param name="customize">
    /// Optional callback invoked after the defaults are applied so callers can override any
    /// individual property without losing the rest of the configuration.
    /// </param>
    /// <returns>The same <paramref name="handler"/> instance for chaining.</returns>
    public static SocketsHttpHandler ConfigureForCloud(
        this SocketsHttpHandler handler,
        Action<SocketsHttpHandler>? customize = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
        // handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1); -- This is the same as default
        handler.ConnectTimeout = TimeSpan.FromSeconds(5);
        handler.MaxConnectionsPerServer = 100;
        handler.AutomaticDecompression = DecompressionMethods.All;
        handler.EnableMultipleHttp2Connections = true;
        handler.InitialHttp2StreamWindowSize = 128 * 1024;
        handler.KeepAlivePingDelay = TimeSpan.FromSeconds(30);
        handler.KeepAlivePingTimeout = TimeSpan.FromSeconds(10);
        handler.KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests;
        handler.ResponseDrainTimeout = TimeSpan.FromSeconds(5);

        customize?.Invoke(handler);
        return handler;
    }
}