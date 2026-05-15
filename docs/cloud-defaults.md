# `ConfigureForCloud()` reference

`SocketsHttpHandler.ConfigureForCloud()` applies transport defaults for dynamic hosting environments such as Kubernetes, ECS, Container Apps, and Docker Swarm. The callback runs after the defaults, so every setting here is easy to override.

`IHttpClientBuilder.ConfigureForCloud()` wraps the same handler defaults, sets `HttpClient.Timeout = 30s`, and pins the factory handler lifetime to `Timeout.InfiniteTimeSpan`. That last part matters: `IHttpClientFactory` normally rotates handlers on its own schedule, while CloudHttp wants `SocketsHttpHandler.PooledConnectionLifetime` to control connection recycling.

These defaults are tuned for cluster-local service calls. They are deliberately not "best for every HTTP call on earth." If you call a slow public API, a legacy service, or a network path with high latency, adjust the values.

## Quick use

For a normal named client:

```csharp
services.AddHttpClient("orders")
    .ConfigureForCloud(
        customizeClient: client =>
        {
            client.BaseAddress = new Uri("https://orders.svc.cluster.local");
        });
```

For a typed client:

```csharp
services.AddHttpClient<IOrdersClient, OrdersClient>()
    .ConfigureForCloud(
        customizeClient: client =>
        {
            client.BaseAddress = new Uri("https://orders.svc.cluster.local");
        });
```

For a distributed client, use `configurePrimaryHandler` to customize the handler on every underlying named client. Do not call `ConfigureForCloud()` yourself here. `AddDistributedHttpClient` already creates each handler with CloudHttp defaults, then runs this callback:

```csharp
services.AddDistributedHttpClient(
    "orders",
    opts => opts.ClientCount = 4,
    configureClient: client => client.BaseAddress = new Uri("https://orders.svc.cluster.local"),
    configurePrimaryHandler: handler =>
    {
        handler.ConnectTimeout = TimeSpan.FromSeconds(3);
        handler.MaxConnectionsPerServer = 200;
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(1);
    });
```

## DNS / connection lifecycle

| Setting | Value | Why |
|---|---|---|
| `PooledConnectionLifetime` | `2 min` | Forces TCP connections to recycle every 2 min so DNS changes (rolling pod restarts, scale-up, scale-down) propagate. Default in .NET is `infinite`, which means a connection opened on day 1 still goes to the same pod on day 30. |
| `PooledConnectionIdleTimeout` | default (`1 min`) | CloudHttp leaves this at the BCL default. Idle connections already drain quickly enough for the intended use case. |
| `ConnectTimeout` | `5 s` | Bounds TCP/TLS connection setup. The .NET default is `infinite`, which is too loose for service-to-service traffic. |

`PooledConnectionLifetime` is the important setting for pod churn. It does not close active requests. It marks a connection as too old to be reused after the current work finishes.

## Capacity

| Setting | Value | Why |
|---|---|---|
| `MaxConnectionsPerServer` | `100` | Keeps a practical cap on concurrent connections per origin while leaving enough room for bursty service traffic. |

If your service does thousands of concurrent calls to the same origin, measure before raising this. More connections can help throughput, but they also increase file descriptors, TLS work, server pressure, and retry blast radius.

## HTTP/2

| Setting | Value | Why |
|---|---|---|
| `EnableMultipleHttp2Connections` | `true` | Lets the handler open another HTTP/2 connection when the existing one is saturated by stream limits. |
| `InitialHttp2StreamWindowSize` | `128 KiB` | Doubles the default per-stream flow-control window. This helps larger JSON or streaming responses without making the window huge. |

## Keep-alive

| Setting | Value | Why |
|---|---|---|
| `KeepAlivePingDelay` | `30 s` | Send a keep-alive ping after 30 s of activity to detect dead connections (NLB silently drops idle TCP, half-closed K8s endpoints, etc.). |
| `KeepAlivePingTimeout` | `10 s` | If no pong arrives within 10 s, close the connection. |
| `KeepAlivePingPolicy` | `WithActiveRequests` | Only ping while requests are in flight - don't ping idle connections (idle ones will be dropped by `PooledConnectionIdleTimeout` anyway). |

The ping settings are there to find dead connections while a request is waiting. They are not a background heartbeat for idle pools.

## Body handling

| Setting | Value | Why |
|---|---|---|
| `AutomaticDecompression` | `DecompressionMethods.All` | Accept gzip, deflate, brotli (and zstd on .NET 10+). Adds `Accept-Encoding` automatically. No reason to opt out of this in a microservice. |
| `ResponseDrainTimeout` | `5 s` | Bound the time spent draining an unread response body when a request is disposed. The default is 100 s. 5 s is plenty for a sane upstream. |

## `IHttpClientBuilder.ConfigureForCloud()`

The builder extension applies the handler defaults above and then configures the client:

| Setting | Value | Why |
|---|---|---|
| `HttpClient.Timeout` | `30 s` | Gives callers a bounded total request time even before they add a resilience pipeline. |
| Handler lifetime | `Timeout.InfiniteTimeSpan` | Prevents `IHttpClientFactory` from rotating the whole handler pool underneath `PooledConnectionLifetime`. |

Use this when you want a normal named or typed client with CloudHttp's handler profile:

```csharp
services.AddHttpClient("orders")
    .ConfigureForCloud(
        customizeHandler: h => h.MaxConnectionsPerServer = 200,
        customizeClient: c => c.BaseAddress = new Uri("https://orders.svc"));
```

## Knobs not set

Some properties the user might expect to see are *intentionally absent*:

- `MaxResponseHeadersLength` - the BCL default (64 KiB) is already fine. Setting it to 64 explicitly was misleading "fluff" in the original draft.
- `SslOptions` - defaults to OS trust store, which is what you want for K8s service-to-service.
- `UseProxy` - left at the default (`true`). In K8s, proxies are environment-driven; the handler should honour `HTTP_PROXY`/`NO_PROXY` for cluster-egress traffic.

## How to tune by environment

For fast in-cluster services, the defaults are a good starting point.

For public internet APIs:

- Increase `ConnectTimeout` if the network path is slow or crosses regions.
- Consider a longer `PooledConnectionLifetime` if DNS changes rarely and TLS setup is expensive.
- Keep a caller-side total deadline with `CancellationTokenSource.CancelAfter`.

For high-throughput internal APIs:

- Watch server stream limits and connection counts.
- Raise `MaxConnectionsPerServer` only after load testing.
- Prefer `Microsoft.Extensions.Http.Resilience` for backoff and circuit breaking instead of hand-written retry loops.

For very sensitive write paths:

- Do not rely on `HttpClient.Timeout` as your business deadline.
- Use caller cancellation and idempotency keys.
- Keep retry policy explicit and close to the operation.

## Customizing

Everything is overridable. The `customize` callback runs *after* the defaults so callers can tweak any single property:

```csharp
new SocketsHttpHandler().ConfigureForCloud(h =>
{
    h.MaxConnectionsPerServer = 200;            // override
    h.ConnectTimeout = TimeSpan.FromSeconds(3); // override
});
```

When used inside `AddDistributedHttpClient(..., configurePrimaryHandler: customize)`, the same `customize` callback is applied to each of the N underlying handler instances.
