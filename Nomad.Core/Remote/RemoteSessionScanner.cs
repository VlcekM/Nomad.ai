using System.Buffers;
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
    private const uint Th32csSnapProcess = 0x2;
    private static readonly string[] CliExeNames = ["claude.exe", "node.exe"];

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
    internal static string? SessionId(string commandLine) => Option(commandLine, "--session-id");

    /// <summary>
    /// The value of an option, given as <c>--name value</c> or <c>--name=value</c>. Walks the line as spans: a CLI
    /// command line runs to a few thousand characters, and this is asked of every one found, every five seconds.
    /// </summary>
    private static string? Option(string commandLine, params string[] names)
    {
        var rest = commandLine.AsSpan();
        ReadOnlySpan<char> previous = default;
        while (NextToken(ref rest, out var token))
        {
            foreach (var name in names)
            {
                if (previous.SequenceEqual(name))
                {
                    return token.ToString();
                }

                if (token.Length > name.Length && token[name.Length] == '=' && token.StartsWith(name, StringComparison.Ordinal))
                {
                    return token[(name.Length + 1)..].ToString();
                }
            }

            previous = token;
        }

        return null;
    }

    /// <summary>The next whitespace-separated token, without quotes around it; false at the end.</summary>
    private static bool NextToken(ref ReadOnlySpan<char> rest, out ReadOnlySpan<char> token)
    {
        rest = rest.TrimStart(" \t");
        if (rest.IsEmpty)
        {
            token = default;
            return false;
        }

        var end = rest.IndexOfAny(' ', '\t');
        if (end < 0)
        {
            end = rest.Length;
        }

        token = rest[..end].Trim('"');
        rest = rest[end..];
        return true;
    }

    /// <summary>
    /// Sessions a <c>claude</c> process outside this Nomad is running: another Nomad window (which starts every session
    /// with <c>--session-id</c> or <c>--resume</c>) or a terminal (<c>claude --resume &lt;id&gt;</c>). Processes of this
    /// Nomad's own job are left out. Safe to call from a background thread.
    /// </summary>
    public static IReadOnlySet<string> RunningSessionIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            return ids;
        }

        var ours = new HashSet<int>(ChildProcessJob.ProcessIds());
        ours.UnionWith(ClaudeCliProcess.OwnedProcessIds);

        // The native CLI, or the npm one running under node.
        foreach (var pid in ProcessIdsNamed(CliExeNames))
        {
            if (ours.Contains(pid) || CommandLine(pid) is not { } commandLine
                || !commandLine.Contains("claude", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A fork runs under its new id; otherwise the resumed one, or the id a new session was given.
            var forked = commandLine.Contains("--fork-session", StringComparison.Ordinal);
            var id = forked ? Option(commandLine, "--session-id") : Option(commandLine, "--resume", "-r") ?? Option(commandLine, "--session-id");
            if (id is not null && Guid.TryParse(id, out _))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// The ids of the processes running one of these executables, from a Toolhelp snapshot: a few hundred bytes reused
    /// per process. (<c>Process.GetProcesses</c> builds an object with its thread list for every process on the machine,
    /// and this runs every five seconds.)
    /// </summary>
    private static unsafe List<int> ProcessIdsNamed(string[] exeNames)
    {
        var pids = new List<int>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == -1)
        {
            return pids;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)sizeof(ProcessEntry32) };
            for (var more = Process32FirstW(snapshot, &entry); more; more = Process32NextW(snapshot, &entry))
            {
                var exe = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(entry.ExeFile);
                foreach (var name in exeNames)
                {
                    if (exe.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        pids.Add((int)entry.ProcessId);
                        break;
                    }
                }
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return pids;
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
            // The result is a UNICODE_STRING followed by its text, in the same buffer (pooled: one query per process, every scan).
            var size = 8192u;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = ArrayPool<byte>.Shared.Rent((int)size);
                try
                {
                    fixed (byte* p = buffer)
                    {
                        var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, p, (uint)buffer.Length, out var needed);
                        if (status == 0)
                        {
                            var text = (UnicodeString*)p;
                            return text->Buffer == 0 ? null : new string((char*)text->Buffer, 0, text->Length / 2);
                        }

                        if (needed <= buffer.Length)
                        {
                            return null;
                        }

                        size = needed;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
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

    /// <summary>PROCESSENTRY32W.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        public fixed char ExeFile[260];
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32FirstW(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32NextW(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(nint process, int infoClass, void* info, uint length, out uint returnLength);
}
