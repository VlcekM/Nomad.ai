using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using Nomad.Mobile.Hub;

namespace Nomad.Mobile.Android;

/// <summary>
/// The app's one activity. It also opens for <c>nomad://enroll</c> links: the phone's camera app reads the QR code from Settings ›
/// Hub › Add phone and Android hands the link here, so the app needs no camera code of its own. Single task, so a link opened
/// while the app runs goes to the running app.
/// </summary>
[Activity(
    Name = "ai.nomad.mobile.MainActivity",
    Label = "Nomad",
    Theme = "@style/NomadTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "nomad",
    DataHost = "enroll")]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        AppServices.Store = new AndroidSecretStore(this);
        AppServices.DefaultDeviceName = DeviceName();
#if NOMAD_EMULATOR_TEST
        EnrollLink.PlainHttpAllowed = url => url.Host == "10.0.2.2";
#endif
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        OpenLink(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        OpenLink(intent);
    }

    private static void OpenLink(Intent? intent)
    {
        if (intent?.Action == Intent.ActionView && intent.DataString is { Length: > 0 } link)
        {
            App.OpenLink(link);
        }
    }

    /// <summary>The name the user gave the phone ("Jame's S21"), else its model.</summary>
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
