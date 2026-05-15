# CloudHttp

Cloud-friendly `HttpClient` extensions for .NET services.

CloudHttp is for service-to-service HTTP inside Kubernetes, ECS, Container Apps, and similar cluster networks. It helps long-lived .NET clients avoid getting stuck on one upstream connection pool by registering several independently pooled clients and selecting between them. It also includes cloud-tuned `SocketsHttpHandler` defaults, JSON fallback helpers, and small route/query utilities.

[![NuGet](https://img.shields.io/nuget/v/haiilong.http.extensions.svg)](https://www.nuget.org/packages/haiilong.http.extensions/)
[![Build](https://github.com/haiilong/CloudHttp/actions/workflows/ci.yml/badge.svg)](https://github.com/haiilong/CloudHttp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## Why this exists

Modern .NET keeps `HttpClient` connections long-lived. With HTTP/2, many requests can ride one TCP connection. That is good for latency, but it can also mean one client keeps talking to the same upstream endpoint while the Kubernetes Service behind it has several replicas. DNS is not consulted for every request. Connections stay open until the handler decides to recycle them. Background: [Load-balancing long-lived connections in Kubernetes](https://haiilong.github.io/blog/load-balancing-long-lived-connections-in-kubernetes).

CloudHttp's workaround is simple: register N named `HttpClient` instances, each with its own `SocketsHttpHandler`, then select between them. Each handler has its own connection pool and DNS refresh lifetime. That does not guarantee one pool per pod, but it gives the load balancer more chances than a single long-lived pool.

Use this library when:

- Your service calls another service through a cluster DNS name such as `https://payments.svc.cluster.local`.
- You want several independent outbound connection pools for one logical upstream.
- You want `SocketsHttpHandler` defaults that make sense for rolling deployments and short-lived pods.
- You plan to use `Microsoft.Extensions.Http.Resilience` for retry, timeout, and circuit breaker policy.

Skip it when you only call public internet APIs, when one connection pool is enough, or when your upstream already handles client-side balancing for you.

It also covers the small ergonomics you reach for every time you talk to JSON over HTTP:

- `SocketsHttpHandler.ConfigureForCloud()` - production defaults for K8s/ECS/Container Apps: short pooled-connection lifetime, bounded connect timeout, decompression, HTTP/2 multiple connections, and keep-alive pings while requests are active.
- `IHttpClientBuilder.ConfigureForCloud()` - the same handler defaults plus `HttpClient.Timeout = 30s` and infinite factory handler lifetime, so `PooledConnectionLifetime` controls recycling.
- `DistributedHttpClient` - keyed singleton wrapper over N independently pooled named clients, with round-robin, weighted, or health-aware selection.
- `HttpRouteBuilder.BuildPath` / `Uri.AddQuery` - minimal route template substitution and query merging.
- `HttpClient.*WithErrorHandlingAsync` extensions - "return this fallback on downstream failure" helpers with structured logging.

What CloudHttp does not do: multi-retry, exponential backoff, jitter, or circuit breaking. Use [`Microsoft.Extensions.Http.Resilience`](https://learn.microsoft.com/dotnet/core/resilience/http-resilience) for that. CloudHttp composes with it through `configureBuilder`, which runs once for each underlying named client.

## Comparison

| Capability | CloudHttp | Vanilla `IHttpClientFactory` |
|---|---|---|
| Multiple independent pools for one logical upstream | Yes, N named clients with N primary handlers | Not by default |
| Best-effort spreading across replicas | Round-robin, weighted, or health-aware selection | Manual |
| Cloud-tuned handler defaults | `ConfigureForCloud()` | Manual |
| Retry and circuit breaker | Compose with `Microsoft.Extensions.Http.Resilience` | Compose with `Microsoft.Extensions.Http.Resilience` |
| AOT / trim honesty | JSON helpers are annotated | Depends on your code |

## Install

```sh
dotnet add package haiilong.http.extensions
```

Targets `net8.0` and `net10.0` (if you use `net9.0`, the `net8.0` build is picked).

## Quickstart

Install CloudHttp:

```sh
dotnet add package haiilong.http.extensions
```

For retry, timeout, and circuit breaker policy, also install Microsoft's resilience package:

```sh
dotnet add package Microsoft.Extensions.Http.Resilience
```

Register a distributed client:

```csharp
using CloudHttp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDistributedHttpClient(
    name: "inventory",
    configureOptions: opts =>
    {
        opts.Mode = DistributionMode.RoundRobin;
        opts.ClientCount = 4; // Four named clients: inventory#0..inventory#3
    },
    configureClient: c =>
    {
        c.BaseAddress = new Uri("https://inventory.svc.cluster.local");
        c.DefaultRequestHeaders.Accept.Add(new("application/json"));
    },
    configureBuilder: clientBuilder =>
    {
        // Polly v8 retry + circuit breaker on each underlying client.
        clientBuilder.AddStandardResilienceHandler();
    });

var app = builder.Build();
app.Run();
```

To change the cloud handler defaults for every underlying client, use
`configurePrimaryHandler`. `AddDistributedHttpClient` already calls
`ConfigureForCloud()` internally, then runs your callback:

```csharp
builder.Services.AddDistributedHttpClient(
    name: "inventory",
    configureOptions: opts => opts.ClientCount = 4,
    configureClient: client =>
    {
        client.BaseAddress = new Uri("https://inventory.svc.cluster.local");
    },
    configurePrimaryHandler: handler =>
    {
        handler.ConnectTimeout = TimeSpan.FromSeconds(3);
        handler.MaxConnectionsPerServer = 200;
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(1);
    });
```

Inject and call:

```csharp
public sealed class InventoryService(
    [FromKeyedServices("inventory")] DistributedHttpClient http)
{
    public Task<StockLevel?> GetStockAsync(string sku, CancellationToken ct)
    {
        var path = HttpRouteBuilder.BuildPath(
            "/stock/{sku}",
            new Dictionary<string, object?> { ["sku"] = sku });

        return http.GetAsync<StockLevel>(path, ct);
    }
}
```

`GetAsync` can rotate once on a transient status or transient exception. The mutating JSON helpers (`PostAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync`) do not auto-rotate. That is intentional. A timed-out `POST` may already have created the row or charged the card.

If a write operation is safe to replay, make that part of your API contract and use `SendAsync` explicitly:

```csharp
public Task<HttpResponseMessage> ChargeAsync(ChargeRequest body, string idempotencyKey, CancellationToken ct)
{
    return http.SendAsync((client, token) =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/charges")
        {
            Content = JsonContent.Create(body)
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request, token);
    }, ct);
}
```

## Configuration

You can configure inline:

```csharp
services.AddDistributedHttpClient(
    "inventory",
    opts =>
    {
        opts.Mode = DistributionMode.HealthAware;
        opts.ClientCount = 4;
        opts.HealthDegradedTimeout = TimeSpan.FromSeconds(20);
        opts.TransientStatusCodes.Add(HttpStatusCode.RequestTimeout);
    },
    configureClient: c => c.BaseAddress = new Uri("https://inventory.svc.cluster.local"));
```

Or bind from configuration during registration:

```json
{
  "CloudHttp": {
    "Inventory": {
      "Mode": "HealthAware",
      "ClientCount": 4,
      "HealthDegradedTimeout": "00:00:20",
      "RotateOnTransientError": true
    }
  }
}
```

```csharp
services.AddDistributedHttpClient(
    "inventory",
    opts => configuration.GetSection("CloudHttp:Inventory").Bind(opts),
    configureClient: c => c.BaseAddress = new Uri("https://inventory.svc.cluster.local"));
```

`ClientCount` is fixed when `AddDistributedHttpClient` runs because the method creates named clients immediately. Later named options can change behavior such as transient status codes or health timeout, but they cannot add more configured named clients.

## Weighted distribution

Useful for canaries or mixed-capacity pools:

```csharp
services.AddWeightedDistribution(
    name: "search",
    weights: new Dictionary<int, double> { [0] = 9, [1] = 1 }, // ~10% to client #1
    configureClient: c => c.BaseAddress = new Uri("https://search.svc.cluster.local"));
```

## Health-aware distribution

A client that returns a transient status or throws a transient exception is taken out of rotation for the configured timeout. Recovery is timeout-based; a later success does not clear a newer degradation from another concurrent request.

```csharp
services.AddHealthAwareDistribution(
    name: "inventory",
    clientCount: 4,
    degradedTimeout: TimeSpan.FromSeconds(30),
    configureClient: c => c.BaseAddress = new Uri("https://inventory.svc.cluster.local"));
```

## ConfigureForCloud

Use on its own without distribution:

```csharp
services.AddHttpClient("orders", c => c.BaseAddress = new Uri("https://orders.svc"))
    .ConfigureForCloud();
```

What this sets: `PooledConnectionLifetime = 2 min`, `ConnectTimeout = 5 s`, `MaxConnectionsPerServer = 100`, `EnableMultipleHttp2Connections`, `AutomaticDecompression = All`, keep-alive pings while requests are in flight, and a short response-drain timeout. See [`docs/cloud-defaults.md`](docs/cloud-defaults.md) for the full list and reasoning.

## Route template + query helpers

```csharp
var path = HttpRouteBuilder.BuildPath("/api/v{ver}/users/{id}",
    new Dictionary<string, object?> { ["ver"] = 2, ["id"] = userId });

var uri = new Uri("https://api.example.com/search")
    .AddQuery(new[]
    {
        new KeyValuePair<string, string?>("q", searchTerm),
        new KeyValuePair<string, string?>("page", page.ToString()),
    });
```

## Error-handling verb helpers

These are not a general error strategy. They are for non-critical calls where a fallback value is genuinely acceptable:

```csharp
public async Task<FeatureFlags> GetFlagsAsync(HttpClient client, ILogger logger, CancellationToken ct)
{
    return await client.GetWithErrorHandlingAsync(
        "/flags",
        defaultResponse: FeatureFlags.Empty,
        logger: logger,
        errorLogLevel: LogLevel.Warning,
        ct: ct);
}
```

Caller cancellation is always propagated. Only HTTP failures, JSON errors, request timeouts and I/O errors fall through to the default value (and get logged).

## Composing with Microsoft.Extensions.Http.Resilience

The recommended setup. CloudHttp handles client-pool selection; Resilience handles retry, timeout, and circuit breaker policy. Attach the resilience handler per underlying client via `configureBuilder`:

```csharp
services.AddDistributedHttpClient("payments",
    configureOptions: opts => opts.ClientCount = 4,
    configureClient: c => c.BaseAddress = new Uri("https://payments.svc"),
    configureBuilder: cb => cb.AddStandardResilienceHandler(o =>
    {
        o.Retry.MaxRetryAttempts = 3;
        o.CircuitBreaker.FailureRatio = 0.2;
        o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
    }));
```

Each of the 4 underlying clients gets its own circuit breaker. For `GetAsync` and explicit `SendAsync(...)` calls, CloudHttp can rotate to the next client after a transient failure that survives the resilience pipeline. Mutating JSON helpers (`PostAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync`) do not auto-rotate, because replaying non-idempotent operations can duplicate side effects.

## Cluster network assumptions

The defaults are biased toward internal service calls:

- Short DNS and connection refresh windows help with rolling restarts and pod churn.
- Bounded connect timeout catches broken routes quickly.
- Keep-alive pings run only while requests are active.
- `MaxConnectionsPerServer = 100` is high enough for most service traffic without being unbounded.

If you call slow public APIs over the internet, tune these values. The defaults are not meant to be universal.

## AOT / trim

The library declares `IsAotCompatible=true`. JSON verb helpers carry `RequiresUnreferencedCode` / `RequiresDynamicCode` because they use reflection-based `System.Text.Json`. For AOT consumers, use `DistributedHttpClient.SendAsync(factory, ct)` and deserialize with your own `JsonTypeInfo<T>` pipeline.

## Documentation

- [`docs/client-distribution.md`](docs/client-distribution.md) - round-robin vs weighted vs health-aware tradeoffs.
- [`docs/cloud-defaults.md`](docs/cloud-defaults.md) - every `ConfigureForCloud()` setting and why.
- [`docs/with-resilience.md`](docs/with-resilience.md) - stacking with `Microsoft.Extensions.Http.Resilience`.

## License

MIT.
