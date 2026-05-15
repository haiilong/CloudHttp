using System.Net;
using System.Text;

namespace CloudHttp.Tests;

/// <summary>
/// Test handler whose response is supplied per instance. Useful for simulating
/// HTTP responses without sending network requests.
/// </summary>
internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    private int _invocationCount;

    public int InvocationCount => _invocationCount;

    public static StubHttpMessageHandler ForStatus(HttpStatusCode code, string? jsonBody = null) =>
        new((_, _) =>
        {
            var message = new HttpResponseMessage(code);

            if (jsonBody is not null)
            {
                message.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(message);
        });

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _invocationCount);
        return responder(request, cancellationToken);
    }
}
