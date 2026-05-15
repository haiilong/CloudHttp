using Microsoft.Extensions.DependencyInjection;

namespace CloudHttp;

/// <summary>
/// Extension methods for <see cref="IHttpClientBuilder"/> that apply cloud-ready
/// <see cref="HttpClient"/> and <see cref="SocketsHttpHandler"/> defaults.
/// </summary>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Configures the named or typed client represented by <paramref name="builder"/> with
    /// production-ready cloud defaults: a <see cref="SocketsHttpHandler"/> tuned by
    /// <see cref="SocketsHttpHandlerExtensions.ConfigureForCloud(SocketsHttpHandler, Action{SocketsHttpHandler}?)"/>,
    /// an infinite factory handler lifetime so connection pools are governed by
    /// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/>, and a 30-second
    /// <see cref="HttpClient.Timeout"/>.
    /// </summary>
    /// <param name="builder">The HTTP client builder to configure. Must not be <see langword="null"/>.</param>
    /// <param name="customizeHandler">
    /// Optional callback invoked after the handler defaults are applied so callers can override
    /// individual <see cref="SocketsHttpHandler"/> properties without losing the rest of the
    /// configuration.
    /// </param>
    /// <param name="customizeClient">
    /// Optional callback invoked after the client defaults are applied so callers can override
    /// client-level settings such as <see cref="HttpClient.Timeout"/>, headers, or
    /// <see cref="HttpClient.BaseAddress"/>.
    /// </param>
    /// <returns>The same <paramref name="builder"/> instance for chaining.</returns>
    public static IHttpClientBuilder ConfigureForCloud(
        this IHttpClientBuilder builder,
        Action<SocketsHttpHandler>? customizeHandler = null,
        Action<HttpClient>? customizeClient = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Cloud-tuned primary handler.
        builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler().ConfigureForCloud(customizeHandler));

        // Pin handler lifetime so the factory does not rotate handlers
        // (and discard pools) underneath PooledConnectionLifetime.
        builder.SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        // Client-level defaults: total timeout and caller customization.
        builder.ConfigureHttpClient(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);

            customizeClient?.Invoke(client);
        });

        return builder;
    }
}