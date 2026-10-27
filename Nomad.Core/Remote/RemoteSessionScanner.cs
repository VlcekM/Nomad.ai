using System.Runtime.InteropServices;
using Nomad.Core.Process;

namespace Nomad.Core.Remote;

/// <summary>
/// Finds the sessions that <see cref="RemoteControlServer"/>s started for the phone: their processes
/// are in Nomad's job (children inherit it) and run <c>claude --print --sdk-url … --session-id &lt;id&gt;</c>.
/// Only processes in the job are looked at, never anything else on the machine.
/// </summary>
/// <summary>A session the phone started: its process, the server that started it, claude.ai id (<c>cse_…</c>) and start time.</summary>
public sealed record PhoneSessionProcess(int ProcessId, int ParentProcessId, string RemoteId, DateTime StartedUtc);

public static partial class RemoteSessionScanner
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    /// <summary>Running phone sessions. Safe to call from a background thread.</summary>
    public static IReadOnlyList<PhoneSessionProcess> Scan()
    {
        var result = new List<PhoneSessionProcess>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        var ours = new HashSet<int>(ClaudeCliProcess.OwnedProcessIds);
        foreach (var pid in ChildProcessJob.ProcessIds())
        {
            if (ours.Contains(pid) || CommandLine(pid) is not { } commandLine || !commandLine.Contains("--sdk-url", StringComparison.Ordinal))
            {
                continue;
            }

            if (SessionId(commandLine) is { } id)
            {
                DateTime started;
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    started = process.StartTime.ToUniversalTime();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    continue; // Already gone.
                }

                result.Add(new PhoneSessionProcess(pid, ParentOf(pid), id, started));
            }
        }

        return result;
    }

    /// <summary>
    /// The local transcript of a phone session. The CLI names it with its own uuid, not the claude.ai
    /// id, so it is the first transcript in the folder's project directory created once the process
    /// started (and not already taken by another phone session).
    /// </summary>
    public static string? FindTranscript(string projectsRoot, string folder, DateTime startedUtc, IReadOnlySet<string> taken)
    {
        var dir = Path.Combine(projectsRoot, ProjectDirectoryName(folder));
        if (!Directory.Exists(dir))
        {
            return null;
        }

        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*.jsonl")
                .Where(f => f.CreationTimeUtc >= startedUtc.AddSeconds(-2) && !taken.Contains(f.FullName))
                .OrderBy(f => f.CreationTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>How the CLI names a folder's directory under <c>~/.claude/projects</c> (<c>R:\a\b.c</c> → <c>R--a-b-c</c>).</summary>
    public static string ProjectDirectoryName(string folder) =>
        string.Concat(folder.TrimEnd('\\', '/').Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));

    /// <summary>The value after <c>--session-id</c> (separate or <c>=</c> form).</summary>
    internal static string? SessionId(string commandLine)
    {
        var parts = commandLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim('"');
            if (part == "--session-id" && i + 1 < parts.Length)
            {
                return parts[i + 1].Trim('"');
            }

            if (part.StartsWith("--session-id=", StringComparison.Ordinal))
            {
                return part["--session-id=".Length..];
            }
        }

        return null;
    }

    private static unsafe string? CommandLine(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            // The result is a UNICODE_STRING followed by its text, in the same buffer.
            var size = 8192u;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = new byte[size];
                fixed (byte* p = buffer)
                {
                    var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, p, size, out var needed);
                    if (status == 0)
                    {
                        var text = (UnicodeString*)p;
                        return text->Buffer == 0 ? null : new string((char*)text->Buffer, 0, text->Length / 2);
                    }

                    if (needed <= size)
                    {
                        return null;
                    }

                    size = needed;
                }
            }

            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static unsafe int ParentOf(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return 0;
        }

        try
        {
            ProcessBasicInformation info;
            return NtQueryInformationProcess(handle, 0, &info, (uint)sizeof(ProcessBasicInformation), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(nint process, int infoClass, void* info, uint length, out uint returnLength);
}
