using System.Globalization;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Mobile.Hub;

namespace Nomad.Mobile.ViewModels;

/// <summary>
/// Updating the app (docs/mobile.md §5): the hub's phone app, else the newest GitHub release's, when it is newer than this one or
/// the same version built differently. Checked on connecting and when the app comes back, at most every half hour unless Settings
/// asks; Update downloads it, checks it against what was offered and hands it to Android's installer.
/// </summary>
public sealed partial class AppUpdateViewModel(MainViewModel main) : ObservableObject
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);

    private DateTime _checkedUtc = DateTime.MinValue;
    private bool _waitingForPermission;
    private IAppInstaller? _listening;

    // What Android refused to install (an older file, one signed by someone else): not offered again until the app restarts.
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);

    // The offer being installed, so Android's refusal is remembered against it.
    private AppOffer? _installing;

    private static IAppInstaller? Installer => AppServices.Installer;

    /// <summary>The platform can install updates (Android); elsewhere nothing of this shows.</summary>
    public bool IsSupported => Installer is not null;

    public string InstalledText => Installer?.Installed is not { } app ? ""
        : app.InstalledUtc is { } at ? $"{app.Version}, installed {at.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture)}" : app.Version;

    /// <summary>The update on offer; null when this app is the newest.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfferText), nameof(ShowsBar), nameof(UpToDateText), nameof(CanUpdate))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    public partial AppOffer? Offer { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate), nameof(ButtonText))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>What is happening now ("Downloading 12 of 58 MB…"), or what went wrong; null when nothing is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBar))]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool StatusIsProblem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpToDateText))]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpToDateText))]
    public partial bool HasChecked { get; set; }

    /// <summary>The chats screen's bar: an update to install, or how installing one went.</summary>
    public bool ShowsBar => IsSupported && (Offer is not null || Status is not null);

    public bool CanUpdate => Offer is not null && !IsBusy;

    public string ButtonText => IsBusy ? "Updating…" : "Update";

    public string OfferText => Offer is not { } o ? ""
        : $"Nomad {o.Version}{(o.PublishedUtc is { } t ? " (" + t.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture) + ")" : "")} is ready to install.";

    /// <summary>Settings' line when there is nothing to install.</summary>
    public string UpToDateText => Offer is not null ? "" : IsChecking ? "Checking for updates…" : HasChecked ? "This is the newest version." : "";

    /// <summary>Looks for an update: on connecting and coming back (at most every half hour), or now when <paramref name="force"/>.</summary>
    public async Task CheckAsync(bool force = false)
    {
        if (Installer is not { } installer || main.Connection is not { IsConnected: true } connection || IsBusy || IsChecking
            || (!force && DateTime.UtcNow - _checkedUtc < CheckEvery))
        {
            return;
        }

        Listen(installer);
        _checkedUtc = DateTime.UtcNow;
        IsChecking = true;
        try
        {
            var offer = await FindAsync(connection, installer);
            if (offer is { Sha256: { } sha } && _refused.Contains(sha))
            {
                offer = null;
            }

            if (ReferenceEquals(main.Connection, connection))
            {
                Offer = offer;
                HasChecked = true;
            }
        }
        catch (Exception)
        {
            // Nothing new is shown; the next check tries again.
        }
        finally
        {
            IsChecking = false;
        }
    }

    private static async Task<AppOffer?> FindAsync(IHubConnection connection, IAppInstaller installer)
    {
        var mine = installer.Installed;
        if (await connection.PhoneAppAsync() is { } hub)
        {
            var offer = new AppOffer("the hub", hub.Version is { Length: > 0 } v ? v : "update", hub.VersionCode, hub.Size, hub.Sha256, hub.PublishedUtc,
                ct => connection.PhoneAppDownloadAsync(ct));
            if (hub.VersionCode > 0 && hub.VersionCode != mine.VersionCode)
            {
                return hub.VersionCode > mine.VersionCode ? offer : null;
            }

            // The same version (or the hub wasn't told which): a different file is a newer build of it.
            return await installer.InstalledSha256Async() is { } sha && !string.Equals(sha, hub.Sha256, StringComparison.OrdinalIgnoreCase)
                ? offer : null;
        }

        return AppServices.ReleaseFeed is { } feed && await feed(CancellationToken.None) is { } release && release.VersionCode > mine.VersionCode
            ? release : null;
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        if (Installer is not { } installer || Offer is not { } offer || IsBusy)
        {
            return;
        }

        Listen(installer);
        if (!installer.CanInstall)
        {
            // Android's own switch: once it is on and the app comes back, the update carries on (OnResumed).
            _waitingForPermission = true;
            Show("Allow Nomad to install apps on the page that opened, then come back here.", problem: false);
            installer.AskToInstall();
            return;
        }

        _waitingForPermission = false;
        IsBusy = true;
        var handedOver = false;
        var path = installer.DownloadPath;
        try
        {
            Show("Downloading…", problem: false);
            if (await DownloadAsync(offer, path) is { } wrong)
            {
                Show(wrong, problem: true);
                return;
            }

            Show("Installing… Nomad closes while Android installs it; open it again to use the new version.", problem: false);
            _installing = offer;
            await installer.InstallAsync(path);
            handedOver = true; // Busy until Android answers: installed (the app ends) or not (Failed).
        }
        catch (Exception ex)
        {
            _installing = null;
            TryDelete(path);
            Show("Couldn't update: " + ChatViewModel.Problem(ex), problem: true);
        }
        finally
        {
            IsBusy = handedOver;
        }
    }

    /// <summary>Downloads the offer to <paramref name="path"/>; what is wrong with what arrived, or null when it is what was offered.</summary>
    private async Task<string?> DownloadAsync(AppOffer offer, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long got = 0;
        await using (var source = await offer.Open(CancellationToken.None))
        await using (var file = File.Create(path))
        {
            var buffer = new byte[81920];
            var shown = -1L;
            int n;
            while ((n = await source.ReadAsync(buffer)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n));
                hash.AppendData(buffer, 0, n);
                got += n;
                var mb = got / (1024 * 1024);
                if (mb != shown)
                {
                    shown = mb;
                    Show(offer.Size > 0 ? $"Downloading {mb} of {Math.Max(1, offer.Size / (1024 * 1024))} MB…" : $"Downloading {mb} MB…", problem: false);
                }
            }
        }

        var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        if ((offer.Size > 0 && got != offer.Size) || (offer.Sha256 is { } want && !string.Equals(want, sha, StringComparison.OrdinalIgnoreCase)))
        {
            TryDelete(path);
            return $"The download from {offer.From} didn't match what it offered (it may have changed meanwhile). Try again.";
        }

        return null;
    }

    /// <summary>The app came back: from Android's install switch (the update carries on), or after a while (a check is due).</summary>
    public void OnResumed()
    {
        if (_waitingForPermission && Installer is { CanInstall: true } && Offer is not null && !IsBusy)
        {
            _waitingForPermission = false;
            _ = UpdateAsync();
            return;
        }

        if (_waitingForPermission && Installer is { CanInstall: false })
        {
            _waitingForPermission = false;
            Show("Nomad can't install apps until it is allowed to. Tap Update to open that switch again.", problem: true);
            return;
        }

        _ = CheckAsync();
    }

    /// <summary>Signed out, or on another hub: its offer means nothing now.</summary>
    public void Clear()
    {
        Offer = null;
        Status = null;
        HasChecked = false;
        _checkedUtc = DateTime.MinValue;
        _waitingForPermission = false;
    }

    private void Listen(IAppInstaller installer)
    {
        if (!ReferenceEquals(_listening, installer))
        {
            _listening = installer;
            installer.Failed += (message, refused) => AppServices.Post(() => OnInstallFailed(message, refused));
        }
    }

    private void OnInstallFailed(string message, bool refused)
    {
        if (refused && _installing is { Sha256: { } sha })
        {
            _refused.Add(sha);
            Offer = null;
        }

        _installing = null;
        IsBusy = false;
        TryDelete(Installer?.DownloadPath);
        Show("Android didn't install the update: " + message, problem: true);
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (path is not null)
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private void Show(string text, bool problem)
    {
        Status = text;
        StatusIsProblem = problem;
    }
}
