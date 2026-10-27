using Avalonia;
using Nomad.App.Infrastructure;
using Nomad.Core.Process;
using Nomad.Core.Settings;

namespace Nomad.App;

internal static class Program
{
    /// <summary>Command-line test hooks (all inactive in normal use).</summary>
    public static DevOptions Dev { get; private set; } = new();

    /// <summary>Settings, loaded before Avalonia starts.</summary>
    public static NomadSettings Settings { get; private set; } = new();

    public static string SettingsPath { get; private set; } = SettingsStore.DefaultPath;

    /// <summary>This process as the one Nomad.ai of the user (null for scripted <c>--dev-*</c> runs).</summary>
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // First: Velopack runs its install, update and uninstall hooks here and may exit; an update downloaded
        // earlier but not applied yet is applied now and Nomad restarts into it (UpdateService).
        Velopack.VelopackApp.Build().Run();

        // Already running: that window comes to the front instead.
        if (!args.Any(a => a.StartsWith("--dev-", StringComparison.Ordinal)))
        {
            Instance = SingleInstance.Claim();
            if (Instance is null)
            {
                return 0;
            }
        }

        Dev = DevOptions.Parse(args);
        SettingsPath = Dev.SettingsPath ?? SettingsStore.DefaultPath;
        Settings = SettingsStore.Load(SettingsPath);
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Every claude process this app started dies with it (the job object would also do this).
            ClaudeCliProcess.KillAllOwned();
            Instance?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Popups are drawn inside the window, so menus show up in --dev-screenshot captures.
            .With(new Win32PlatformOptions
            {
                OverlayPopups = true,
                // GPU through ANGLE (Direct3D 11): far faster than Skia's software renderer. Native
                // OpenGL and Vulkan commit 2-3x more memory. Software only when no GPU is usable.
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            })
            .WithInterFont()
            .LogToTrace();
}
