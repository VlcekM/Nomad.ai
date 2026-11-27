using System.Text.Json;
using Nomad.Core;

namespace Nomad.Mobile.Hub;

/// <summary>
/// The app as installed: its version as the release names it ("2.9.0"), Android's version code (20900), and when this copy was
/// installed (so an update to another build of the same version shows).
/// </summary>
public sealed record InstalledApp(string Version, long VersionCode, DateTime? InstalledUtc = null);

/// <summary>
/// Installing a newer copy of this app (Android's package installer). The tests use a fake; without one the app offers no updates.
/// </summary>
public interface IAppInstaller
{
    InstalledApp Installed { get; }

    /// <summary>The SHA-256 of the installed app's file (lowercase hex), or null when it can't be read.</summary>
    Task<string?> InstalledSha256Async();

    /// <summary>Where a downloaded update is kept until it is installed (the app's cache).</summary>
    string DownloadPath { get; }

    /// <summary>Whether Android lets this app install apps (Settings › Apps › Install unknown apps › Nomad).</summary>
    bool CanInstall { get; }

    /// <summary>Opens Android's page with that switch for this app.</summary>
    void AskToInstall();

    /// <summary>
    /// Hands the file to Android's installer. It asks the user to confirm, unless Android lets the app update itself (from
    /// Android 12 on). On success Android ends the app and installs the new copy.
    /// </summary>
    Task InstallAsync(string apkPath, CancellationToken ct = default);

    /// <summary>
    /// Android's installer ended without installing: what it said, and whether it refused the file itself (older, signed by
    /// someone else, not for this phone) rather than being cancelled. Raised on any thread.
    /// </summary>
    event Action<string, bool>? Failed;
}

/// <summary>
/// An update on offer: where from ("the hub" or "GitHub"), its version, size and SHA-256 when known, when it was published, and
/// its file to download.
/// </summary>
public sealed record AppOffer(
    string From, string Version, long VersionCode, long Size, string? Sha256, DateTime? PublishedUtc, Func<CancellationToken, Task<Stream>> Open);

/// <summary>
/// The newest GitHub release's phone app (<c>Nomad-android.apk</c>), for a hub that offers none. Its version code comes from the
/// release's tag as the build makes it from the version (v2.9.0 is 20900); a release without the APK offers nothing.
/// </summary>
public static class GitHubRelease
{
    private const string AssetName = "Nomad-android.apk";

    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Nomad-Mobile");
        return http;
    }

    public static async Task<AppOffer?> LatestAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{NomadInfo.Repository}/releases/latest", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (VersionCodeOf(tag) is not { } code || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) && name.GetString() == AssetName
                && asset.TryGetProperty("browser_download_url", out var url) && url.GetString() is { Length: > 0 } link)
            {
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                // "sha256:<hex>", which GitHub works out for each asset.
                var sha = asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.Ordinal)
                    ? digest["sha256:".Length..] : null;
                DateTime? published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var when) ? when.ToUniversalTime() : null;
                return new AppOffer("GitHub", tag.TrimStart('v'), code, size, sha, published,
                    async token => await Http.GetStreamAsync(link, token));
            }
        }

        return null;
    }

    /// <summary>"v2.9.0" or "2.9.0-beta.1" as Android's version code: 20900, as Nomad.Mobile.Android.csproj makes it.</summary>
    public static long? VersionCodeOf(string tag)
    {
        var core = tag.TrimStart('v').Split('-', '+')[0];
        return Version.TryParse(core, out var v) && v.Build >= 0 ? v.Major * 10000L + v.Minor * 100L + v.Build : null;
    }
}
