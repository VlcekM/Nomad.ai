using Avalonia;
using Klodi.App.Infrastructure;
using Klodi.Core.Process;

namespace Klodi.App;

internal static class Program
{
    /// <summary>Command-line test hooks (all inactive in normal use).</summary>
    public static DevOptions Dev { get; private set; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        Dev = DevOptions.Parse(args);
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
            .With(new Win32PlatformOptions { OverlayPopups = true })
            .WithInterFont()
            .LogToTrace();
}
