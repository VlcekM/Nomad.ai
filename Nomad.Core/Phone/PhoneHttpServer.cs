using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Nomad.Core.Phone;

/// <summary>A request to the phone view: the method, the path without its query, the query values, and the body.</summary>
public sealed record PhoneRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>What the phone view answers.</summary>
public sealed record PhoneResponse(int Status, string ContentType, byte[] Body)
{
    public static PhoneResponse Text(int status, string text) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    public static PhoneResponse Json(byte[] json) => new(200, "application/json", json);

    public static PhoneResponse Html(string html) => new(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
}

/// <summary>
/// The phone view's web server: plain HTTP/1.1 on 127.0.0.1 only, one request per connection. Reaching it from a phone is
/// the user's own choice (a tunnel or a private network that forwards the port); the server itself never listens beyond this PC.
/// Every request except the page itself must carry the access key, so whatever carries the port there carries nothing
/// useful to anyone without the link.
/// </summary>
public sealed class PhoneHttpServer : IDisposable
{
    /// <summary>The largest request body accepted (a typed message).</summary>
    public const int MaxBody = 256 * 1024;

    /// <summary>The largest request head accepted.</summary>
    private const int MaxHead = 16 * 1024;

    private readonly TcpListener _listener;
    private readonly Func<PhoneRequest, Task<PhoneResponse>> _handle;
    private readonly byte[] _key;
    private readonly CancellationTokenSource _stop = new();

    private PhoneHttpServer(int port, string key, Func<PhoneRequest, Task<PhoneResponse>> handle)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _key = Encoding.UTF8.GetBytes(key);
        _handle = handle;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// Starts listening on 127.0.0.1:<paramref name="port"/> (0 picks a free port); throws <see cref="SocketException"/>
    /// when the port is taken.
    /// </summary>
    public static PhoneHttpServer Start(int port, string key, Func<PhoneRequest, Task<PhoneResponse>> handle)
    {
        var server = new PhoneHttpServer(port, key, handle);
        server._listener.Start();
        _ = server.AcceptLoopAsync();
        return server;
    }

    /// <summary>A new random access key, safe in a URL.</summary>
    public static string NewKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Whether <paramref name="request"/> carries the access key, in the <c>X-Nomad-Key</c> header or a <c>key</c> query value.
    /// </summary>
    public bool IsAuthorized(PhoneRequest request)
    {
        var given = request.Header("x-nomad-key") ?? (request.Query.TryGetValue("key", out var k) ? k : null);
        return given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), _key);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var stream = client.GetStream();
                var request = await ReadAsync(stream, timeout.Token);
                var response = request is null
                    ? PhoneResponse.Text(400, "Bad request")
                    : request.Path == "/" || IsAuthorized(request)
                        ? await _handle(request)
                        : PhoneResponse.Text(401, "This link is missing its key, or the key changed. Copy the link from Nomad's settings again.");
                await WriteAsync(stream, response, timeout.Token);
            }
            catch (Exception)
            {
                // A dropped connection or a handler failure ends that one request; the server carries on.
            }
        }
    }

    /// <summary>Reads one request; null when it is malformed or too large.</summary>
    internal static async Task<PhoneRequest?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHead];
        var filled = 0;
        var headEnd = -1;
        while (headEnd < 0)
        {
            if (filled == buffer.Length)
            {
                return null;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0)
            {
                return null;
            }

            filled += read;
            headEnd = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, headEnd).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
        if (length < 0 || length > MaxBody)
        {
            return null;
        }

        var body = new byte[length];
        var already = Math.Min(filled - (headEnd + 4), length);
        Array.Copy(buffer, headEnd + 4, body, 0, already);
        while (already < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(already), ct);
            if (read == 0)
            {
                return null;
            }

            already += read;
        }

        var target = first[1];
        var q = target.IndexOf('?');
        var path = Uri.UnescapeDataString(q < 0 ? target : target[..q]);
        return new PhoneRequest(first[0].ToUpperInvariant(), path, ParseQuery(q < 0 ? "" : target[(q + 1)..]), headers, body);
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            values[name] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }

        return values;
    }

    private static async Task WriteAsync(Stream stream, PhoneResponse response, CancellationToken ct)
    {
        var reason = response.Status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            409 => "Conflict",
            _ => "Error",
        };
        var head = $"HTTP/1.1 {response.Status} {reason}\r\nContent-Type: {response.ContentType}\r\nContent-Length: {response.Body.Length}\r\n" +
                   "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(response.Body, ct);
        await stream.FlushAsync(ct);
    }
}
