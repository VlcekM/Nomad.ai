using System.Diagnostics;
using System.Text;

namespace Nomad.Core.Agents;

/// <summary>
/// What a PowerShell command run by <see cref="DesktopCommand"/> left behind: its exit code (null when it was stopped at its time
/// limit or cancelled), what it printed, and how long it took.
/// </summary>
public sealed record DesktopCommandResult(int? ExitCode, string Output, string Errors, bool TimedOut, TimeSpan Elapsed, bool Cancelled = false);

/// <summary>
/// Runs a PowerShell command as a child of the Nomad window's own process, so it starts on the desktop the user sees ("Default")
/// rather than on the hidden one an agent's own shell runs on: scripts that focus windows, click or capture the screen work there.
/// </summary>
public static partial class DesktopCommand
{
    /// <summary>
    /// The time limit when the call gives none.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The longest time limit a call may ask for.
    /// </summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How much of each stream the result keeps: the start and the end of anything longer.
    /// </summary>
    public const int MaxStreamChars = 30_000;

    /// <summary>
    /// The PowerShell processes running now, so the ones still running when Nomad exits are ended.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Diagnostics.Process> Running = new();

    /// <summary>
    /// Whether the exit handler is in place.
    /// </summary>
    private static int _exitHooked;

    /// <summary>
    /// The process id of each PowerShell started, as it starts; a test reads it.
    /// </summary>
    internal static event Action<int>? Started;

    /// <summary>
    /// Runs <paramref name="command"/> in Windows PowerShell in <paramref name="workingDirectory"/>, without a window, and waits for
    /// it up to <paramref name="timeout"/>, or until <paramref name="cancellationToken"/> is cancelled; a command still running then is
    /// stopped with everything it started (its own process tree, found from its process id).
    /// <para>
    /// PowerShell is deliberately not put in Nomad's child-process job: everything in that job ends when Nomad does, and a program
    /// the command opens for the user (<c>Start-Process</c>) must outlive Nomad. A PowerShell still running when Nomad exits is
    /// ended on its own, leaving what it opened.
    /// </para>
    /// </summary>
    public static async Task<DesktopCommandResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // UTF-8 out, so non-ASCII output survives; the command goes encoded, so no quoting can break it.
        var script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8\n" + command;
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script))])
        {
            psi.ArgumentList.Add(argument);
        }

        var clock = Stopwatch.StartNew();
        using var process = new System.Diagnostics.Process { StartInfo = psi };
        if (!process.Start()) throw new InvalidOperationException("PowerShell did not start.");

        HookExit();
        Running[process.Id] = process;
        Started?.Invoke(process.Id);
        var timedOut = false;
        var cancelled = false;
        try
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                limit.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = cancellationToken.IsCancellationRequested;
                    timedOut = !cancelled;
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // It ended just now.
                    }

                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            // A grandchild that keeps the pipes open must not hold the answer up.
            var streams = Task.WhenAll(output, errors);
            await Task.WhenAny(streams, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
            var stdout = output.IsCompletedSuccessfully ? output.Result : "";
            var stderr = errors.IsCompletedSuccessfully ? errors.Result : "";

            return new DesktopCommandResult(timedOut || cancelled ? null : process.ExitCode, Clip(stdout), Clip(PlainErrors(stderr)), timedOut,
                clock.Elapsed, cancelled);
        }
        finally
        {
            Running.TryRemove(process.Id, out _);
        }
    }

    /// <summary>
    /// Ends, as Nomad exits, each PowerShell still running (only that process: what it opened for the user stays).
    /// </summary>
    private static void HookExit()
    {
        if (Interlocked.Exchange(ref _exitHooked, 1) != 0) return;

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var process in Running.Values)
            {
                try
                {
                    process.Kill(entireProcessTree: false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Already gone.
                }
            }
        };
    }

    /// <summary>
    /// The error stream as text. Started with an encoded command and its streams redirected, Windows PowerShell writes progress and
    /// error records to it as CLIXML ("#&lt; CLIXML" and <c>&lt;Objs&gt;</c> blocks); the progress is dropped and the errors' text kept.
    /// </summary>
    public static string PlainErrors(string stderr)
    {
        if (!stderr.Contains("#< CLIXML", StringComparison.Ordinal)) return stderr;

        var text = ClixmlBlock().Replace(stderr, block => string.Concat(ClixmlError().Matches(block.Value)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Replace("_x000D_", "\r").Replace("_x000A_", "\n"))));
        return string.Join("\n", text.Split('\n').Where(line => line.TrimEnd() != "#< CLIXML")).Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<Objs\b.*?</Objs>", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex ClixmlBlock();

    [System.Text.RegularExpressions.GeneratedRegex(@"<S S=""Error"">(.*?)</S>", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex ClixmlError();

    /// <summary>
    /// The text, or its first third and last two thirds of <see cref="MaxStreamChars"/> with a note of what was cut between them.
    /// </summary>
    public static string Clip(string text)
    {
        if (text.Length <= MaxStreamChars) return text;

        var head = MaxStreamChars / 3;
        var tail = MaxStreamChars - head;
        return text[..head] + $"\n[… {text.Length - head - tail} characters cut …]\n" + text[^tail..];
    }

    /// <summary>
    /// Windows PowerShell from the system folder, else whatever "powershell.exe" the path finds.
    /// </summary>
    private static string PowerShellPath()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(system) ? system : "powershell.exe";
    }
}
