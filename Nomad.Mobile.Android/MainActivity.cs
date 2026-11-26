using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
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
        try
        {
            base.OnCreate(savedInstanceState);
        }
        catch (Exception ex)
        {
            // Android's own crash report shows only the outer exception; the whole chain goes to the log ("adb logcat -s Nomad").
            global::Android.Util.Log.Error("Nomad", ex.ToString());
            throw;
        }

        OpenLink(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        OpenLink(intent);
    }

    /// <summary>
    /// The activity keeps running through a light/dark switch (it handles UiMode itself, so the app isn't torn down); the app's
    /// theme follows the phone's here.
    /// </summary>
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        var night = (newConfig.UiMode & UiMode.NightMask) == UiMode.NightYes;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current is { } app)
            {
                app.RequestedThemeVariant = night ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            }
        });
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
