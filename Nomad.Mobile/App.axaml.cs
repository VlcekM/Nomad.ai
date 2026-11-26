using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Nomad.Mobile.ViewModels;
using Nomad.Mobile.Views;

namespace Nomad.Mobile;

public partial class App : Application
{
    /// <summary>The running app's model, once started; the Android head hands it links and lifecycle changes.</summary>
    public static MainViewModel? Main { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Main = new MainViewModel(AppServices.Store, AppServices.Hubs);
        var view = new MainView { DataContext = Main };
        switch (ApplicationLifetime)
        {
            case ISingleViewApplicationLifetime single:
                single.MainView = view;
                break;
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new Window { Content = view, Width = 412, Height = 900, Title = "Nomad" };
                break;
        }

        // Back from the background: Android may have closed the connection meanwhile.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Background)
                {
                    Main.OnResumed();
                }
            };
        }

        Main.Start();
        if (PendingLink is { } link)
        {
            PendingLink = null;
            Main.HandleLink(link);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>A link that arrived before the app finished starting (it opened from the camera app).</summary>
    public static string? PendingLink { get; set; }

    /// <summary>A <c>nomad://enroll</c> link, now or once the app has started.</summary>
    public static void OpenLink(string link)
    {
        if (Main is { } main)
        {
            main.HandleLink(link);
        }
        else
        {
            PendingLink = link;
        }
    }
}
