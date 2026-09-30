using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ShieldLabs.Tests.Support;

/// <summary>A recorded outgoing request (copied, because the SDK disposes the request message).</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers)
{
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;
}

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a queue of scripted responses.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _queue = new();
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _fallback;

    public List<RecordedRequest> Requests { get; } = new();

    public static HttpResponseMessage Response(int status, string body, string? contentType = "application/json", Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        if (contentType is not null)
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        configure?.Invoke(response);
        return response;
    }

    public FakeHttpHandler Enqueue(int status, string body, string? contentType = "application/json", Action<HttpResponseMessage>? configure = null)
    {
        _queue.Enqueue((_, _) => Task.FromResult(Response(status, body, contentType, configure)));
        return this;
    }

    public FakeHttpHandler Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _queue.Enqueue(responder);
        return this;
    }

    /// <summary>Answers every request not covered by the queue.</summary>
    public FakeHttpHandler Always(int status, string body, string? contentType = "application/json", Action<HttpResponseMessage>? configure = null)
    {
        _fallback = (_, _) => Task.FromResult(Response(status, body, contentType, configure));
        return this;
    }

    public FakeHttpHandler Always(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _fallback = responder;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers.NonValidated)
        {
            headers[header.Key] = header.Value.ToString();
        }

        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers));
        var responder = _queue.Count > 0 ? _queue.Dequeue() : _fallback;
        if (responder is null)
        {
            throw new InvalidOperationException("No scripted response for " + request.RequestUri);
        }

        return responder(request, cancellationToken);
    }
}
