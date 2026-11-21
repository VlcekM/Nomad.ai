using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Nomad.Core.Phone;

namespace Nomad.Core.Tests;

public sealed class PhoneHttpServerTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static Task<PhoneResponse> Echo(PhoneRequest r) => Task.FromResult(r.Path == "/"
        ? PhoneResponse.Html("<p>page</p>")
        : PhoneResponse.Text(200, $"{r.Method} {r.Path} id={r.Query.GetValueOrDefault("id")} body={Encoding.UTF8.GetString(r.Body)}"));

    [Fact]
    public async Task ThePageNeedsNoKey()
    {
        using var server = PhoneHttpServer.Start(0, Key, Echo);
        using var http = new HttpClient();

        var page = await http.GetAsync($"http://127.0.0.1:{server.Port}/");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("<p>page</p>", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EverythingElseNeedsTheKey()
    {
        using var server = PhoneHttpServer.Start(0, Key, Echo);
        using var http = new HttpClient();

        var without = await http.GetAsync($"http://127.0.0.1:{server.Port}/api/chats");
        var wrong = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/api/chats");
        wrong.Headers.Add("X-Nomad-Key", "nope");

        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);
    }

    [Fact]
    public async Task PassesQueryAndBodyThrough()
    {
        using var server = PhoneHttpServer.Start(0, Key, Echo);
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/api/send?id=a%20b")
        {
            Content = new StringContent("{\"text\":\"héllo\"}", Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.Add("X-Nomad-Key", Key);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("POST /api/send id=a b body={\"text\":\"héllo\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ListensOnLoopbackOnly()
    {
        using var server = PhoneHttpServer.Start(0, Key, Echo);
        var others = (await Dns.GetHostAddressesAsync(Dns.GetHostName()))
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

        foreach (var address in others)
        {
            using var client = new System.Net.Sockets.TcpClient();
            await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(() => client.ConnectAsync(address, server.Port));
        }
    }

    [Fact]
    public async Task TheKeyIsNotTakenFromTheAddress()
    {
        using var server = PhoneHttpServer.Start(0, Key, Echo);
        using var http = new HttpClient();

        var response = await http.GetAsync($"http://127.0.0.1:{server.Port}/api/chats?key={Key}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("POST /api/send HTTP/1.1\r\nX-Nomad-Key: " + Key + "\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", "411")]
    [InlineData("POST /api/send HTTP/1.1\r\nX-Nomad-Key: " + Key + "\r\nContent-Length: 999999999\r\n\r\n", "413")]
    [InlineData("POST /api/send HTTP/1.1\r\nX-Nomad-Key: " + Key + "\r\nContent-Length: 2\r\nContent-Length: 5\r\n\r\nhi", "400")]
    [InlineData("POST /api/send HTTP/1.1\r\nContent-Length: 5\r\n\r\n", "401")]
    public async Task RefusesBadFramingAndChecksTheKeyBeforeTheBody(string raw, string status)
    {
        using var server = PhoneHttpServer.Start(0, Key, Echo);
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(raw));
        var reply = await new StreamReader(stream).ReadLineAsync();

        Assert.StartsWith($"HTTP/1.1 {status} ", reply);
    }

    [Fact]
    public async Task ThePageCannotBeFramed()
    {
        using var server = PhoneHttpServer.Start(0, Key, Echo);
        using var http = new HttpClient();

        var page = await http.GetAsync($"http://127.0.0.1:{server.Port}/");

        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", page.Content.Headers.Contains("Content-Security-Policy")
            ? page.Content.Headers.GetValues("Content-Security-Policy").Single()
            : page.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Theory]
    [InlineData("abcdef", 10, "abcdef")]
    [InlineData("abcdef", 3, "abc…")]
    [InlineData("ab\U0001F600cd", 3, "ab…")]
    [InlineData("ab\U0001F600cd", 4, "ab\U0001F600…")]
    public void ClipNeverSplitsASurrogatePair(string text, int max, string expected) => Assert.Equal(expected, PhoneText.Clip(text, max));

    [Fact]
    public void KeysAreRandomAndUrlSafe()
    {
        var a = PhoneHttpServer.NewKey();

        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.NotEqual(a, PhoneHttpServer.NewKey());
    }
}
