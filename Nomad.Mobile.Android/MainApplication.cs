using Android.App;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Nomad.Mobile.Hub;

namespace Nomad.Mobile.Android;

/// <summary>
/// The Android application, which starts Avalonia (Avalonia 12 builds the app here, not in the activity) with the phone's own
/// services: secrets, the device's name, the installer, saving files and the release feed.
/// </summary>
[Application]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    /// <summary>
    /// Made by Android.
    /// </summary>
    protected MainApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        AppServices.Store = new AndroidSecretStore(this);
        AppServices.DefaultDeviceName = DeviceName();
        AppServices.Installer = new AndroidInstaller(this);
        AppServices.Files = new AndroidFileSaver(this);
        AppServices.ReleaseFeed = GitHubRelease.LatestAsync;
#if NOMAD_EMULATOR_TEST
        EnrollLink.PlainHttpAllowed = url => url.Host == "10.0.2.2";
#endif
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }

    /// <summary>
    /// The name the user gave the phone ("Anna's Pixel"), else its model.
    /// </summary>
    private string DeviceName()
    {
        try
        {
            if (global::Android.Provider.Settings.Global.GetString(ContentResolver, "device_name") is { Length: > 0 } name)
            {
                return name;
            }
        }
        catch (Java.Lang.Exception)
        {
            // Some phones keep it elsewhere; the model will do.
        }

        return Build.Model is { Length: > 0 } model ? model : "Phone";
    }
}
