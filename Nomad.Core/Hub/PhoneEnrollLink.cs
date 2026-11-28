namespace Nomad.Core.Hub;

/// <summary>
/// Enrollment links for Nomad Hub:
/// <list type="bullet">
/// <item><c>https://&lt;hub&gt;/enroll/&lt;token&gt;</c> (a one-time link through the hub; works in any camera, browser or chat app)</item>
/// <item><c>nomad://enroll?hub=&lt;address&gt;&amp;token=&lt;token&gt;</c> (deep link handed directly to the Nomad app)</item>
/// </list>
/// </summary>
public static class PhoneEnrollLink
{
    public static string Make(string hubUrl, string token) =>
        $"nomad://enroll?hub={Uri.EscapeDataString(hubUrl)}&token={Uri.EscapeDataString(token)}";

    /// <summary>A one-time enrollment link hosted by the hub itself: <c>https://&lt;hub&gt;/enroll/&lt;token&gt;</c>.</summary>
    public static string MakeHubLink(string hubUrl, string token) =>
        hubUrl.Trim().TrimEnd('/') + "/enroll/" + Uri.EscapeDataString(token.Trim());

    /// <summary>
    /// Parses either a <c>nomad://enroll?hub=...&amp;token=...</c> deep link or a hub enrollment URL
    /// (<c>https://&lt;hub&gt;/enroll/&lt;token&gt;</c>), returning the hub base URL and token.
    /// </summary>
    public static (string Hub, string Token)? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme.Equals("nomad", StringComparison.OrdinalIgnoreCase) && uri.Host.Equals("enroll", StringComparison.OrdinalIgnoreCase))
        {
            string? hub = null, token = null;
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var at = pair.IndexOf('=');
                var key = at < 0 ? pair : pair[..at];
                var value = at < 0 ? "" : Uri.UnescapeDataString(pair[(at + 1)..].Replace('+', ' '));
                if (key == "hub") hub = value;
                else if (key == "token") token = value;
            }

            return string.IsNullOrWhiteSpace(hub) || string.IsNullOrWhiteSpace(token) ? null : (hub.Trim(), token.Trim());
        }

        if (uri.Scheme is "http" or "https" && uri.Host.Length > 0)
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            const string marker = "/enroll/";
            var idx = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var rawToken = path[(idx + marker.Length)..];
                if (rawToken.Length > 0 && !rawToken.Contains('/'))
                {
                    var basePath = path[..idx];
                    var authority = uri.GetLeftPart(UriPartial.Authority);
                    var hubBase = authority + basePath + "/";
                    var token = Uri.UnescapeDataString(rawToken).Trim();
                    if (token.Length > 0)
                    {
                        return (hubBase, token);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Whether the phone can't use this address as it is: the app requires HTTPS, as Android refuses plain HTTP.</summary>
    public static bool NeedsHttps(string hubUrl) =>
        !Uri.TryCreate(hubUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps;
}
