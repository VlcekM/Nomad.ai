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

    /// <summary>Nothing changed since the version the phone already has.</summary>
    public static PhoneResponse NotModified { get; } = new(304, "text/plain", []);
}

/// <summary>
/// The phone view's web server: plain HTTP/1.1 on 127.0.0.1 only, one request per connection. Reaching it from a phone is
/// the user's own choice (a tunnel or a private network that forwards the port); the server itself never listens beyond this PC.
/// Every request except the page itself must carry the access key in the <c>X-Nomad-Key</c> header, checked from the request
/// head before any body is read, so whatever carries the port there carries nothing useful to anyone without the link.
/// </summary>
public sealed class PhoneHttpServer : IDisposable
{
    /// <summary>The largest request body accepted (a typed message).</summary>
    public const int MaxBody = 256 * 1024;

    /// <summary>The largest request head accepted.</summary>
    private const int MaxHead = 16 * 1024;

    /// <summary>Connections served at once; more are closed at once rather than queued.</summary>
    private const int MaxConnections = 16;

    /// <summary>How long a client has to send its request, and to take the response.</summary>
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a handler may take before the client is told the work is still going on.</summary>
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener;
    private readonly Func<PhoneRequest, Task<PhoneResponse>> _handle;
    private readonly byte[] _key;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);

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

    /// <summary>Whether <paramref name="headers"/> carry the access key in <c>X-Nomad-Key</c>.</summary>
    private bool IsAuthorized(IReadOnlyDictionary<string, string> headers) =>
        headers.TryGetValue("x-nomad-key", out var given) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), _key);

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

            if (!_slots.Wait(0))
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ServeAsync(client);
                }
                finally
                {
                    _slots.Release();
                }
            });
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var response = await RespondAsync(stream);
                using var write = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                write.CancelAfter(IoTimeout);
                await WriteAsync(stream, response, write.Token);
            }
            catch (Exception)
            {
                // A dropped or slow connection ends that one request; the server carries on.
            }
        }
    }

    private async Task<PhoneResponse> RespondAsync(Stream stream)
    {
        PhoneRequest request;
        using (var read = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            read.CancelAfter(IoTimeout);
            var head = await ReadHeadAsync(stream, read.Token);
            if (head.Error is { } error)
            {
                return error;
            }

            if (head.Path != "/" && !IsAuthorized(head.Headers!))
            {
                return PhoneResponse.Text(401, "This link is missing its key, or the key changed. Copy the link from Nomad's settings again.");
            }

            var body = await ReadBodyAsync(stream, head, read.Token);
            if (body is null)
            {
                return PhoneResponse.Text(400, "Bad request");
            }

            request = new PhoneRequest(head.Method!, head.Path!, head.Query!, head.Headers!, body);
        }

        var handling = _handle(request);
        if (await Task.WhenAny(handling, Task.Delay(HandlerTimeout, _stop.Token)) != handling)
        {
            return PhoneResponse.Text(504, "Nomad is still working on that. Look at the chat before trying again.");
        }

        return await handling;
    }

    /// <summary>A parsed request head, or the response that refuses it; <see cref="Rest"/> is body bytes read with the head.</summary>
    internal sealed record Head(string? Method, string? Path, Dictionary<string, string>? Query, Dictionary<string, string>? Headers,
        int Length, byte[] Rest, PhoneResponse? Error);

    private static Head Refuse(int status, string text) => new(null, null, null, null, 0, [], PhoneResponse.Text(status, text));

    /// <summary>Reads and parses the request head.</summary>
    internal static async Task<Head> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHead];
        var filled = 0;
        var headEnd = -1;
        while (headEnd < 0)
        {
            if (filled == buffer.Length)
            {
                return Refuse(431, "Request head too large");
            }

            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0)
            {
                return Refuse(400, "Bad request");
            }

            filled += read;
            headEnd = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, headEnd).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return Refuse(400, "Bad request");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return Refuse(400, "Bad request");
            }

            var name = line[..colon].Trim();
            if (!headers.TryAdd(name, line[(colon + 1)..].Trim()) && name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(400, "Bad request");
            }
        }

        if (headers.ContainsKey("transfer-encoding"))
        {
            return Refuse(411, "Send the body with a Content-Length.");
        }

        var length = 0;
        if (headers.TryGetValue("content-length", out var cl))
        {
            if (!int.TryParse(cl, System.Globalization.NumberStyles.None, null, out length))
            {
                return Refuse(400, "Bad request");
            }

            if (length > MaxBody)
            {
                return Refuse(413, "Request too large");
            }
        }

        var target = first[1];
        var q = target.IndexOf('?');
        string path;
        try
        {
            path = Uri.UnescapeDataString(q < 0 ? target : target[..q]);
        }
        catch (UriFormatException)
        {
            return Refuse(400, "Bad request");
        }

        var rest = buffer.AsSpan(headEnd + 4, filled - (headEnd + 4)).ToArray();
        return new Head(first[0].ToUpperInvariant(), path, ParseQuery(q < 0 ? "" : target[(q + 1)..]), headers, length, rest, null);
    }

    /// <summary>Reads the body the head announced; null when the connection ends first or sends more than announced.</summary>
    internal static async Task<byte[]?> ReadBodyAsync(Stream stream, Head head, CancellationToken ct)
    {
        if (head.Rest.Length > head.Length)
        {
            return null;
        }

        var body = new byte[head.Length];
        head.Rest.CopyTo(body, 0);
        var filled = head.Rest.Length;
        while (filled < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(filled), ct);
            if (read == 0)
            {
                return null;
            }

            filled += read;
        }

        return body;
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            try
            {
                var name = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
                values[name] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                // A malformed pair is skipped.
            }
        }

        return values;
    }

    private static async Task WriteAsync(Stream stream, PhoneResponse response, CancellationToken ct)
    {
        var reason = response.Status switch
        {
            200 => "OK",
            204 => "No Content",
            304 => "Not Modified",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            409 => "Conflict",
            411 => "Length Required",
            413 => "Payload Too Large",
            431 => "Request Header Fields Too Large",
            504 => "Gateway Timeout",
            _ => "Error",
        };
        var head = $"HTTP/1.1 {response.Status} {reason}\r\nContent-Type: {response.ContentType}\r\nContent-Length: {response.Body.Length}\r\n" +
                   "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\n" +
                   "Content-Security-Policy: default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; " +
                   "frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(response.Body, ct);
        await stream.FlushAsync(ct);
    }
}
