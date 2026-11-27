using Avalonia.Threading;
using Nomad.Mobile.Hub;

namespace Nomad.Mobile;

/// <summary>A screen with something to let go of when it stops showing (a watched chat, subscriptions).</summary>
public interface IClosable
{
    void Close();
}

/// <summary>What the platform head (Android) gives the app; the tests set their own.</summary>
public static class AppServices
{
    public static ISecretStore Store { get; set; } = new MemorySecretStore();

    public static IHubFactory Hubs { get; set; } = new HubFactory();

    /// <summary>The name a new enrollment suggests for this phone (its model, on Android).</summary>
    public static string DefaultDeviceName { get; set; } = "Phone";

    /// <summary>Runs work on the UI thread. Hub events arrive on any thread; the tests run it at once.</summary>
    public static Action<Action> Post { get; set; } = work => Dispatcher.UIThread.Post(work);

    /// <summary>Installs updates of this app; null (the tests' default) offers none.</summary>
    public static IAppInstaller? Installer { get; set; }

    /// <summary>Where an update comes from when the hub offers none (the GitHub release, on Android); null looks nowhere else.</summary>
    public static Func<CancellationToken, Task<AppOffer?>>? ReleaseFeed { get; set; }

    /// <summary>Copies text to the clipboard, where the platform has one.</summary>
    public static Func<string, Task>? CopyText { get; set; }
}
