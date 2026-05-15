using System.Net;
using CloudHttp.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHttp.Tests;

public class DistributedHttpClientTests
{
    private static DistributedHttpClient Build(
        IReadOnlyList<HttpClient> clients,
        IClientSelector selector,
        ClientDistributionOptions? options = null)
    {
        // construct via reflection through internal ctor: friend access works because
        // CloudHttp.Tests is listed in InternalsVisibleTo.
        return new DistributedHttpClient(
            clients,
            selector,
            options ?? new ClientDistributionOptions { ClientCount = clients.Count },
            new Logger<DistributedHttpClient>(NullLoggerFactory.Instance));
    }

    private static HttpClient ClientFor(StubHttpMessageHandler handler, string baseUri = "https://test.invalid/")
        => new(handler) { BaseAddress = new Uri(baseUri) };

    [Fact]
    public async Task GetAsync_returns_first_client_success_without_rotation()
    {
        var h0 = StubHttpMessageHandler.ForStatus(HttpStatusCode.OK, "{\"name\":\"a\"}");
        var h1 = StubHttpMessageHandler.ForStatus(HttpStatusCode.OK, "{\"name\":\"b\"}");
        var clients = new[] { ClientFor(h0), ClientFor(h1) };
        var sut = Build(clients, new RoundRobinSelector(2));

        var first = await sut.GetAsync<Sample>("/x");
        var second = await sut.GetAsync<Sample>("/x");

        // RR: first call -> idx 0, second call -> idx 1
        first!.Name.Should().Be("a");
        second!.Name.Should().Be("b");
        h0.InvocationCount.Should().Be(1);
        h1.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_rotates_once_on_transient_status()
    {
        var h0 = StubHttpMessageHandler.ForStatus(HttpStatusCode.ServiceUnavailable);
        var h1 = StubHttpMessageHandler.ForStatus(HttpStatusCode.OK, "{\"name\":\"ok\"}");
        var clients = new[] { ClientFor(h0), ClientFor(h1) };
        var sut = Build(clients, new RoundRobinSelector(2));

        var result = await sut.GetAsync<Sample>("/x");

        result!.Name.Should().Be("ok");
        h0.InvocationCount.Should().Be(1);
        h1.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_does_not_rotate_when_RotateOnTransientError_false()
    {
        var h0 = StubHttpMessageHandler.ForStatus(HttpStatusCode.ServiceUnavailable);
        var h1 = StubHttpMessageHandler.ForStatus(HttpStatusCode.OK, "{\"name\":\"ok\"}");
        var clients = new[] { ClientFor(h0), ClientFor(h1) };
        var opts = new ClientDistributionOptions { ClientCount = 2, RotateOnTransientError = false };
        var sut = Build(clients, new RoundRobinSelector(2), opts);

        var act = async () => await sut.GetAsync<Sample>("/x");

        await act.Should().ThrowAsync<HttpRequestException>();
        h0.InvocationCount.Should().Be(1);
        h1.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task GetAsync_propagates_caller_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancellationToken = cts.Token;
        var h0 = new StubHttpMessageHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var clients = new[] { ClientFor(h0) };
        var sut = Build(clients, new RoundRobinSelector(1));

        var act = async () => await sut.GetAsync<Sample>("/x", cancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetAsync_rethrows_non_transient_exception()
    {
        var h0 = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("boom"));
        var clients = new[] { ClientFor(h0) };
        var sut = Build(clients, new RoundRobinSelector(1));

        var act = async () => await sut.GetAsync<Sample>("/x");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task SendAsync_invokes_caller_factory_with_selected_client()
    {
        var h0 = StubHttpMessageHandler.ForStatus(HttpStatusCode.OK);
        var h1 = StubHttpMessageHandler.ForStatus(HttpStatusCode.OK);
        var clients = new[] { ClientFor(h0), ClientFor(h1) };
        var sut = Build(clients, new RoundRobinSelector(2));

        using var resp1 = await sut.SendAsync((c, t) => c.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/y"), t));
        using var resp2 = await sut.SendAsync((c, t) => c.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/y"), t));

        h0.InvocationCount.Should().Be(1);
        h1.InvocationCount.Should().Be(1);
        resp1.IsSuccessStatusCode.Should().BeTrue();
        resp2.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_rotates_on_transient_status()
    {
        var h0 = StubHttpMessageHandler.ForStatus(HttpStatusCode.BadGateway);
        var h1 = StubHttpMessageHandler.ForStatus(HttpStatusCode.OK);
        var clients = new[] { ClientFor(h0), ClientFor(h1) };
        var sut = Build(clients, new RoundRobinSelector(2));

        using var response = await sut.SendAsync((c, t) => c.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/y"), t));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        h0.InvocationCount.Should().Be(1);
        h1.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task Rotates_on_transient_exception()
    {
        var firstHit = false;
        var h0 = new StubHttpMessageHandler((_, _) =>
        {
            firstHit = true;
            throw new HttpRequestException("connection refused", inner: null, statusCode: null);
        });
        var h1 = StubHttpMessageHandler.ForStatus(HttpStatusCode.OK, "{\"name\":\"ok\"}");
        var clients = new[] { ClientFor(h0), ClientFor(h1) };
        var sut = Build(clients, new RoundRobinSelector(2));

        var result = await sut.GetAsync<Sample>("/x");

        firstHit.Should().BeTrue();
        h1.InvocationCount.Should().Be(1);
        result!.Name.Should().Be("ok");
    }

    [Fact]
    public async Task Rotates_on_IOException()
    {
        var h0 = new StubHttpMessageHandler((_, _) => throw new IOException("socket broke"));
        var h1 = StubHttpMessageHandler.ForStatus(HttpStatusCode.OK, "{\"name\":\"ok\"}");
        var clients = new[] { ClientFor(h0), ClientFor(h1) };
        var sut = Build(clients, new RoundRobinSelector(2));

        var result = await sut.GetAsync<Sample>("/x");

        result!.Name.Should().Be("ok");
        h1.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task Rotates_on_HttpClient_Timeout_shape()
    {
        // HttpClient.Timeout throws TaskCanceledException with InnerException = TimeoutException
        // — this is distinct from caller cancellation.
        var h0 = new StubHttpMessageHandler((_, _) =>
            throw new TaskCanceledException("timeout", new TimeoutException("inner")));
        var h1 = StubHttpMessageHandler.ForStatus(HttpStatusCode.OK, "{\"name\":\"ok\"}");
        var clients = new[] { ClientFor(h0), ClientFor(h1) };
        var sut = Build(clients, new RoundRobinSelector(2));

        var result = await sut.GetAsync<Sample>("/x");

        result!.Name.Should().Be("ok");
        h1.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task Both_clients_503_returns_second_response()
    {
        var h0 = StubHttpMessageHandler.ForStatus(HttpStatusCode.ServiceUnavailable);
        var h1 = StubHttpMessageHandler.ForStatus(HttpStatusCode.GatewayTimeout);
        var clients = new[] { ClientFor(h0), ClientFor(h1) };
        var sut = Build(clients, new RoundRobinSelector(2));

        var act = async () => await sut.GetAsync<Sample>("/x");

        // EnsureSuccessStatusCode throws on the second response (504)
        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        h0.InvocationCount.Should().Be(1);
        h1.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task Both_clients_throw_transient_rethrows_second_exception()
    {
        var h0 = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("first"));
        var h1 = new StubHttpMessageHandler((_, _) => throw new IOException("second"));
        var clients = new[] { ClientFor(h0), ClientFor(h1) };
        var sut = Build(clients, new RoundRobinSelector(2));

        var act = async () => await sut.GetAsync<Sample>("/x");

        (await act.Should().ThrowAsync<IOException>()).WithMessage("second");
    }

    [Fact]
    public async Task Single_client_transient_marks_degraded_and_does_not_rotate()
    {
        var h0 = StubHttpMessageHandler.ForStatus(HttpStatusCode.ServiceUnavailable);
        var clients = new[] { ClientFor(h0) };
        var selector = new HealthAwareSelector(1, TimeSpan.FromSeconds(30));
        var sut = Build(clients, selector);

        var act = async () => await sut.GetAsync<Sample>("/x");

        await act.Should().ThrowAsync<HttpRequestException>();
        h0.InvocationCount.Should().Be(1); // no rotation possible with 1 client
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Mutating_json_helpers_do_not_auto_rotate_on_transient_status(string verb)
    {
        var h0 = StubHttpMessageHandler.ForStatus(HttpStatusCode.ServiceUnavailable);
        var h1 = StubHttpMessageHandler.ForStatus(HttpStatusCode.OK, "{\"name\":\"ok\"}");
        var clients = new[] { ClientFor(h0), ClientFor(h1) };
        var sut = Build(clients, new RoundRobinSelector(2));

        var act = async () =>
        {
            _ = verb switch
            {
                "POST" => await sut.PostAsync<Sample, Sample>("/x", new Sample { Name = "request" }),
                "PUT" => await sut.PutAsync<Sample, Sample>("/x", new Sample { Name = "request" }),
                "PATCH" => await sut.PatchAsync<Sample, Sample>("/x", new Sample { Name = "request" }),
                "DELETE" => await sut.DeleteAsync<Sample>("/x"),
                _ => throw new InvalidOperationException(),
            };
        };

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        h0.InvocationCount.Should().Be(1);
        h1.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task HealthAware_marks_second_attempt_degraded_on_rotation_failure()
    {
        // regression: previously the rotation path never called Mark*, so a failing
        // second client stayed in rotation. After fix both clients should be marked.
        var h0 = StubHttpMessageHandler.ForStatus(HttpStatusCode.ServiceUnavailable);
        var h1 = StubHttpMessageHandler.ForStatus(HttpStatusCode.BadGateway);
        var clients = new[] { ClientFor(h0), ClientFor(h1) };
        var clock = new TestClock { Now = 1000 };
        var selector = new HealthAwareSelector(2, TimeSpan.FromSeconds(30), () => clock.Now);
        var sut = Build(clients, selector);

        try { await sut.GetAsync<Sample>("/x"); } catch (HttpRequestException) { /* expected */ }

        // both clients should now be degraded — selector falls back to RR since all degraded
        // assert this indirectly: probing Select repeatedly should include both indices
        // while both are still within the 30s degraded window.
        var picks = new HashSet<int>();
        for (var i = 0; i < 20; i++) picks.Add(selector.Select(null));
        picks.Should().BeEquivalentTo([0, 1]); // all-degraded fallback to RR

        // advance past timeout: both should be healthy
        clock.Now = 1000 + 31_000;
        picks.Clear();
        for (var i = 0; i < 20; i++) picks.Add(selector.Select(null));
        picks.Should().BeEquivalentTo([0, 1]);
    }

    [Fact]
    public void HealthAware_does_not_clear_newer_degradation_from_stale_success()
    {
        var clock = new TestClock { Now = 1000 };
        var selector = new HealthAwareSelector(2, TimeSpan.FromSeconds(30), () => clock.Now);

        selector.MarkDegraded(0);
        selector.MarkHealthy(0);

        selector.Select(null).Should().Be(1);
    }

    private sealed class Sample
    {
        public string? Name { get; set; }
    }

    private sealed class TestClock
    {
        public long Now { get; set; }
    }
}
