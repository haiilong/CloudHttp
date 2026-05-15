using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHttp.Internal;
using Microsoft.Extensions.Logging;

namespace CloudHttp;

/// <summary>
/// Wraps a set of independently-pooled <see cref="HttpClient"/> instances and routes each
/// outbound request to one of them according to a <see cref="DistributionMode"/>. Designed
/// for Kubernetes / dynamic-hosting scenarios where a single long-lived HTTP/2 connection
/// would pin to one upstream pod; using N clients with N <see cref="SocketsHttpHandler"/>
/// instances spreads connections across pods.
/// </summary>
/// <remarks>
/// This type performs <em>at most one rotation</em> on a transient failure (status code in
/// <see cref="ClientDistributionOptions.TransientStatusCodes"/> or exception matching
/// <see cref="ClientDistributionOptions.IsTransientException"/>) for safe operations and
/// explicit <see cref="SendAsync"/> calls. It does not implement exponential backoff, jitter,
/// or circuit-breaking, compose with <c>Microsoft.Extensions.Http.Resilience</c> on each
/// underlying named client for those.
/// </remarks>
public sealed class DistributedHttpClient
{
    private readonly IReadOnlyList<HttpClient> _clients;
    private readonly IClientSelector _selector;
    private readonly ClientDistributionOptions _options;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions? _jsonOptions;

    /// <summary>Number of underlying clients in this distributor.</summary>
    public int ClientCount => _clients.Count;

    internal DistributedHttpClient(
        IReadOnlyList<HttpClient> clients,
        IClientSelector selector,
        ClientDistributionOptions options,
        ILogger<DistributedHttpClient> logger,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        if (clients.Count == 0) throw new ArgumentException("At least one client is required.", nameof(clients));
        if (clients.Count != selector.Count) throw new ArgumentException("Selector count must match client count.", nameof(selector));

        _clients = clients;
        _selector = selector;
        _options = options;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    /// <summary>
    /// Sends a request via the next selected client. <paramref name="send"/> is invoked with
    /// the chosen <see cref="HttpClient"/>; it may be invoked twice if the first attempt
    /// returns a transient status and <see cref="ClientDistributionOptions.RotateOnTransientError"/>
    /// is set. Caller is responsible for disposing the returned <see cref="HttpResponseMessage"/>.
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        return ExecuteAsync(send, ct);
    }

    /// <summary>
    /// Sends a GET to <paramref name="path"/> via the next selected client, deserialising the
    /// JSON body as <typeparamref name="TResponse"/>. Throws <see cref="HttpRequestException"/>
    /// on non-success status after at most one rotation.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("JSON serialization may require runtime code generation.")]
    public async Task<TResponse?> GetAsync<TResponse>(string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var response = await ExecuteAsync((c, t) => c.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, t), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(_jsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>POSTs <paramref name="request"/> as JSON to <paramref name="path"/>; returns the deserialised JSON body.</summary>
    [RequiresUnreferencedCode("JSON serialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("JSON serialization may require runtime code generation.")]
    public async Task<TResponse?> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var response = await ExecuteAsync((c, t) => c.PostAsJsonAsync(path, request, _jsonOptions, t), ct, allowRotation: false).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(_jsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>PUTs <paramref name="request"/> as JSON to <paramref name="path"/>; returns the deserialised JSON body.</summary>
    [RequiresUnreferencedCode("JSON serialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("JSON serialization may require runtime code generation.")]
    public async Task<TResponse?> PutAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var response = await ExecuteAsync((c, t) => c.PutAsJsonAsync(path, request, _jsonOptions, t), ct, allowRotation: false).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(_jsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>PATCHes <paramref name="request"/> as JSON to <paramref name="path"/>; returns the deserialised JSON body.</summary>
    [RequiresUnreferencedCode("JSON serialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("JSON serialization may require runtime code generation.")]
    public async Task<TResponse?> PatchAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var response = await ExecuteAsync((c, t) => c.PatchAsJsonAsync(path, request, _jsonOptions, t), ct, allowRotation: false).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(_jsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>DELETEs <paramref name="path"/>; returns the deserialised JSON body (if any).</summary>
    [RequiresUnreferencedCode("JSON serialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("JSON serialization may require runtime code generation.")]
    public async Task<TResponse?> DeleteAsync<TResponse>(string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var response = await ExecuteAsync((c, t) => c.DeleteAsync(path, t), ct, allowRotation: false).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(_jsonOptions, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> ExecuteAsync(
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken ct,
        bool allowRotation = true)
    {
        var firstIdx = _selector.Select(previousIndex: null);
        HttpResponseMessage? response = null;
        Exception? caught = null;

        try
        {
            response = await send(_clients[firstIdx], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (_options.IsTransientException(ex))
        {
            caught = ex;
        }

        var hadTransientStatus = response is not null && _options.TransientStatusCodes.Contains(response.StatusCode);
        var isTransient = caught is not null || hadTransientStatus;

        if (!isTransient)
        {
            _selector.MarkHealthy(firstIdx);
            return response!;
        }

        _selector.MarkDegraded(firstIdx);

        if (!allowRotation || !_options.RotateOnTransientError || _clients.Count == 1)
        {
            return caught is not null ? throw caught : response!;
        }

        _logger.LogDebug(
            caught,
            "DistributedHttpClient: client #{ClientIndex} returned transient {Status}; rotating.",
            firstIdx,
            response?.StatusCode);

        response?.Dispose();
        var secondIdx = _selector.Select(previousIndex: firstIdx);
        HttpResponseMessage? secondResponse = null;
        Exception? secondCaught = null;
        try
        {
            secondResponse = await send(_clients[secondIdx], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (_options.IsTransientException(ex))
        {
            secondCaught = ex;
        }

        var secondHadTransientStatus = secondResponse is not null && _options.TransientStatusCodes.Contains(secondResponse.StatusCode);
        if (secondCaught is not null || secondHadTransientStatus)
        {
            _selector.MarkDegraded(secondIdx);
            return secondCaught is not null ? throw secondCaught : secondResponse!;
        }

        _selector.MarkHealthy(secondIdx);
        return secondResponse!;
    }
}
