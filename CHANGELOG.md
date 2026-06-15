# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] - 2026-06-15

### Added

- `DistributedHttpClient.PostAsJsonAsync<TRequest>`, `PutAsJsonAsync<TRequest>`, and
  `PatchAsJsonAsync<TRequest>`: raw-response write helpers that return the
  `HttpResponseMessage` directly instead of deserialising. They do not call
  `EnsureSuccessStatusCode`, so the caller can inspect the status code, `Location`
  header, or an empty body. Like the deserialising write helpers, they never
  auto-rotate; replaying a write can duplicate side effects.
- `DistributedHttpClient.DeleteAsync(path, ct)`: a non-generic DELETE overload that
  returns the raw response without reading the body. Because it does no JSON work it
  is AOT/trim-safe (no `RequiresUnreferencedCode` / `RequiresDynamicCode`).

  These remove the boilerplate of dropping down to `SendAsync((client, token) => ...)`
  just to send a write and inspect the response. `SendAsync` remains the explicit,
  opt-in path for rotation and idempotent replay.

### Fixed

- Empty and `204 No Content` responses no longer surface a spurious `JsonException`.
  The deserialising helpers (`GetAsync<T>`, `PostAsync`, `PutAsync`, `PatchAsync`,
  `DeleteAsync<T>`) now return `default` when the body is empty (status
  `204 No Content` or a zero `Content-Length`).
- `*WithErrorHandlingAsync` helpers treat an empty/`204` body as success: they return
  the supplied default value without logging a deserialisation error.

## [1.0.0]

### Added

- `DistributedHttpClient`: keyed-singleton wrapper over N independently pooled named
  `HttpClient` instances with round-robin, weighted, and health-aware selection, and
  at-most-one rotation on transient failures for safe operations.
- `AddDistributedHttpClient`, `AddRoundRobinDistribution`, `AddWeightedDistribution`,
  and `AddHealthAwareDistribution` registration extensions.
- `SocketsHttpHandler.ConfigureForCloud()` and `IHttpClientBuilder.ConfigureForCloud()`
  cloud-tuned transport defaults.
- `HttpClient.*WithErrorHandlingAsync` fallback verb helpers with structured logging.
- `HttpRouteBuilder.BuildPath` and `Uri.AddQuery` route/query helpers.
- Multi-targets `net8.0` and `net10.0`; declares `IsAotCompatible`.

[1.1.0]: https://github.com/haiilong/CloudHttp/releases/tag/v1.1.0
[1.0.0]: https://github.com/haiilong/CloudHttp/releases/tag/v1.0.0
