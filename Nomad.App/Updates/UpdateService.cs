using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.ViewModels;
using Nomad.Core;
using Nomad.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace Nomad.App.Updates;

public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Failed,
}

/// <summary>
/// Self-update through Velopack, as Meitou's launcher does it: the feed of the <c>v*</c> GitHub releases, channel
/// <c>win-x64</c>. Only a Setup install updates itself (<see cref="IsInstalled"/>); a portable or dev build shows its
/// version and opens the releases page.
/// <para>
/// Policy. Auto-update on: check at startup and every 6 hours, download silently, then restart into the update when
/// nothing would be interrupted (<see cref="MainViewModel.CanRestartQuietly"/>: no turn, phone session, prompt being
/// typed or Remote Control link). Nomad's CLI processes die with it (job object), so a busy Nomad is never restarted on
/// its own: the footer offers "Restart to update", and the update is applied when Nomad exits. Auto-update off: check
/// and offer the version, downloaded and installed on click. Failures are a one-line <see cref="Status"/>.
/// </para>
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    public const string Channel = "win-x64";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly MainViewModel _main;
    private readonly HttpClient _http;
    private UpdateManager? _manager;
    private UpdateInfo? _update;
    private DispatcherTimer? _timer;
    private Task<ReleaseList>? _releases;
    private DispatcherTimer? _idleWait;
    private bool _manualDownload;
    private bool _restarting;
    private bool _applyOnExitStarted;

    public UpdateService(MainViewModel main)
    {
        _main = main;
        _http = ReleaseFeed.CreateClient(typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        var cacheDir = Path.GetDirectoryName(Path.GetFullPath(main.SettingsPath)) ?? AppContext.BaseDirectory;
        var repository = main.Dev.ReleasesRepository ?? NomadInfo.Repository;
        var cacheName = repository == NomadInfo.Repository ? "releases-cache.json" : $"releases-cache-{repository.Replace('/', '-')}.json";
        Feed = new ReleaseFeed(_http, repository, Path.Combine(cacheDir, cacheName));

        CreateManager();
        IsInstalled = _manager is { IsInstalled: true, IsPortable: false };
        IsPortable = _manager is { IsInstalled: true, IsPortable: true };
        CurrentVersion = _manager is { IsInstalled: true, CurrentVersion: { } installed }
            ? installed.ToString()
            : typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        Status = IsInstalled ? "Nomad.ai checks for updates at startup and every 6 hours." : "";
    }

    public ReleaseFeed Feed { get; }

    /// <summary>Installed by Setup, so it can update itself (a portable zip or a dev build cannot).</summary>
    public bool IsInstalled { get; }

    public bool IsPortable { get; }

    /// <summary>"1.0.0": Velopack's package version when installed, else the 3-part assembly version.</summary>
    public string CurrentVersion { get; }

    public string VersionLine => $"Nomad.ai {CurrentVersion} · {Channel}";

    public string? InstallHint => IsInstalled
        ? null
        : (IsPortable ? "Portable build" : "Development build") + ": it updates itself only when installed with Setup.";

    /// <summary>Closes the window, as the user would: how Nomad exits to restart into an update.</summary>
    public Action? RequestExit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChip), nameof(ShowRestartChip), nameof(ShowPromptChip), nameof(ChipText), nameof(ChipTip), nameof(ActionText))]
    [NotifyPropertyChangedFor(nameof(ActionIsPrimary), nameof(CanAct), nameof(PromptActionText), nameof(IsFailed), nameof(IsAttention))]
    public partial UpdatePhase Phase { get; set; }

    /// <summary>One line: checking, up to date, available, downloading 42 %, restart to update, or what failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; }

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipText), nameof(PromptActionText))]
    public partial int Progress { get; set; }

    /// <summary>The version the last check found ("1.0.1"), null when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipText), nameof(ChipTip), nameof(PromptTitle))]
    public partial string? AvailableVersion { get; set; }

    /// <summary>That version's release notes (GitHub's, else the package's), shown in the update prompt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailableNotes))]
    public partial string? AvailableNotes { get; set; }

    public bool HasAvailableNotes => !string.IsNullOrWhiteSpace(AvailableNotes);

    public bool IsFailed => Phase == UpdatePhase.Failed;

    /// <summary>A version is waiting for the user: the status line is drawn in the accent colour.</summary>
    public bool IsAttention => Phase is UpdatePhase.Available or UpdatePhase.Ready;

    /// <summary>"Restart to update" was clicked while a turn ran: Nomad restarts once none does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipText), nameof(ChipTip))]
    public partial bool RestartQueued { get; set; }

    // ------------------------------------------------------------------ the footer chip and its prompt

    /// <summary>The footer chip is out (it folds in and out).</summary>
    public bool ShowChip => ShowRestartChip || ShowPromptChip;

    /// <summary>Downloaded: the chip restarts into the update when clicked.</summary>
    public bool ShowRestartChip => Phase == UpdatePhase.Ready;

    /// <summary>
    /// Found but not downloaded (auto-update off), or being downloaded or failed after the user asked for it: the chip
    /// opens the prompt with the version's notes.
    /// </summary>
    public bool ShowPromptChip => Phase == UpdatePhase.Available ||
        (_manualDownload && _update is not null && Phase is UpdatePhase.Downloading or UpdatePhase.Failed);

    public string ChipText => Phase switch
    {
        UpdatePhase.Ready => RestartQueued ? "Restarting after the turn" : "Restart to update",
        UpdatePhase.Downloading => $"Downloading {Progress} %",
        _ => $"Update to {AvailableVersion}",
    };

    public string ChipTip => Phase switch
    {
        UpdatePhase.Ready when RestartQueued => $"Nomad.ai restarts into {AvailableVersion} as soon as no turn is running.",
        UpdatePhase.Ready => $"Restart Nomad.ai into version {AvailableVersion}. It is also installed when Nomad.ai closes.",
        _ => $"Nomad.ai {AvailableVersion} is available",
    };

    public string PromptTitle => $"Nomad.ai {AvailableVersion} is available";

    public string PromptActionText => Phase switch
    {
        UpdatePhase.Downloading => $"Downloading {Progress} %",
        UpdatePhase.Ready => "Restart to update",
        _ => "Download and restart",
    };

    // ------------------------------------------------------------------ the Updates page's button

    public string ActionText => !IsInstalled ? "Open releases page" : Phase switch
    {
        UpdatePhase.Checking => "Checking…",
        UpdatePhase.Available => "Download and restart",
        UpdatePhase.Downloading => "Downloading…",
        UpdatePhase.Ready => "Restart to update",
        _ => "Check for updates",
    };

    public bool ActionIsPrimary => IsInstalled && Phase is UpdatePhase.Available or UpdatePhase.Ready;

    public bool CanAct => Phase is not (UpdatePhase.Checking or UpdatePhase.Downloading) && !_restarting;

    [RelayCommand]
    private async Task ActAsync()
    {
        if (!IsInstalled)
        {
            OpenReleasesPage();
            return;
        }

        switch (Phase)
        {
            case UpdatePhase.Available:
                await InstallAsync();
                break;
            case UpdatePhase.Ready:
                RestartNow();
                break;
            default:
                await CheckAsync(automatic: false);
                break;
        }
    }

    /// <summary>The prompt's button: download (if not yet), then restart.</summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Phase is UpdatePhase.Available or UpdatePhase.Failed && _update is not null)
        {
            _manualDownload = true;
            await DownloadAsync();
        }

        if (Phase == UpdatePhase.Ready)
        {
            RestartNow();
        }
    }

    /// <summary>"Restart to update": now, or once the running turns have finished (their CLIs would die with Nomad).</summary>
    [RelayCommand]
    private void RestartNow()
    {
        if (Phase != UpdatePhase.Ready)
        {
            return;
        }

        if (_main.HasRunningTurns)
        {
            // Not now: the running turns' CLIs would die with Nomad. Checked every second until none runs.
            RestartQueued = true;
            Status = $"Nomad.ai restarts into {AvailableVersion} when the running turn has finished.";
            _idleWait ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
            {
                if (!_main.HasRunningTurns)
                {
                    _idleWait?.Stop();
                    Restart();
                }
            });
            _idleWait.Start();
            return;
        }

        Restart();
    }

    [RelayCommand]
    private void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Feed.PageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Could not open the browser: " + OneLine(ex);
        }
    }

    // ------------------------------------------------------------------ checking, downloading, applying

    /// <summary>After the window opened: the first check, then one every 6 hours (Setup installs only).</summary>
    public void Start()
    {
        if (!IsInstalled || _timer is not null)
        {
            return;
        }

        if (_main.Dev.UpdateCheckAfterSeconds is { } after)
        {
            // --dev-update-after: a screenshot can then catch the chip folding in.
            DispatcherTimer.RunOnce(() => _ = CheckAsync(automatic: true), TimeSpan.FromSeconds(after));
        }
        else
        {
            _ = CheckAsync(automatic: true);
        }

        _timer = new DispatcherTimer(CheckInterval, DispatcherPriority.Background, (_, _) => _ = CheckAsync(automatic: true));
        _timer.Start();
    }

    /// <summary>
    /// Asks the feed for a newer version. With auto-update on it is downloaded at once; an automatic check then
    /// restarts into it when Nomad is idle, a check from the Updates page leaves the restart to the user.
    /// </summary>
    public async Task CheckAsync(bool automatic)
    {
        if (_manager is null || _restarting || Phase is UpdatePhase.Checking or UpdatePhase.Downloading)
        {
            return;
        }

        if (Phase == UpdatePhase.Ready)
        {
            if (automatic && _main.Settings.AutoUpdate)
            {
                RestartWhenIdle();
            }

            return;
        }

        Phase = UpdatePhase.Checking;
        Status = "Checking for updates…";
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                _update = null;
                AvailableVersion = null;
                AvailableNotes = null;
                Phase = UpdatePhase.UpToDate;
                Status = $"Nomad.ai {CurrentVersion} is up to date (checked at {DateTime.Now:HH:mm}).";
                return;
            }

            _update = update;
            AvailableVersion = update.TargetFullRelease.Version.ToString();
            AvailableNotes = await NotesForAsync(update.TargetFullRelease);
            if (!_main.Settings.AutoUpdate)
            {
                Phase = UpdatePhase.Available;
                Status = $"Nomad.ai {AvailableVersion} is available.";
                return;
            }

            _manualDownload = !automatic;
            await DownloadAsync();
            if (automatic && Phase == UpdatePhase.Ready)
            {
                RestartWhenIdle();
            }
        }
        catch (Exception ex)
        {
            Phase = UpdatePhase.Failed;
            Status = "Update check failed: " + OneLine(ex);
        }
    }

    private async Task DownloadAsync()
    {
        if (_manager is null || _update is null)
        {
            return;
        }

        Progress = 0;
        Phase = UpdatePhase.Downloading;
        Status = $"Downloading Nomad.ai {AvailableVersion}… 0 %";
        try
        {
            await _manager.DownloadUpdatesAsync(_update, p => Dispatcher.UIThread.Post(() =>
            {
                if (Phase == UpdatePhase.Downloading)
                {
                    Progress = p;
                    Status = $"Downloading Nomad.ai {AvailableVersion}… {p} %";
                }
            }));
            Phase = UpdatePhase.Ready;
            Status = $"Nomad.ai {AvailableVersion} is downloaded: restart to update.";
        }
        catch (Exception ex)
        {
            Phase = UpdatePhase.Failed;
            Status = "Download failed: " + OneLine(ex);
        }
    }

    /// <summary>The automatic restart: only when it would interrupt nothing (<see cref="MainViewModel.CanRestartQuietly"/>).</summary>
    private void RestartWhenIdle()
    {
        if (!_main.CanRestartQuietly)
        {
            Status = $"Nomad.ai {AvailableVersion} is downloaded: restart to update, or it is installed when Nomad.ai closes.";
            return;
        }

        Restart();
    }

    /// <summary>
    /// Velopack's ApplyUpdatesAndRestart, but silent and with a normal exit: Nomad closes its window as the user would
    /// (settings saved, phone servers stopped cleanly), and <see cref="OnExit"/> then has Update.exe apply the package
    /// without a window or dialog and start the new version with the same arguments.
    /// </summary>
    private void Restart()
    {
        if (_manager is null || _update is null || _restarting)
        {
            return;
        }

        _restarting = true;
        OnPropertyChanged(nameof(CanAct));
        Status = $"Restarting into Nomad.ai {AvailableVersion}…";
        RequestExit?.Invoke();
    }

    /// <summary>
    /// Nomad is exiting with an update downloaded: Update.exe applies it, and starts the new version if this exit is a
    /// <see cref="Restart"/>. Started last, once the window has closed: Velopack 1.2.161's updater can fail to wait for
    /// Nomad ("Failed to wait for process … Access is denied" in its log) and then goes on at once, stopping a Nomad
    /// that is still running. If it cannot start, Velopack applies the update at the next start.
    /// </summary>
    public void OnExit()
    {
        if (_manager is null || _update is null || Phase != UpdatePhase.Ready || _applyOnExitStarted)
        {
            return;
        }

        _applyOnExitStarted = true;
        try
        {
            var args = _restarting ? Environment.GetCommandLineArgs().Skip(1).ToArray() : null;
            _manager.WaitExitThenApplyUpdates(_update.TargetFullRelease, silent: true, restart: _restarting, args);
        }
        catch (Exception)
        {
            // Velopack applies a downloaded update at the next start anyway.
        }
    }

    /// <summary>The pre-release setting changed: the GitHub source is rebuilt with it and asked again.</summary>
    public void OnPrereleasesChanged()
    {
        if (!IsInstalled || Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Ready || _restarting)
        {
            return;
        }

        CreateManager();
        _update = null;
        AvailableVersion = null;
        Phase = UpdatePhase.Idle;
        _ = CheckAsync(automatic: false);
    }

    // ------------------------------------------------------------------ release notes

    /// <summary>The GitHub release list, read once per run (and again when <paramref name="refresh"/>).</summary>
    public Task<ReleaseList> GetReleasesAsync(bool refresh = false)
    {
        if (refresh || _releases is null || _releases.IsFaulted)
        {
            _releases = Feed.FetchAsync();
        }

        return _releases;
    }

    private async Task<string?> NotesForAsync(VelopackAsset asset)
    {
        var version = asset.Version.ToString();
        try
        {
            // The list read earlier in this run may predate the release: then it is read again (an ETag request).
            var list = await GetReleasesAsync();
            if (list.Releases.All(r => r.Version != version))
            {
                list = await GetReleasesAsync(refresh: true);
            }

            if (list.Releases.FirstOrDefault(r => r.Version == version) is { Notes.Length: > 0 } release)
            {
                return ReleaseFeed.TrimNotes(release.Notes);
            }
        }
        catch (Exception)
        {
            // The package's own notes below.
        }

        return string.IsNullOrWhiteSpace(asset.NotesMarkdown) ? null : ReleaseFeed.TrimNotes(asset.NotesMarkdown);
    }

    /// <summary>Whether <paramref name="version"/> is newer than the running one (SemVer, pre-releases before their release).</summary>
    public bool IsNewer(string version) =>
        SemanticVersion.TryParse(version, out var other) && SemanticVersion.TryParse(CurrentVersion, out var current) && other > current;

    public bool IsCurrent(string version) =>
        SemanticVersion.TryParse(version, out var other) && SemanticVersion.TryParse(CurrentVersion, out var current)
            ? other == current
            : version == CurrentVersion;

    // ------------------------------------------------------------------ helpers

    private void CreateManager()
    {
        try
        {
            IUpdateSource source = _main.Dev.UpdateSource is { Length: > 0 } folder
                ? new SimpleFileSource(new DirectoryInfo(folder))
                : new GithubSource($"https://github.com/{NomadInfo.Repository}", null, _main.Settings.IncludePrereleases);
            _manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = Channel });
        }
        catch (Exception)
        {
            _manager = null;
        }
    }

    private static string OneLine(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }
}
