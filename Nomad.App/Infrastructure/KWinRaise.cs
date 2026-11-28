using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Nomad.App.Infrastructure;

/// <summary>
/// Brings the default browser to the front on KDE Plasma's Wayland session after a link opens in it. Nomad is an Xwayland
/// window there, which KWin never gives an activation token, so the browser it starts may not take the focus (the link only
/// opens behind, in a tab). A KWin script may activate any window, so one is loaded for that and unloaded again.
/// </summary>
internal static partial class KWinRaise
{
    /// <summary>
    /// How long the script waits for the browser's window to appear when none is open yet; it is unloaded after.
    /// </summary>
    private static readonly TimeSpan ScriptLifetime = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a helper (xdg-settings, dbus-send) may take before it is given up.
    /// </summary>
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The script: activates the default browser's topmost window, or the first one to open. <c>%ID%</c> is the browser's desktop
    /// id, checked to hold only safe characters first.
    /// </summary>
    private const string Script = """
        const id = "%ID%";
        function isBrowser(w) {
            return w.normalWindow && ((w.desktopFileName || "").toLowerCase() === id || (w.resourceClass || "").toLowerCase() === id);
        }
        function raise(w) {
            workspace.activeWindow = w;
            workspace.raiseWindow(w);
        }
        const open = workspace.stackingOrder.filter(isBrowser);
        if (open.length > 0) {
            raise(open[open.length - 1]);
        } else {
            const added = function (w) {
                if (!isBrowser(w)) return;
                workspace.windowAdded.disconnect(added);
                raise(w);
            };
            workspace.windowAdded.connect(added);
        }
        """;

    /// <summary>
    /// Raises the default browser in the background when this is a Plasma Wayland session; does nothing anywhere else, or when
    /// anything along the way fails.
    /// </summary>
    public static void RaiseDefaultBrowserSoon()
    {
        if (!IsPlasmaWayland()) return;

        _ = Task.Run(RaiseDefaultBrowserAsync);
    }

    private static bool IsPlasmaWayland() =>
        OperatingSystem.IsLinux() &&
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland" &&
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':').Contains("KDE", StringComparer.OrdinalIgnoreCase);

    private static async Task RaiseDefaultBrowserAsync()
    {
        try
        {
            if (await DefaultBrowserIdAsync() is not { } id) return;

            await RunScriptAsync(Script.Replace("%ID%", id, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // Only a nicety: the link opened anyway.
        }
    }

    /// <summary>
    /// The default browser's desktop id in lower case (<c>brave-browser</c> for <c>brave-browser.desktop</c>), which is its
    /// windows' desktop file name; null when unknown or not a plain id.
    /// </summary>
    private static async Task<string?> DefaultBrowserIdAsync()
    {
        var desktop = await RunAsync("xdg-settings", "get", "default-web-browser")
            ?? await RunAsync("xdg-mime", "query", "default", "x-scheme-handler/https");
        var id = desktop?.Trim();
        if (id is null) return null;

        if (id.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase)) id = id[..^".desktop".Length];

        return SafeId().IsMatch(id) ? id.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Loads the script into KWin under a name of its own, runs it, and unloads it once it has had its time.
    /// </summary>
    private static async Task RunScriptAsync(string script)
    {
        var name = "nomad-raise-" + Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } cache
            ? cache
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "Nomad");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name + ".js");
        await File.WriteAllTextAsync(file, script);

        try
        {
            if (await KWinCallAsync("/Scripting", "org.kde.kwin.Scripting.loadScript", "string:" + file, "string:" + name) is not { } loaded
                || !int.TryParse(loaded.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), out var scriptId)
                || scriptId < 0)
            {
                return;
            }

            await KWinCallAsync($"/Scripting/Script{scriptId}", "org.kde.kwin.Script.run");
            await Task.Delay(ScriptLifetime);
            await KWinCallAsync("/Scripting", "org.kde.kwin.Scripting.unloadScript", "string:" + name);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Calls a method of KWin's on the session bus; its reply, or null when the call failed.
    /// </summary>
    private static Task<string?> KWinCallAsync(string path, string method, params string[] arguments) =>
        RunAsync("dbus-send", ["--session", "--print-reply=literal", "--dest=org.kde.KWin", path, method, .. arguments]);

    /// <summary>
    /// Runs a helper and returns what it printed; null when it could not start, failed or took too long.
    /// </summary>
    private static async Task<string?> RunAsync(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null) return null;

        using var timeout = new CancellationTokenSource(HelperTimeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? await output : null;
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            return null;
        }
    }

    /// <summary>
    /// A desktop id that is safe to put in the script.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeId();
}
