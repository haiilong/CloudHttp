using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudHttp;

/// <summary>
/// "Best-effort" verb helpers for <see cref="HttpClient"/> that return a caller-supplied
/// <c>defaultResponse</c> on transport, status, JSON or timeout errors, logging the failure
/// so it shows up in observability without bubbling the exception. Useful when downstream
/// availability is non-critical and the caller has a sensible fallback value.
/// </summary>
/// <remarks>
/// Caller cancellation (<see cref="CancellationToken.IsCancellationRequested"/>) is
/// <em>always</em> propagated as <see cref="OperationCanceledException"/>. Only request
/// timeouts and downstream-side errors are swallowed.
/// </remarks>
public static class HttpClientErrorHandlingExtensions
{
    /// <summary>GETs JSON or returns <paramref name="defaultResponse"/> on failure.</summary>
    [RequiresUnreferencedCode("JSON serialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("JSON serialization may require runtime code generation.")]
    public static Task<TResponse> GetWithErrorHandlingAsync<TResponse>(
        this HttpClient client,
        string path,
        TResponse defaultResponse,
        ILogger logger,
        JsonSerializerOptions? options = null,
        LogLevel errorLogLevel = LogLevel.Error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        return ExecuteAsync(
            (c, t) => c.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, t),
            client, defaultResponse, logger, options, errorLogLevel, "GET", path, ct);
    }

    /// <summary>POSTs JSON or returns <paramref name="defaultResponse"/> on failure.</summary>
    [RequiresUnreferencedCode("JSON serialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("JSON serialization may require runtime code generation.")]
    public static Task<TResponse> PostWithErrorHandlingAsync<TRequest, TResponse>(
        this HttpClient client,
        string path,
        TRequest request,
        TResponse defaultResponse,
        ILogger logger,
        JsonSerializerOptions? options = null,
        LogLevel errorLogLevel = LogLevel.Error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        return ExecuteAsync(
            (c, t) => c.PostAsJsonAsync(path, request, options, t),
            client, defaultResponse, logger, options, errorLogLevel, "POST", path, ct);
    }

    /// <summary>PUTs JSON or returns <paramref name="defaultResponse"/> on failure.</summary>
    [RequiresUnreferencedCode("JSON serialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("JSON serialization may require runtime code generation.")]
    public static Task<TResponse> PutWithErrorHandlingAsync<TRequest, TResponse>(
        this HttpClient client,
        string path,
        TRequest request,
        TResponse defaultResponse,
        ILogger logger,
        JsonSerializerOptions? options = null,
        LogLevel errorLogLevel = LogLevel.Error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        return ExecuteAsync(
            (c, t) => c.PutAsJsonAsync(path, request, options, t),
            client, defaultResponse, logger, options, errorLogLevel, "PUT", path, ct);
    }

    /// <summary>PATCHes JSON or returns <paramref name="defaultResponse"/> on failure.</summary>
    [RequiresUnreferencedCode("JSON serialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("JSON serialization may require runtime code generation.")]
    public static Task<TResponse> PatchWithErrorHandlingAsync<TRequest, TResponse>(
        this HttpClient client,
        string path,
        TRequest request,
        TResponse defaultResponse,
        ILogger logger,
        JsonSerializerOptions? options = null,
        LogLevel errorLogLevel = LogLevel.Error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        return ExecuteAsync(
            (c, t) => c.PatchAsJsonAsync(path, request, options, t),
            client, defaultResponse, logger, options, errorLogLevel, "PATCH", path, ct);
    }

    /// <summary>DELETEs and reads JSON body, or returns <paramref name="defaultResponse"/> on failure.</summary>
    [RequiresUnreferencedCode("JSON serialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("JSON serialization may require runtime code generation.")]
    public static Task<TResponse> DeleteWithErrorHandlingAsync<TResponse>(
        this HttpClient client,
        string path,
        TResponse defaultResponse,
        ILogger logger,
        JsonSerializerOptions? options = null,
        LogLevel errorLogLevel = LogLevel.Error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        return ExecuteAsync(
            (c, t) => c.DeleteAsync(path, t),
            client, defaultResponse, logger, options, errorLogLevel, "DELETE", path, ct);
    }

    /// <summary>
    /// GETs a stream or returns <see cref="Stream.Null"/> on failure. Caller cannot distinguish
    /// "empty response" from "failed", check <see cref="Stream.Length"/> only after reading;
    /// for hard errors rely on the log.
    /// </summary>
    public static async Task<Stream> GetStreamWithErrorHandlingAsync(
        this HttpClient client,
        string path,
        ILogger logger,
        LogLevel errorLogLevel = LogLevel.Error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return await client.GetStreamAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            logger.Log(errorLogLevel, ex, "HTTP GET stream {Path} failed. Status: {StatusCode}", path, ex.StatusCode);
            return Stream.Null;
        }
        catch (TaskCanceledException ex)
        {
            logger.Log(errorLogLevel, ex, "HTTP GET stream {Path} timed out", path);
            return Stream.Null;
        }
        catch (IOException ex)
        {
            logger.Log(errorLogLevel, ex, "HTTP GET stream {Path} I/O error", path);
            return Stream.Null;
        }
    }

    [RequiresUnreferencedCode("JSON serialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("JSON serialization may require runtime code generation.")]
    private static async Task<TResponse> ExecuteAsync<TResponse>(
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
        HttpClient client,
        TResponse defaultResponse,
        ILogger logger,
        JsonSerializerOptions? options,
        LogLevel errorLogLevel,
        string method,
        string path,
        CancellationToken ct)
    {
        try
        {
            using var response = await send(client, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
                return defaultResponse;
            return await response.Content.ReadFromJsonAsync<TResponse>(options, ct).ConfigureAwait(false)
                ?? defaultResponse;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            logger.Log(errorLogLevel, ex, "HTTP {Method} {Path} failed. Status: {StatusCode}", method, path, ex.StatusCode);
            return defaultResponse;
        }
        catch (JsonException ex)
        {
            logger.Log(errorLogLevel, ex, "JSON deserialization failed for {Method} {Path}", method, path);
            return defaultResponse;
        }
        catch (TaskCanceledException ex)
        {
            logger.Log(errorLogLevel, ex, "Request timed out for {Method} {Path}", method, path);
            return defaultResponse;
        }
        catch (IOException ex)
        {
            logger.Log(errorLogLevel, ex, "I/O error for {Method} {Path}", method, path);
            return defaultResponse;
        }
    }
}
