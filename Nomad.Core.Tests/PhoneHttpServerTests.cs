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
    public void KeysAreRandomAndUrlSafe()
    {
        var a = PhoneHttpServer.NewKey();

        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.NotEqual(a, PhoneHttpServer.NewKey());
    }
}
