using System.Net;

namespace CloudHttp;

/// <summary>
/// Options controlling how a <see cref="DistributedHttpClient"/> spreads requests across its
/// underlying <see cref="HttpClient"/> instances. The class is intentionally mutable and
/// parameterless so it can be bound from <c>appsettings.json</c>.
/// </summary>
public sealed class ClientDistributionOptions
{
    /// <summary>Selection strategy. Defaults to <see cref="DistributionMode.RoundRobin"/>.</summary>
    public DistributionMode Mode { get; set; } = DistributionMode.RoundRobin;

    /// <summary>
    /// Number of underlying <see cref="HttpClient"/> instances (each with its own
    /// <see cref="SocketsHttpHandler"/> and connection pool). Defaults to <c>2</c>.
    /// </summary>
    public int ClientCount { get; set; } = 2;

    /// <summary>
    /// When <see langword="true"/> (default), a single transient failure causes the next request
    /// attempt to be sent through a different client. The library does not perform multi-attempt
    /// retry with backoff, compose with <c>Microsoft.Extensions.Http.Resilience</c> for that.
    /// </summary>
    public bool RotateOnTransientError { get; set; } = true;

    /// <summary>
    /// Per-client weights for <see cref="DistributionMode.Weighted"/>. Keys are client indices
    /// (<c>0</c>..<see cref="ClientCount"/>-1); values are relative weights. Required when
    /// <see cref="Mode"/> is <see cref="DistributionMode.Weighted"/>; ignored otherwise.
    /// </summary>
    public IReadOnlyDictionary<int, double>? ClientWeights { get; set; }

    /// <summary>
    /// How long a client stays out of rotation in <see cref="DistributionMode.HealthAware"/>
    /// after a transient failure. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan HealthDegradedTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// HTTP status codes that count as transient (rotation-worthy). Defaults to
    /// <c>408, 429, 500, 502, 503, 504</c>.
    /// </summary>
    public HashSet<HttpStatusCode> TransientStatusCodes { get; set; } =
    [
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
    ];

    /// <summary>
    /// Predicate for whether an exception is transient (rotation-worthy). Defaults to
    /// <see cref="HttpRequestException"/>, <see cref="IOException"/>, <see cref="TimeoutException"/>,
    /// and <see cref="TaskCanceledException"/> whose <see cref="Exception.InnerException"/> is
    /// <see cref="TimeoutException"/> (the shape thrown by <see cref="HttpClient.Timeout"/>).
    /// Caller cancellation is filtered out earlier and never reaches this predicate.
    /// </summary>
    public Func<Exception, bool> IsTransientException { get; set; } =
        static ex => ex is HttpRequestException
            or IOException
            or TimeoutException
            or TaskCanceledException { InnerException: TimeoutException };
}