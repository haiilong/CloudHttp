# Stacking CloudHttp with `Microsoft.Extensions.Http.Resilience`

CloudHttp deliberately does not implement multi-retry, exponential backoff, jitter, or circuit breaking. Microsoft's [`Microsoft.Extensions.Http.Resilience`](https://learn.microsoft.com/dotnet/core/resilience/http-resilience) package (Polly v8 under the hood) already does that well. Use CloudHttp for independent pools and client selection. Use Resilience for policy.

Install it separately:

```sh
dotnet add package Microsoft.Extensions.Http.Resilience
```

## Where the handler attaches

`AddDistributedHttpClient` registers N named clients (`name#0`, `name#1`, ...). The `configureBuilder` callback receives each underlying `IHttpClientBuilder`, so resilience handlers attach per underlying client, not once around the distributor. Each connection pool gets its own circuit breaker.

```csharp
services.AddDistributedHttpClient(
    name: "payments",
    configureOptions: opts =>
    {
        opts.Mode = DistributionMode.RoundRobin;
        opts.ClientCount = 4;
    },
    configureClient: c => c.BaseAddress = new Uri("https://payments.svc.cluster.local"),
    configureBuilder: cb =>
    {
        cb.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromMilliseconds(200);

            options.CircuitBreaker.FailureRatio = 0.2;
            options.CircuitBreaker.MinimumThroughput = 20;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);

            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        });
    });
```

This is the usual production shape:

- CloudHttp creates several independently pooled named clients.
- Resilience runs inside each named client.
- CloudHttp rotates only after that named client's pipeline reports a transient result.
- The caller still owns the total deadline with a `CancellationToken`.

## Order of operations for `GetAsync` / `SendAsync`

1. A request goes out via client `#0`.
2. Upstream returns 503.
3. Resilience pipeline on `#0`: retries with jittered backoff. Each retry stays on the same named client and therefore the same connection pool.
4. If the response or exception is still transient after the resilience pipeline, CloudHttp marks client `#0` degraded.
5. CloudHttp rotates once to a different client, usually `#1`.
6. Resilience pipeline on `#1` runs.
7. Whatever the second client returns is what the caller gets. CloudHttp does not keep looping.

If `MaxRetryAttempts = 3`, the caller can see up to 8 attempts total: 4 on the first client and 4 on the rotated client.

That number surprises people. Keep the timeout budget small enough that the worst path still fits inside what the caller can tolerate.

## Mutating JSON helpers do not auto-rotate

`PostAsync`, `PutAsync`, `PatchAsync`, and `DeleteAsync` on `DistributedHttpClient` do not rotate automatically. A timeout or 503 does not prove the server failed to process the request. Replaying a charge, shipment, or write can duplicate side effects.

If the operation is safe to replay, make that explicit:

```csharp
await http.SendAsync((client, ct) =>
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/charges")
    {
        Content = JsonContent.Create(body)
    };
    request.Headers.Add("Idempotency-Key", idempotencyKey);
    return client.SendAsync(request, ct);
}, ct);
```

The idempotency key belongs to your API contract. CloudHttp cannot invent it for you.

For payments, order creation, emails, provisioning, and any operation with external side effects, assume replay is unsafe until the upstream API proves otherwise.

## What about `Retry-After`?

`AddStandardResilienceHandler` honours `Retry-After` on 429 / 503 responses. Don't add your own retry on top.

## Custom transient classification

Add status codes specific to your upstream to `ClientDistributionOptions.TransientStatusCodes`:

```csharp
opts.TransientStatusCodes.Add(HttpStatusCode.UnprocessableEntity);
```

Rotation treats the failure as "try a different client once." The retry policy inside the resilience handler decides whether to retry within the same client first. Add the status to both places if you want both behaviors.

Example:

```csharp
services.AddDistributedHttpClient(
    "search",
    opts =>
    {
        opts.ClientCount = 4;
        opts.TransientStatusCodes.Add(HttpStatusCode.RequestTimeout);
    },
    configureClient: client => client.BaseAddress = new Uri("https://search.svc.cluster.local"),
    configureBuilder: builder =>
    {
        builder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 2;
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
        });
    });
```

## Timeouts and rotation

CloudHttp's default `IsTransientException` predicate treats `HttpClient.Timeout` as transient when it appears as `TaskCanceledException` with `InnerException = TimeoutException`. `GetAsync` and `SendAsync` can rotate once on that timeout. Caller cancellation is different: when the provided `CancellationToken` is cancelled, CloudHttp rethrows `OperationCanceledException` and does not rotate.

### Why rotate on timeout

Single-pod brownouts are real in Kubernetes. One replica can be GC-paused, CPU-throttled, or halfway through rollout while its siblings are fine. If a request is stuck on a connection pool that keeps reaching that replica, a single rotation gives another pool a chance.

Without rotation on timeout, a single sick pod stalls every request that hashes to it through the round-robin / weighted / health-aware selector. With rotation, one extra attempt against a different pool usually succeeds.

### The wall-clock caveat

Rotation costs at most one extra attempt. On the worst path that is `2 x HttpClient.Timeout`. If you set `c.Timeout = 30s` and both attempts time out, the caller waits 60 seconds before seeing failure. If the upstream service is globally slow, rotation adds load and time without helping.

### Bound the total deadline at the caller

A caller-side total budget is the cleanest way to bound the whole operation. Use `CancellationTokenSource.CancelAfter` so the full call, including any retry and rotation, is bounded by one number you control:

```csharp
using var cts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
cts.CancelAfter(TimeSpan.FromSeconds(30));
await http.GetAsync<Foo>("/x", cts.Token);
```

When `cts` fires, CloudHttp rethrows `OperationCanceledException` immediately. No rotation. Caller sees timeout at 30 s, not 60 s.

If you cannot use a caller `CancellationToken`, set per-client `HttpClient.Timeout` to roughly half of your acceptable total wall-clock budget so the rotation still fits under the deadline:

```csharp
configureClient: c => c.Timeout = TimeSpan.FromSeconds(15) // total <= ~30 s
```

### Interaction with the resilience handler

Polly v8 throws `Polly.Timeout.TimeoutRejectedException` for both `AttemptTimeout` and `TotalRequestTimeout`. That type **does not** match CloudHttp's default `IsTransientException`. So when you attach `AddStandardResilienceHandler`, the layering is:

1. Each attempt is bounded by `AttemptTimeout` and retried by Polly (within the same client).
2. The whole pipeline is bounded by `TotalRequestTimeout` (also Polly).
3. If Polly times out the pipeline, the exception bubbles out without triggering CloudHttp rotation. Correct, because Polly already retried.
4. If a raw `HttpClient.Timeout` slips through (e.g. you set it tighter than `TotalRequestTimeout`), CloudHttp rotates once.

That layering is intentional: Polly retries within one named client, then CloudHttp can jump to a different named client once.

## Suggested starting point

For a cluster-local dependency with normal latency:

```csharp
services.AddDistributedHttpClient(
    "inventory",
    opts =>
    {
        opts.Mode = DistributionMode.HealthAware;
        opts.ClientCount = 4;
        opts.HealthDegradedTimeout = TimeSpan.FromSeconds(30);
    },
    configureClient: client =>
    {
        client.BaseAddress = new Uri("https://inventory.svc.cluster.local");
        client.Timeout = TimeSpan.FromSeconds(15);
    },
    configureBuilder: builder =>
    {
        builder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 2;
            options.Retry.Delay = TimeSpan.FromMilliseconds(100);
            options.Retry.UseJitter = true;

            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(3);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);

            options.CircuitBreaker.MinimumThroughput = 20;
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
        });
    });
```

Treat those as starting numbers, not magic. The right values depend on your p95 latency, upstream capacity, and how many caller replicas you run.

### Opting out

If you do not want timeout-driven rotation:

```csharp
configureOptions: opts =>
{
    opts.IsTransientException = ex => ex is HttpRequestException || ex is IOException;
}
```

Or disable rotation entirely:

```csharp
configureOptions: opts => opts.RotateOnTransientError = false;
```

## Don't double-handle cancellation

The resilience handler observes the request `CancellationToken` and will not retry once it's cancelled. CloudHttp likewise rethrows `OperationCanceledException` when `ct.IsCancellationRequested` - no swallowing.

## Anti-patterns

- Writing another `try/catch` retry loop around a client that already uses `AddStandardResilienceHandler`.
- Relying on CloudHttp's single rotation as your only retry policy for noisy production traffic.
- Auto-replaying mutating operations without idempotency keys.
- Attaching resilience to one shared client and trying to put CloudHttp "in front of it". Use `configureBuilder`; it runs per underlying named client.
- Setting generous `HttpClient.Timeout`, generous Polly total timeout, and caller `CancelAfter` all at once without deciding which one owns the deadline.
