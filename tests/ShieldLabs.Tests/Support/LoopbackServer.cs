using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ShieldLabs.Tests.Support;

/// <summary>
/// A one-request HTTP server on 127.0.0.1 that records the raw request line, so tests can check
/// the exact bytes the SDK puts on the wire (after <see cref="Uri"/> processing).
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public LoopbackServer()
    {
        _listener.Start();
    }

    public string BaseUrl => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Accepts one connection, answers 200 with <paramref name="body"/> and returns the request line.</summary>
    public async Task<string> AnswerOnceAsync(string body, CancellationToken cancellationToken)
    {
        using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        var head = new StringBuilder();
        var buffer = new byte[4096];
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        var payload = Encoding.UTF8.GetBytes(body);
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
            + payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        return head.ToString().Split("\r\n")[0];
    }

    public void Dispose() => _listener.Stop();
}
