using System.Runtime.InteropServices;

namespace Klodi.Core.Process;

/// <summary>
/// A Windows job object with KILL_ON_JOB_CLOSE: every CLI process we start is assigned to it, so if
/// Klodi dies without cleaning up, Windows ends our children (and only ours) when the job handle
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
    private static partial bool CloseHandle(nint handle);
}
