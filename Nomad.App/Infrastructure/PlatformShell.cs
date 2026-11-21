using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nomad.App.Infrastructure;

/// <summary>Opens folders, reveals files in the file manager, and opens terminals on Windows and Linux.</summary>
public static partial class PlatformShell
{
    private const int AsfwAny = -1;

    private static readonly string[] LinuxTerminals =
    [
        "x-terminal-emulator",
        "gnome-terminal",
        "konsole",
        "xfce4-terminal",
        "alacritty",
        "kitty",
        "wezterm",
        "xterm",
    ];

    /// <summary>Opens <paramref name="path"/> with the OS's default handler, falling back to the file manager if none is registered.</summary>
    public static void OpenWithShell(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // A folder (or a file whose app is already running) opens in another process; let it come to the front.
            AllowSetForegroundWindow(AsfwAny);
        }

        if (!TryStart(new ProcessStartInfo(path) { UseShellExecute = true }) && File.Exists(path))
        {
            RevealInFileManager(path);
        }
    }

    /// <summary>Opens <paramref name="folder"/> in Explorer (Windows) or the default file manager (Linux).</summary>
    public static void OpenFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            TryStart(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
            return;
        }

        TryStart(new ProcessStartInfo("xdg-open") { ArgumentList = { folder }, UseShellExecute = false });
    }

    /// <summary>Opens the file manager with <paramref name="path"/> selected (or its containing folder on Linux).</summary>
    public static void RevealInFileManager(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // The explorer.exe started here hands the window to the running shell process, which may not take the
            // foreground on its own (the window would only flash in the taskbar): this app has it, so pass it on.
            AllowSetForegroundWindow(AsfwAny);
            TryStart(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            return;
        }

        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        TryStart(new ProcessStartInfo("xdg-open") { ArgumentList = { folder }, UseShellExecute = false });
    }

    /// <summary>Opens a terminal in <paramref name="folder"/>.</summary>
    public static void OpenTerminal(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!TryStart(new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", folder }, UseShellExecute = true }))
            {
                TryStart(new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true });
            }

            return;
        }

        foreach (var terminal in CandidateLinuxTerminals())
        {
            if (TryStart(new ProcessStartInfo(terminal) { WorkingDirectory = folder, UseShellExecute = false }))
            {
                return;
            }
        }
    }

    /// <summary>Opens a terminal in <paramref name="folder"/> running <paramref name="executable"/> with <paramref name="argument"/>.</summary>
    public static bool OpenTerminalCommand(string folder, string executable, string argument)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryStart(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/k \"\"{executable}\" {argument}\"",
                WorkingDirectory = folder,
                UseShellExecute = true,
            });
        }

        foreach (var terminal in CandidateLinuxTerminals())
        {
            var psi = new ProcessStartInfo(terminal) { WorkingDirectory = folder, UseShellExecute = false };
            var name = Path.GetFileName(terminal);
            if (name is "gnome-terminal")
            {
                psi.ArgumentList.Add("--");
            }
            else if (name is "wezterm")
            {
                psi.ArgumentList.Add("start");
                psi.ArgumentList.Add("--");
            }
            else if (name is "kitty")
            {
                // kitty takes the command directly.
            }
            else
            {
                psi.ArgumentList.Add("-e");
            }

            psi.ArgumentList.Add(executable);
            psi.ArgumentList.Add(argument);
            if (TryStart(psi))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> CandidateLinuxTerminals()
    {
        var env = Environment.GetEnvironmentVariable("TERMINAL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env.Trim();
        }

        foreach (var terminal in LinuxTerminals)
        {
            yield return terminal;
        }
    }

    private static bool TryStart(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
