using System.Runtime.InteropServices;

namespace Nomad.Core.Process;

/// <summary>
/// A Windows job object with KILL_ON_JOB_CLOSE: every CLI process we start is assigned to it, so if
/// Nomad dies without cleaning up, Windows ends our children (and only ours) when the job handle
/// closes. No-op on other platforms or when job creation fails.
/// </summary>
internal static partial class ChildProcessJob
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<nint> Job = new(CreateJob);

    public static void TryAssign(System.Diagnostics.Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var job = Job.Value;
            if (job != 0)
            {
                AssignProcessToJobObject(job, process.Handle);
            }
        }
        catch (Exception)
        {
            // Best effort: the explicit kill on shutdown still applies.
        }
    }

    /// <summary>
    /// PIDs of every process in the job: ours, and the ones they started (children inherit the job).
    /// Empty when there is no job.
    /// </summary>
    public static unsafe IReadOnlyList<int> ProcessIds()
    {
        if (!OperatingSystem.IsWindows() || !Job.IsValueCreated || Job.Value == 0)
        {
            return [];
        }

        // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORD counts, then ULONG_PTR ids.
        const int capacity = 512;
        var buffer = stackalloc nuint[capacity + 1];
        var length = (uint)((capacity + 1) * sizeof(nuint));
        if (!QueryInformationJobObject(Job.Value, BasicProcessIdListClass, buffer, length, 0))
        {
            return [];
        }

        var count = (int)((uint*)buffer)[1];
        var ids = new int[Math.Min(count, capacity)];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = (int)buffer[1 + i];
        }

        return ids;
    }

    private const int BasicProcessIdListClass = 3;

    private static unsafe nint CreateJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var job = CreateJobObjectW(0, 0);
        if (job == 0)
        {
            return 0;
        }

        var info = new JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(job, ExtendedLimitInformationClass, &info, (uint)sizeof(JobObjectExtendedLimitInformation)))
        {
            CloseHandle(job);
            return 0;
        }

        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateJobObjectW(nint jobAttributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryInformationJobObject(nint job, int infoClass, void* info, uint length, nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
