# CloudHttp.Sample

This sample shows `DistributedHttpClient` rotating across several independently pooled clients.

The sample executable has two modes:

- `server`: starts a tiny ASP.NET Core upstream API.
- `client`: calls the upstream through `DistributedHttpClient` and prints which backend instance handled each response.

Docker Compose runs several `server` containers behind Docker's internal DNS name `upstream`. The client calls `http://upstream:8080` with multiple underlying named `HttpClient` instances. Because each named client has its own `SocketsHttpHandler`, the response summary should show traffic landing on more than one upstream container.

This is not a perfect model of Kubernetes. It is a compact way to see the thing CloudHttp is trying to improve: one logical upstream, several independent client pools, and visible backend selection.

## Run the demo

From the repository root, run Docker Compose directly:

```powershell
$env:REQUESTS = "48"
$env:CLIENT_COUNT = "8"
$env:DISTRIBUTION_MODE = "RoundRobin"
$env:ENDPOINT = "/whoami"
$env:FAIL_EVERY = "0"

docker compose `
  --file .\samples\CloudHttp.Sample\compose.yaml `
  up `
  --build `
  --abort-on-container-exit `
  --exit-code-from client `
  --scale upstream=4 `
  client
```

Useful variants:

```powershell
# More upstream replicas and more requests.
$env:REQUESTS = "120"
$env:CLIENT_COUNT = "12"
docker compose --file .\samples\CloudHttp.Sample\compose.yaml up --build --abort-on-container-exit --exit-code-from client --scale upstream=6 client

# Use health-aware mode.
$env:DISTRIBUTION_MODE = "HealthAware"
docker compose --file .\samples\CloudHttp.Sample\compose.yaml up --build --abort-on-container-exit --exit-code-from client --scale upstream=4 client

# Use weighted mode. The sample gives the last client a larger weight.
$env:DISTRIBUTION_MODE = "Weighted"
docker compose --file .\samples\CloudHttp.Sample\compose.yaml up --build --abort-on-container-exit --exit-code-from client --scale upstream=4 client

# Make each upstream return 503 every third request.
# GetAsync can rotate once after a transient failure.
$env:DISTRIBUTION_MODE = "HealthAware"
$env:ENDPOINT = "/unstable"
$env:FAIL_EVERY = "3"
$env:REQUESTS = "80"
docker compose --file .\samples\CloudHttp.Sample\compose.yaml up --build --abort-on-container-exit --exit-code-from client --scale upstream=4 client
```

The unstable variant changes the client endpoint to `/unstable` and makes every upstream return 503 every third request. `GetAsync` can rotate once after a transient failure, so this mode makes rotation easier to spot.

Clean up containers when you are done:

```powershell
docker compose --file .\samples\CloudHttp.Sample\compose.yaml down --remove-orphans
```

## What to look for

The client prints one line per request:

```text
[12] cloudhttp-sample-upstream-3 handled backend request #4 (ok)
```

At the end it prints a summary:

```text
Summary
-------
cloudhttp-sample-upstream-1: 11 responses
cloudhttp-sample-upstream-2: 14 responses
cloudhttp-sample-upstream-3: 12 responses
cloudhttp-sample-upstream-4: 11 responses
Failures observed by client: 0
```

The distribution will not be perfectly even. Docker DNS, connection pooling, timing, and request count all affect the result. The point is to show that a single logical upstream can be reached through several independent client pools.

## Files involved

- `Program.cs`: dual-mode server/client sample.
- `compose.yaml`: starts scaled upstream containers and the client.
- `Dockerfile`: builds the sample image.
- `run-demo-local.ps1`: starts one local upstream process and runs the client without Docker.

## How this maps to production

The sample uses Docker Compose because it is easy to run locally. In production, the same shape maps to a cluster service:

```csharp
services.AddDistributedHttpClient(
    name: "inventory",
    configureOptions: opts =>
    {
        opts.Mode = DistributionMode.HealthAware;
        opts.ClientCount = 8;
        opts.HealthDegradedTimeout = TimeSpan.FromSeconds(30);
    },
    configureClient: client =>
    {
        client.BaseAddress = new Uri("https://inventory.svc.cluster.local");
        client.Timeout = TimeSpan.FromSeconds(10);
    },
    configurePrimaryHandler: handler =>
    {
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
    });
```

The important parts are:

- Multiple named clients are registered under `inventory#0`, `inventory#1`, and so on.
- Each named client gets its own primary handler and connection pool.
- The `DistributedHttpClient` is a keyed singleton, so selector state lives across calls.
- `configurePrimaryHandler` is the hook for overriding CloudHttp's handler defaults on every underlying client.

## Run without Docker

Use the local PowerShell runner:

```powershell
.\samples\CloudHttp.Sample\run-demo-local.ps1
```

Useful variants:

```powershell
# More requests against the local upstream.
.\samples\CloudHttp.Sample\run-demo-local.ps1 -Requests 50 -ClientCount 8

# Exercise the health-aware selector against the local unstable endpoint.
.\samples\CloudHttp.Sample\run-demo-local.ps1 -Mode HealthAware -Unstable

# Use a different local port.
.\samples\CloudHttp.Sample\run-demo-local.ps1 -Port 18080
```

The local script starts one upstream process and then runs the client against it. It is good for debugging the sample without Docker. It does not demonstrate multi-replica backend distribution because there is only one upstream process.

You can also run the two modes manually. Start one upstream:

```powershell
dotnet run --project .\samples\CloudHttp.Sample\CloudHttp.Sample.csproj -- server
```

Then run the client in another terminal:

```powershell
$env:UPSTREAM_URL = "http://localhost:8080"
$env:CLIENT_COUNT = "4"
$env:REQUESTS = "20"
dotnet run --project .\samples\CloudHttp.Sample\CloudHttp.Sample.csproj -- client
```

To demonstrate several backend instances without Docker, put multiple local server processes behind your own local load balancer or DNS name, then point `UPSTREAM_URL` at that single front door. The Docker Compose demo already provides that shape.

## Environment variables

Client mode:

- `UPSTREAM_URL`: upstream base URL. Default: `http://localhost:8080`.
- `CLIENT_COUNT`: number of underlying named clients. Default: `8`.
- `REQUESTS`: number of requests to send. Default: `48`.
- `DELAY_MS`: delay between requests. Default: `100`.
- `DISTRIBUTION_MODE`: `RoundRobin`, `Weighted`, or `HealthAware`. Default: `RoundRobin`.
- `ENDPOINT`: `/whoami` or `/unstable`. Default: `/whoami`.

Server mode:

- `ASPNETCORE_URLS`: listen URL. Compose sets `http://+:8080`.
- `INSTANCE_NAME`: optional display name. If omitted, the container hostname is used.
- `FAIL_EVERY`: return 503 every N requests on `/unstable`. Use `0` to disable.

## Scripts

- `run-demo-local.ps1`: local one-upstream debug demo.

## Troubleshooting

If Docker says the project cannot be found, run the script from the repository root.

If all responses come from one backend, increase `-Requests`, increase `-ClientCount`, or run with more replicas. Small request counts can look lopsided.

If the unstable demo still shows few failures, that is expected. The client rotates once for `GetAsync`, and a second backend often succeeds.
