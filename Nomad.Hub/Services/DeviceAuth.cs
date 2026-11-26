using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nomad.Hub.Data;

namespace Nomad.Hub.Services;

/// <summary>A device's secret as a bearer token (or <c>access_token</c> on the SignalR URL), checked against its hash.</summary>
public sealed class DeviceAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, HubDb db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Device";

    private static readonly TimeSpan SeenGranularity = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? secret = null;
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            secret = header["Bearer ".Length..].Trim();
        }
        else if (Request.Path.StartsWithSegments("/hubs") && Request.Query.TryGetValue("access_token", out var token))
        {
            secret = token.ToString();
        }

        if (string.IsNullOrEmpty(secret))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = Secrets.Hash(secret);
        var device = await db.Devices.SingleOrDefaultAsync(d => d.SecretHash == hash && !d.Revoked, Context.RequestAborted);
        if (device is null)
        {
            return AuthenticateResult.Fail("Unknown or revoked device.");
        }

        var now = DateTime.UtcNow;
        if (now - device.LastSeenUtc > SeenGranularity)
        {
            await db.Devices.Where(d => d.Id == device.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenUtc, now), Context.RequestAborted);
        }

        List<Claim> claims = [new Claim(ClaimTypes.NameIdentifier, device.Id.ToString()), new Claim(ClaimTypes.Name, device.Name)];
        if (device.IsViewer)
        {
            claims.Add(new Claim(DeviceClaims.Viewer, "true"));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public static class DeviceClaims
{
    public static Guid DeviceId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static string DeviceName(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Name) ?? "";

    public const string Viewer = "nomad:viewer";

    /// <summary>A phone: it reads the board and chats and sends them input, and may do nothing else (docs/mobile.md §6).</summary>
    public static bool IsViewer(this ClaimsPrincipal user) => user.HasClaim(Viewer, "true");
}

/// <summary>Marks an API endpoint a phone may call; a phone gets 403 from every other.</summary>
public sealed class ViewerAllowed;
