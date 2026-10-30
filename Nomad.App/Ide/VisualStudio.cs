using System.Diagnostics;

namespace Nomad.App.Ide;

/// <summary>
/// Opens a folder in Visual Studio: its solution (the nearest .slnx or .sln, up to the repository's root), or else the
/// folder itself (Open Folder). Visual Studio is found with vswhere, which its installer puts in a fixed place.
/// </summary>
public static class VisualStudio
{
    private static readonly string VsWhere = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");

    /// <summary>Visual Studio is installed (its installer, at least).</summary>
    public static bool IsInstalled { get; } = OperatingSystem.IsWindows() && File.Exists(VsWhere);

    /// <summary>The solution Visual Studio opens for a folder: the nearest one in it or above it, within its repository.</summary>
    public static string? SolutionFor(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            try
            {
                var solution = dir.EnumerateFiles("*.sln*")
                    .Where(f => f.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (solution is not null)
                {
                    return solution.FullName;
                }

                // Not past the repository's root (".git" is a folder there, or a file in a worktree).
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    return null;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Starts Visual Studio on the folder's solution (or the folder); false when it could not.</summary>
    public static async Task<bool> OpenAsync(string folder)
    {
        var target = SolutionFor(folder) ?? folder;
        var devenv = await Task.Run(FindDevenv);
        try
        {
            if (devenv is not null)
            {
                Process.Start(new ProcessStartInfo(devenv) { ArgumentList = { target }, UseShellExecute = false })?.Dispose();
                return true;
            }

            // No devenv found: the solution's own handler (the Visual Studio Version Selector).
            if (target != folder)
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
                return true;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }

        return false;
    }

    // The newest Visual Studio's devenv.exe (previews too).
    private static string? FindDevenv()
    {
        if (!IsInstalled)
        {
            return null;
        }

        try
        {
            var start = new ProcessStartInfo(VsWhere)
            {
                ArgumentList = { "-latest", "-prerelease", "-products", "*", "-property", "productPath", "-utf8" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var path = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return path is not null && File.Exists(path) ? path : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
