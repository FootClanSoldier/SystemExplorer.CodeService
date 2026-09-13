using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SystemExplorer.CodeService;

internal sealed class WindowsProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint TerminationExitCode = 1;
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(25);

    private readonly SafeFileHandle _jobHandle;
    private int _disposed;

    private WindowsProcessJob(SafeFileHandle jobHandle)
    {
        _jobHandle = jobHandle;
    }

    public static WindowsProcessJob CreateKillOnClose()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Job Objects are only available on Windows.");
        }

        IntPtr rawHandle = CreateJobObjectW(IntPtr.Zero, null);
        if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to create the Roslyn generation Job Object.");
        }

        SafeFileHandle jobHandle = new(rawHandle, ownsHandle: true);

        try
        {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION limitInformation = new()
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };

            if (!SetInformationJobObject(
                    jobHandle,
                    JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                    ref limitInformation,
                    checked((uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Failed to configure the Roslyn generation Job Object for kill-on-close containment.");
            }

            return new WindowsProcessJob(jobHandle);
        }
        catch
        {
            jobHandle.Dispose();
            throw;
        }
    }

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ThrowIfDisposed();

        if (!AssignProcessToJobObject(_jobHandle, process.SafeHandle))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Failed to assign Roslyn Language Server process {process.Id} to its generation Job Object.");
        }
    }

    public async Task TerminateRemainingProcessesAndWaitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "timeout must be non-negative.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (QueryActiveProcessCount() == 0)
        {
            return;
        }

        if (!TerminateJobObject(_jobHandle, TerminationExitCode))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to terminate remaining Roslyn generation Job Object processes.");
        }

        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (QueryActiveProcessCount() == 0)
            {
                return;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed >= timeout)
            {
                throw new TimeoutException(
                    $"Roslyn generation Job Object still had active processes after {timeout.TotalMilliseconds:0} ms.");
            }

            TimeSpan remaining = timeout - elapsed;
            TimeSpan delay = remaining < PollDelay ? remaining : PollDelay;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _jobHandle.Dispose();
    }

    private uint QueryActiveProcessCount()
    {
        ThrowIfDisposed();

        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accountingInformation = default;
        if (!QueryInformationJobObject(
                _jobHandle,
                JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation,
                ref accountingInformation,
                checked((uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>()),
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to query active Roslyn generation Job Object processes.");
        }

        return accountingInformation.ActiveProcesses;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0 || _jobHandle.IsClosed || _jobHandle.IsInvalid)
        {
            throw new ObjectDisposedException(nameof(WindowsProcessJob));
        }
    }

    private enum JOBOBJECTINFOCLASS
    {
        JobObjectBasicAccountingInformation = 1,
        JobObjectExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateJobObjectW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle hJob,
        JOBOBJECTINFOCLASS jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle hJob,
        SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(
        SafeFileHandle hJob,
        uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle hJob,
        JOBOBJECTINFOCLASS jobObjectInfoClass,
        ref JOBOBJECT_BASIC_ACCOUNTING_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength,
        out uint lpReturnLength);
}
