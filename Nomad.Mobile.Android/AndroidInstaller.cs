using System.Security.Cryptography;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Nomad.Mobile.Hub;

namespace Nomad.Mobile.Android;

/// <summary>
/// Installs an update through Android's package installer: a session written from the downloaded file and committed. Android
/// asks the user to confirm unless it lets the app update itself (from Android 12, with UPDATE_PACKAGES_WITHOUT_USER_ACTION in
/// the manifest). Android answers <see cref="InstallStatusReceiver"/>:
/// the confirm screen to show, or a refusal. On success it has already ended the app to install the new copy, and the app stays
/// closed until opened again: Android doesn't let an app, or its installer's answer, open it from the background. Works on the
/// application's context, so it outlives any one activity.
/// </summary>
public sealed class AndroidInstaller : IAppInstaller
{
    private readonly Context _app;
    private string? _sha256;

    public AndroidInstaller(Context context)
    {
        _app = context.ApplicationContext ?? context;
        Current = this;
        try
        {
            // A download kept from before: installed by now, or no longer wanted.
            File.Delete(DownloadPath);
        }
        catch (IOException)
        {
        }
    }

    internal static AndroidInstaller? Current { get; private set; }

    /// <summary>Nomad's activity shows (MainActivity says so): Android's confirm screen can open over it now.</summary>
    internal static bool InForeground { get; set; }

    // Android's confirm screen, arrived while Nomad was in the background: it opens when Nomad comes back.
    private static Intent? _pendingConfirm;

    public InstalledApp Installed
    {
        get
        {
            var info = _app.PackageManager!.GetPackageInfo(_app.PackageName!, 0)!;
            return new InstalledApp(info.VersionName ?? "", OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode,
                DateTimeOffset.FromUnixTimeMilliseconds(info.LastUpdateTime).UtcDateTime);
        }
    }

    public Task<string?> InstalledSha256Async() => _sha256 is { } known ? Task.FromResult<string?>(known) : Task.Run(() =>
    {
        try
        {
            // The installed app's own file, as it was installed: the same bytes as the APK it came from.
            using var stream = File.OpenRead(_app.ApplicationInfo!.SourceDir!);
            return _sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception)
        {
            return null;
        }
    });

    public string DownloadPath => Path.Combine(_app.CacheDir!.AbsolutePath, "update", "Nomad-android.apk");

    public bool CanInstall => _app.PackageManager!.CanRequestPackageInstalls();

    public void AskToInstall()
    {
        var intent = new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources, global::Android.Net.Uri.Parse("package:" + _app.PackageName));
        intent.AddFlags(ActivityFlags.NewTask);
        _app.StartActivity(intent);
    }

    public event Action<string, bool>? Failed;

    public Task InstallAsync(string apkPath, CancellationToken ct = default) => Task.Run(() =>
    {
        var installer = _app.PackageManager!.PackageInstaller;
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(_app.PackageName);
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            // Silent where Android allows it (this app installed the copy it updates); Android asks otherwise.
            parameters.SetRequireUserAction((int)PackageInstallUserAction.NotRequired);
        }

        var id = installer.CreateSession(parameters);
        using var session = installer.OpenSession(id);
        try
        {
            var length = new FileInfo(apkPath).Length;
            using (var input = File.OpenRead(apkPath))
            using (var output = session.OpenWrite("nomad", 0, length))
            {
                input.CopyTo(output);
                session.Fsync(output);
            }

            // A broadcast, which reaches the app even in the background (an activity couldn't be opened there).
            var status = new Intent(_app, typeof(InstallStatusReceiver));
            var flags = PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
            var pending = PendingIntent.GetBroadcast(_app, id, status, flags)!;
            session.Commit(pending.IntentSender);
        }
        catch (Exception)
        {
            session.Abandon(); // Not left open in Android's installer.
            throw;
        }
    }, ct);

    /// <summary>
    /// Android's word on a session: the confirm screen to show (now over Nomad, or when Nomad next comes back), a refusal or
    /// cancellation to report, or success.
    /// </summary>
    internal static void HandleStatus(Context context, Intent intent)
    {
        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
        switch (status)
        {
            case PackageInstallStatus.PendingUserAction:
#pragma warning disable CA1422 // The typed overload is Android 13's; this one works on every version.
                if (intent.GetParcelableExtra(Intent.ExtraIntent) is Intent confirm)
#pragma warning restore CA1422
                {
                    confirm.AddFlags(ActivityFlags.NewTask);
                    if (InForeground)
                    {
                        _pendingConfirm = null;
                        context.StartActivity(confirm);
                    }
                    else
                    {
                        _pendingConfirm = confirm;
                    }
                }

                break;
            case PackageInstallStatus.Success:
                _pendingConfirm = null;
                break;
            default:
                _pendingConfirm = null;
                // Refused for the file itself (older, signed by someone else, not for this phone), not for the moment it was tried in.
                var refused = status is not (PackageInstallStatus.FailureAborted or PackageInstallStatus.FailureStorage
                    or PackageInstallStatus.FailureBlocked or PackageInstallStatus.FailureTimeout);
                Current?.Failed?.Invoke(Explain(status, intent.GetStringExtra(PackageInstaller.ExtraStatusMessage)), refused);
                break;
        }
    }

    private static string Explain(PackageInstallStatus status, string? message) => status switch
    {
        PackageInstallStatus.FailureAborted => "the install was cancelled.",
        PackageInstallStatus.FailureConflict =>
            "it is signed differently from the Nomad on this phone (a build from elsewhere). To switch, uninstall this one and install the new one, then connect it again.",
        PackageInstallStatus.FailureIncompatible =>
            "it doesn't fit this phone: it is older than the Nomad installed here, or built for another kind of phone.",
        PackageInstallStatus.FailureStorage => "the phone is short of space.",
        PackageInstallStatus.FailureBlocked => "the phone doesn't allow it.",
        _ when message?.Contains("VERSION_DOWNGRADE", StringComparison.Ordinal) == true => "it is older than the Nomad installed here.",
        _ => string.IsNullOrWhiteSpace(message) ? "it failed." : message.TrimEnd('.') + ".",
    };

    /// <summary>MainActivity resumed: a confirm screen that arrived meanwhile opens now.</summary>
    internal static void OnResumed(Activity activity)
    {
        InForeground = true;
        if (_pendingConfirm is { } confirm)
        {
            _pendingConfirm = null;
            activity.StartActivity(confirm);
        }
    }
}

/// <summary>What Android's installer says about a session, passed to <see cref="AndroidInstaller.HandleStatus"/>.</summary>
[BroadcastReceiver(Name = "ai.nomad.mobile.InstallStatusReceiver", Exported = false)]
public sealed class InstallStatusReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is not null && intent is not null)
        {
            AndroidInstaller.HandleStatus(context, intent);
        }
    }
}
