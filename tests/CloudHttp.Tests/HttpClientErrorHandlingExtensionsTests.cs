using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHttp.Tests;

public class HttpClientErrorHandlingExtensionsTests
{
    private static HttpClient ClientFor(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new StubHttpMessageHandler(responder)) { BaseAddress = new Uri("https://test.invalid/") };

    private sealed record Payload(string Name);

    [Fact]
    public async Task GetWithErrorHandling_returns_deserialised_body_on_success()
    {
        var client = ClientFor((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"Name\":\"ok\"}", Encoding.UTF8, "application/json"),
        }));

        var result = await client.GetWithErrorHandlingAsync(
            "/x", defaultResponse: new Payload("default"), logger: NullLogger.Instance);

        result.Name.Should().Be("ok");
    }

    [Fact]
    public async Task GetWithErrorHandling_returns_default_on_5xx()
    {
        var captured = new CapturedLogger();
        var client = ClientFor((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var result = await client.GetWithErrorHandlingAsync("/x", new Payload("fb"), captured);

        result.Name.Should().Be("fb");
        captured.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task GetWithErrorHandling_returns_default_on_malformed_json()
    {
        var captured = new CapturedLogger();
        var client = ClientFor((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("this is not json", Encoding.UTF8, "application/json"),
        }));

        var result = await client.GetWithErrorHandlingAsync("/x", new Payload("fb"), captured);

        result.Name.Should().Be("fb");
        captured.Entries.Should().ContainSingle(e => e.Message.Contains("JSON deserialization"));
    }

    [Fact]
    public async Task GetWithErrorHandling_propagates_caller_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancellationToken = cts.Token;
        var client = ClientFor((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var act = async () => await client.GetWithErrorHandlingAsync(
            "/x", new Payload("fb"), NullLogger.Instance, ct: cancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetWithErrorHandling_logs_at_requested_level()
    {
        var captured = new CapturedLogger();
        var client = ClientFor((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));

        await client.GetWithErrorHandlingAsync("/x", new Payload("fb"), captured, errorLogLevel: LogLevel.Warning);

        captured.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task PostWithErrorHandling_sends_body_and_returns_default_on_failure()
    {
        var captured = new CapturedLogger();
        HttpRequestMessage? seen = null;
        var client = ClientFor((req, _) =>
        {
            seen = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        var result = await client.PostWithErrorHandlingAsync(
            "/x", new Payload("send"), new Payload("fallback"), captured);

        result.Name.Should().Be("fallback");
        seen.Should().NotBeNull();
        seen!.Method.Should().Be(HttpMethod.Post);
        captured.Entries.Should().ContainSingle();
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Verb_helpers_swallow_5xx(string verb)
    {
        var client = ClientFor((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var fb = new Payload("fb");

        var result = verb switch
        {
            "PUT" => await client.PutWithErrorHandlingAsync("/x", new Payload("u"), fb, NullLogger.Instance),
            "PATCH" => await client.PatchWithErrorHandlingAsync("/x", new Payload("u"), fb, NullLogger.Instance),
            "DELETE" => await client.DeleteWithErrorHandlingAsync("/x", fb, NullLogger.Instance),
            _ => throw new InvalidOperationException(),
        };

        result.Name.Should().Be("fb");
    }

    [Fact]
    public async Task GetStreamWithErrorHandling_returns_stream_on_success()
    {
        var bodyBytes = "hello"u8.ToArray();
        var client = ClientFor((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bodyBytes),
        }));

        await using var stream = await client.GetStreamWithErrorHandlingAsync("/x", NullLogger.Instance);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();

        text.Should().Be("hello");
    }

    [Fact]
    public async Task GetStreamWithErrorHandling_returns_Stream_Null_on_failure()
    {
        var captured = new CapturedLogger();
        var client = ClientFor((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        await using var stream = await client.GetStreamWithErrorHandlingAsync("/x", captured);

        stream.Should().BeSameAs(Stream.Null);
        captured.Entries.Should().ContainSingle();
    }

    private sealed class CapturedLogger : ILogger
    {
        public record Entry(LogLevel Level, string Message);
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new Entry(logLevel, formatter(state, exception)));
    }
}
