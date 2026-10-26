using Avalonia;
using Klodi.App.Infrastructure;
using Klodi.Core.Process;
using Klodi.Core.Settings;

namespace Klodi.App;

internal static class Program
{
    /// <summary>Command-line test hooks (all inactive in normal use).</summary>
    public static DevOptions Dev { get; private set; } = new();

    /// <summary>Settings, loaded before Avalonia starts because they choose the renderer.</summary>
    public static KlodiSettings Settings { get; private set; } = new();

    public static string SettingsPath { get; private set; } = SettingsStore.DefaultPath;

    /// <summary>The renderer this run uses (the setting may change later; it applies after a restart).</summary>
    public static bool HardwareAccelerated { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Dev = DevOptions.Parse(args);
        SettingsPath = Dev.SettingsPath ?? SettingsStore.DefaultPath;
        Settings = SettingsStore.Load(SettingsPath);
        HardwareAccelerated = Settings.HardwareAcceleration;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Every claude process this app started dies with it (the job object would also do this).
            ClaudeCliProcess.KillAllOwned();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Popups are drawn inside the window, so menus show up in --dev-screenshot captures.
            .With(new Win32PlatformOptions
            {
                OverlayPopups = true,
                // Software rendering unless the user turns the GPU on: for this mostly-text window it
                // costs little CPU and saves roughly 80 MB that ANGLE and the Direct3D driver commit.
                RenderingMode = HardwareAccelerated
                    ? [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]
                    : [Win32RenderingMode.Software],
            })
            .WithInterFont()
            .LogToTrace();
}
