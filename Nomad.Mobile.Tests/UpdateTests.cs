using System.Security.Cryptography;
using Nomad.Hub.Contracts;
using Nomad.Mobile.Hub;
using Nomad.Mobile.ViewModels;

namespace Nomad.Mobile.Tests;

/// <summary>Updating the app (docs/mobile.md §5): what counts as newer, the download's check, and Android's install switch.</summary>
[Collection("app services")]
public sealed class UpdateTests : IDisposable
{
    private static readonly byte[] NewApk = [.. Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7))];

    private readonly FakeInstaller _installer = new();

    public UpdateTests()
    {
        AppServices.Post = work => work();
        AppServices.Installer = _installer;
        AppServices.ReleaseFeed = null;
    }

    public void Dispose()
    {
        AppServices.Installer = null;
        AppServices.ReleaseFeed = null;
        _installer.Dispose();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static PhoneAppInfo Offered(long code, byte[]? bytes = null) =>
        new("2.9.0", code, (bytes ?? NewApk).Length, Sha(bytes ?? NewApk), new DateTime(2026, 11, 26, 21, 40, 0, DateTimeKind.Utc));

    private static async Task<(MainViewModel Main, FakeHub Hub)> SignedInAsync(PhoneAppInfo? offered)
    {
        var hub = new FakeHub { PhoneApp = offered, PhoneAppBytes = NewApk };
        hub.Board.AddRange(Samples.Board());
        var store = new MemorySecretStore();
        new Enrollment(new Uri("https://hub.example.ts.net/"), Guid.NewGuid(), "Test phone", "secret").Save(store);
        var main = new MainViewModel(store, hub);
        main.Start();
        await Task.Yield();
        return (main, hub);
    }

    [Fact]
    public async Task A_newer_version_on_the_hub_is_offered_downloaded_checked_and_handed_to_Android()
    {
        var (main, hub) = await SignedInAsync(Offered(20900));
        var update = main.Chats.Update;

        Assert.Contains("PhoneApp", hub.Calls); // Looked for on connecting.
        Assert.True(update.ShowsBar);
        Assert.StartsWith("Nomad 2.9.0 (", update.OfferText);

        await update.UpdateCommand.ExecuteAsync(null);

        Assert.Equal([_installer.DownloadPath], _installer.InstallsStarted);
        Assert.Equal(NewApk, File.ReadAllBytes(_installer.DownloadPath));
        Assert.StartsWith("Installing", update.Status);
        Assert.False(update.StatusIsProblem);

        // Busy until Android answers, so it can't be started twice.
        Assert.True(update.IsBusy);
        Assert.False(update.UpdateCommand.CanExecute(null));

        // Cancelled: said on the bar, and it can be tried again.
        _installer.Fail("the install was cancelled.");
        Assert.Equal("Android didn't install the update: the install was cancelled.", update.Status);
        Assert.True(update.StatusIsProblem);
        Assert.False(update.IsBusy);
        Assert.NotNull(update.Offer);
        Assert.False(File.Exists(_installer.DownloadPath));
    }

    [Fact]
    public async Task A_file_Android_refuses_isnt_offered_again()
    {
        // An older file with no version beside it: offered, since it differs from this one, but Android won't take it.
        var (main, _) = await SignedInAsync(Offered(0));
        await main.Update.UpdateCommand.ExecuteAsync(null);
        _installer.Fail("it doesn't fit this phone.", refused: true);

        Assert.Null(main.Update.Offer);
        await main.Update.CheckAsync(force: true);
        Assert.Null(main.Update.Offer);
        Assert.True(main.Update.StatusIsProblem);
    }

    [Fact]
    public async Task It_looks_again_only_after_half_an_hour_unless_asked()
    {
        var (main, hub) = await SignedInAsync(offered: null);
        Assert.Single(hub.Calls, "PhoneApp");

        await main.Update.CheckAsync();
        Assert.Single(hub.Calls, "PhoneApp"); // Too soon.

        hub.PhoneApp = Offered(20900);
        await main.Update.CheckAsync(force: true); // Settings opening.
        Assert.Equal(2, hub.Calls.Count(c => c == "PhoneApp"));
        Assert.NotNull(main.Update.Offer);
    }

    [Fact]
    public async Task The_same_version_is_offered_only_when_its_file_differs_and_an_older_one_never()
    {
        _installer.Sha256 = Sha(NewApk);
        var (same, _) = await SignedInAsync(Offered(20805));
        Assert.Null(same.Update.Offer); // The very file installed here.

        _installer.Sha256 = Sha([1, 2, 3]);
        var (rebuilt, _) = await SignedInAsync(Offered(20805));
        Assert.NotNull(rebuilt.Update.Offer); // Another build of the same version.

        var (older, _) = await SignedInAsync(Offered(20700));
        Assert.Null(older.Update.Offer);

        var (unversioned, _) = await SignedInAsync(Offered(0)); // The hub wasn't told its version: the file decides.
        Assert.NotNull(unversioned.Update.Offer);
    }

    [Fact]
    public async Task Without_one_on_the_hub_a_newer_GitHub_release_is_offered()
    {
        var released = 20900L;
        AppServices.ReleaseFeed = _ => Task.FromResult<AppOffer?>(
            new AppOffer("GitHub", "2.9.0", released, NewApk.Length, null, null, _ => Task.FromResult<Stream>(new MemoryStream(NewApk))));

        var (main, _) = await SignedInAsync(offered: null);
        Assert.Equal("GitHub", main.Update.Offer?.From);

        released = 20805; // The version installed here.
        await main.Update.CheckAsync(force: true);
        Assert.Null(main.Update.Offer);
    }

    [Fact]
    public async Task A_download_that_doesnt_match_the_offer_is_thrown_away_and_not_installed()
    {
        var (main, hub) = await SignedInAsync(Offered(20900));
        hub.PhoneAppBytes = [.. NewApk.Take(1000)];

        await main.Update.UpdateCommand.ExecuteAsync(null);

        Assert.Empty(_installer.InstallsStarted);
        Assert.False(File.Exists(_installer.DownloadPath));
        Assert.True(main.Update.StatusIsProblem);
        Assert.Contains("didn't match", main.Update.Status);
    }

    [Fact]
    public async Task Without_Androids_install_switch_it_opens_it_and_carries_on_when_the_app_comes_back()
    {
        _installer.CanInstall = false;
        var (main, _) = await SignedInAsync(Offered(20900));

        await main.Update.UpdateCommand.ExecuteAsync(null);
        Assert.Equal(1, _installer.Asked);
        Assert.Empty(_installer.InstallsStarted);
        Assert.StartsWith("Allow Nomad to install apps", main.Update.Status);

        // Back without turning it on: said so, and nothing is installed.
        main.OnResumed();
        Assert.Empty(_installer.InstallsStarted);
        Assert.True(main.Update.StatusIsProblem);

        // Turned on: the update carries on by itself on coming back.
        await main.Update.UpdateCommand.ExecuteAsync(null);
        _installer.CanInstall = true;
        main.OnResumed();
        await WaitForAsync(() => _installer.InstallsStarted.Count == 1);
    }

    [Fact]
    public void Release_tags_become_the_version_codes_the_build_gives()
    {
        Assert.Equal(20900, GitHubRelease.VersionCodeOf("v2.9.0"));
        Assert.Equal(21012, GitHubRelease.VersionCodeOf("2.10.12-beta.1"));
        Assert.Null(GitHubRelease.VersionCodeOf("nightly"));
        Assert.Null(GitHubRelease.VersionCodeOf("v3.0"));
    }

    [Fact]
    public async Task Nothing_is_offered_where_the_platform_cant_install()
    {
        AppServices.Installer = null;
        var (main, hub) = await SignedInAsync(Offered(20900));

        Assert.DoesNotContain("PhoneApp", hub.Calls);
        Assert.False(main.Update.ShowsBar);
        Assert.False(main.Update.IsSupported);
    }

    private static async Task WaitForAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(done());
    }

    private sealed class FakeInstaller : IAppInstaller, IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "nomad-mobile-update-" + Guid.NewGuid().ToString("N"));

        public InstalledApp Installed { get; } = new("2.8.5", 20805);

        public string? Sha256 { get; set; } = "0000";

        public Task<string?> InstalledSha256Async() => Task.FromResult(Sha256);

        public string DownloadPath => Path.Combine(_folder, "Nomad-android.apk");

        public bool CanInstall { get; set; } = true;

        public int Asked { get; private set; }

        public List<string> InstallsStarted { get; } = [];

        public void AskToInstall() => Asked++;

        public Task InstallAsync(string apkPath, CancellationToken ct = default)
        {
            InstallsStarted.Add(apkPath);
            return Task.CompletedTask;
        }

        public event Action<string, bool>? Failed;

        public void Fail(string message, bool refused = false) => Failed?.Invoke(message, refused);

        public void Dispose()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
    }
}
