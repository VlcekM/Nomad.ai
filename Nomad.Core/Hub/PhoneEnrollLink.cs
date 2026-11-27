namespace Nomad.Core.Hub;

/// <summary>
/// The link a phone opens to join the hub (docs/mobile.md §4): <c>nomad://enroll?hub=&lt;address&gt;&amp;token=&lt;token&gt;</c>,
/// both values URL-encoded. Settings › Hub shows it as text and as a QR code; Android hands it to the Nomad app.
/// </summary>
public static class PhoneEnrollLink
{
    public static string Make(string hubUrl, string token) =>
        $"nomad://enroll?hub={Uri.EscapeDataString(hubUrl)}&token={Uri.EscapeDataString(token)}";

    /// <summary>Whether the phone can't use this address as it is: the app requires HTTPS, as Android refuses plain HTTP.</summary>
    public static bool NeedsHttps(string hubUrl) =>
        !Uri.TryCreate(hubUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps;
}
