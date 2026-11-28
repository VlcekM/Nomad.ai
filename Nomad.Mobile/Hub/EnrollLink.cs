using Nomad.Core.Hub;

namespace Nomad.Mobile.Hub;

/// <summary>
/// Enrollment links Settings › Hub › Add phone shows as a QR code or link: either a hub one-time URL
/// (<c>https://&lt;hub&gt;/enroll/&lt;token&gt;</c>) or a direct <c>nomad://enroll?hub=&lt;address&gt;&amp;token=&lt;token&gt;</c> link.
/// </summary>
public static class EnrollLink
{
    /// <summary>The hub address and token in a link, or null if it isn't one.</summary>
    public static (string Hub, string Token)? TryParse(string? text) => PhoneEnrollLink.TryParse(text);

    /// <summary>Plain HTTP the platform allows anyway: only an emulator test build sets it, for the PC the emulator runs on.</summary>
    public static Func<Uri, bool>? PlainHttpAllowed { get; set; }

    /// <summary>
    /// The hub address as a URL the phone may use, or why not. Android refuses plain HTTP, so the address must be HTTPS (a reverse
    /// proxy, or Tailscale's <c>tailscale serve --https</c>). A bare host name gets <c>https://</c> in front.
    /// </summary>
    public static (Uri? Url, string? Problem) CheckAddress(string? address)
    {
        var text = address?.Trim() ?? "";
        if (text.Length == 0)
        {
            return (null, "Enter the hub's address.");
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Host.Length == 0)
        {
            return (null, "That isn't a web address.");
        }

        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && PlainHttpAllowed?.Invoke(url) == true))
        {
            return (null, "The phone needs the hub over HTTPS. Put it behind a reverse proxy, or use Tailscale's \"tailscale serve --https\".");
        }

        // "https://example.com/nomad" is the hub at /nomad/: its API and connection paths go under it.
        return (url.AbsolutePath.EndsWith('/') ? url : new Uri(url.GetLeftPart(UriPartial.Path) + "/"), null);
    }
}
