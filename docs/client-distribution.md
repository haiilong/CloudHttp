# Client distribution modes

CloudHttp ships three strategies for choosing which underlying `HttpClient` handles the next request. Each underlying client has its own primary handler and connection pool. In Kubernetes that gives the load balancer more chances to land on different backing pods, but it is still best-effort. A Service, DNS, kube-proxy, sidecar, or cloud load balancer can still route two pools to the same replica.

Use distribution for pool selection. Use `Microsoft.Extensions.Http.Resilience` for retry, backoff, jitter, timeout, and circuit breaker policy.

## How registration works

This call:

```csharp
services.AddDistributedHttpClient(
    "payments",
    opts => opts.ClientCount = 4,
    configureClient: client => client.BaseAddress = new Uri("https://payments.svc.cluster.local"));
```

registers these named clients:

```text
payments#0
payments#1
payments#2
payments#3
```

Each named client gets its own primary `SocketsHttpHandler` with `ConfigureForCloud()` applied. The `DistributedHttpClient` itself is registered as a keyed singleton under `"payments"`:

```csharp
public sealed class PaymentsService(
    [FromKeyedServices("payments")] DistributedHttpClient http)
{
}
```

The selector also lives inside that singleton, so round-robin counters and health-aware degradation state are shared across requests.

## Round-robin (default)

Every call advances an atomic counter `idx` and picks `idx % ClientCount`.

- Use when replicas are interchangeable and roughly equal in capacity.
- The selector is lock-free (`Interlocked.Increment`).
- During rotation, it skips the previous failed index when `ClientCount > 1`.

```csharp
services.AddRoundRobinDistribution("payments", clientCount: 4,
    configureClient: c => c.BaseAddress = new Uri("https://payments.svc"));
```

Use this first unless you have a reason not to. It is easy to reason about, and it does not require tuning.

## Weighted

Each client index gets a relative weight. Selection is `Random.Shared.NextDouble() * totalWeight`, then binary-search into a sorted cumulative ladder.

- Use when you want canary-style traffic splits, such as `{0: 9, 1: 1}` for roughly 10% to client 1.
- Every pick is independent. Ratios converge over many requests, not over a small burst.
- During rotation, if the weighted pick equals the previous failed client, the selector returns a different weighted client when possible.
- Weights with invalid indices or non-positive values are ignored. If no positive in-range weights remain, registration fails.

```csharp
services.AddWeightedDistribution("search",
    weights: new Dictionary<int, double> { [0] = 9, [1] = 1 });
```

Weights create enough clients to include the largest configured index. This example creates two clients. If you use keys `0`, `1`, and `4`, CloudHttp creates five clients and only weighted selection targets the indices with positive weights.

Weighted mode does not remember health. A transient failure can rotate away from the failed index for the second attempt, but the next independent call goes back to normal weighted selection.

## Health-aware

Health-aware mode is round-robin with a temporary degraded list. When a client has a transient failure, CloudHttp records `now + HealthDegradedTimeout` for that client. The selector skips it until that timestamp expires. Default timeout: 30 seconds.

Recovery is timeout-based. `MarkHealthy` does not immediately clear degradation, because concurrent requests can complete out of order. An older success should not erase a newer failure.

If all clients are degraded, the selector falls back to round-robin. Calling a degraded client is better than failing before trying.

Time source is `Environment.TickCount64` (monotonic and not affected by wall-clock changes). Tests inject a `Func<long>` clock so the behavior is deterministic.

- Use when single-replica brownouts are common enough that you want short local avoidance.
- This is not a health check. A pod that returns successful but wrong data will not be degraded.

```csharp
services.AddHealthAwareDistribution("inventory",
    clientCount: 4,
    degradedTimeout: TimeSpan.FromSeconds(30));
```

Health-aware mode is local process memory. It is not shared between replicas of your caller service. If you run 10 caller pods, each one keeps its own degraded list.

## What rotation costs

CloudHttp performs at most one extra attempt, and only for operations that allow rotation:

- `DistributedHttpClient.GetAsync<T>()`
- `DistributedHttpClient.SendAsync(factory, ct)`

The JSON mutating helpers (`PostAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync`) do not auto-rotate. Replaying a mutating request after a timeout or 5xx can duplicate side effects if the first attempt reached the server. If your operation is idempotent, add an idempotency key and use `SendAsync(...)` to opt into replay explicitly.

Two transients in a row across two different clients surface as the second-attempt response or exception. If you want retries with jitter, configure `AddStandardResilienceHandler()` inside `configureBuilder`.

## Choosing a mode

Start here:

- Use round-robin for normal replica spreading.
- Use weighted when you intentionally want uneven traffic, such as a canary or a bigger upstream pool.
- Use health-aware when single-replica brownouts are common and short local avoidance helps.

Avoid health-aware mode if transient errors usually mean the whole upstream is overloaded. In that case, skipping one pool just moves pressure to the next pool. A circuit breaker and backoff policy are usually more important.

## App configuration example

You can bind options during registration:

```json
{
  "CloudHttp": {
    "Search": {
      "Mode": "Weighted",
      "ClientCount": 3,
      "RotateOnTransientError": true,
      "ClientWeights": {
        "0": 8,
        "1": 1,
        "2": 1
      }
    }
  }
}
```

```csharp
services.AddDistributedHttpClient(
    "search",
    opts => configuration.GetSection("CloudHttp:Search").Bind(opts),
    configureClient: client => client.BaseAddress = new Uri("https://search.svc.cluster.local"));
```

Keep `ClientCount` in the registration-time configuration. CloudHttp uses it while registering the named clients. Changing it later through named options does not create new named clients.

## What rotation does *not* fix

- A whole namespace or Service going down. All N clients still target the same logical upstream.
- A bad URL, bad auth header, or malformed request. Those are caller bugs.
- Non-idempotent writes without idempotency protection. CloudHttp deliberately avoids auto-replaying the JSON mutating helpers.
- Globally slow upstreams. Use timeouts and resilience policies; rotation only helps when another pool has a better path.

## Client count is fixed at registration

`AddDistributedHttpClient` uses `ClientCount` during registration to create the underlying named clients (`{name}#0`, `{name}#1`, ...). That count is fixed. Later named options can change behavior such as transient status codes or health timeout, but they cannot add more configured named clients after registration.
