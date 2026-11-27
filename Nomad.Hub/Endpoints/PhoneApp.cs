using System.Security.Cryptography;
using System.Text.Json;
using Nomad.Hub.Contracts;
using Nomad.Hub.Services;

namespace Nomad.Hub;

/// <summary>
/// The phone app the hub hands to phones as an update (docs/mobile.md §5, "Updating the app"): <c>Nomad-android.apk</c> in the
/// data folder's <c>phone-app</c> folder, with the <c>Nomad-android.json</c> a build writes beside it (its version and Android
/// version code). Nothing is offered while there is no APK there.
/// </summary>
public sealed class PhoneAppStore(HubOptions options)
{
    public const string ApkName = "Nomad-android.apk";

    public const string InfoName = "Nomad-android.json";

    private readonly Lock _gate = new();
    private (long Length, DateTime Written, string Sha256)? _hashed;

    // Whole, so a relative data folder (Hub__DataPath=data) is found from the working folder, not served from the web root.
    public string Folder => Path.GetFullPath(Path.Combine(options.DataPath, "phone-app"));

    public string ApkPath => Path.Combine(Folder, ApkName);

    /// <summary>What is offered, or null when there is no APK. The hash is worked out once per file.</summary>
    public PhoneAppInfo? Current()
    {
        var apk = new FileInfo(ApkPath);
        if (!apk.Exists)
        {
            return null;
        }

        string sha;
        lock (_gate)
        {
            if (_hashed is not { } h || h.Length != apk.Length || h.Written != apk.LastWriteTimeUtc)
            {
                try
                {
                    using var stream = apk.OpenRead();
                    _hashed = (apk.Length, apk.LastWriteTimeUtc, Convert.ToHexStringLower(SHA256.HashData(stream)));
                }
                catch (IOException)
                {
                    return null; // Still being copied in.
                }
            }

            sha = _hashed.Value.Sha256;
        }

        var (version, code) = ReadInfo();
        return new PhoneAppInfo(version, code, apk.Length, sha, apk.LastWriteTimeUtc);
    }

    private (string Version, long VersionCode) ReadInfo()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, InfoName)));
            var root = doc.RootElement;
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var code = root.TryGetProperty("versionCode", out var c) && c.TryGetInt64(out var n) ? n : 0;
            return (version, code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ("", 0); // No version given: a phone offers it when the file differs from its own.
        }
    }
}

public static class PhoneAppEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/phone-app", (PhoneAppStore store) => store.Current() is { } info ? Results.Ok(info) : Results.NotFound())
            .WithMetadata(new ViewerAllowed());

        api.MapGet("/phone-app/apk", (PhoneAppStore store) => File.Exists(store.ApkPath)
                ? Results.File(store.ApkPath, "application/vnd.android.package-archive", PhoneAppStore.ApkName, enableRangeProcessing: true)
                : Results.NotFound())
            .WithMetadata(new ViewerAllowed());
    }
}
