using System.Runtime.InteropServices;

namespace EasyService.Core;

/// <summary>One measurement of what the supervised application costs the machine.</summary>
public readonly record struct ResourceSample(double CpuPercent, long WorkingSetBytes, int ProcessCount, double CpuSecondsTotal);

/// <summary>
/// Measures CPU and memory of the supervised process <em>tree</em>, not just the process we
/// started. An administrator asking "what is this service costing me" means the whole tree -
/// a batch file that spawns java.exe would otherwise report zero.
///
/// The job object we already use for clean shutdown doubles as the accounting boundary, so
/// this costs one extra syscall per sample.
/// </summary>
internal sealed class ResourceSampler
{
    private long _previousCpu100ns = -1;
    private DateTime _previousSampleUtc;

    /// <summary>
    /// CPU percentage is relative to the whole machine: 100 % means every core is busy.
    /// That matches what Task-Manager and most monitoring systems show for a host.
    /// </summary>
    public ResourceSample Sample(IntPtr job, IntPtr process)
    {
        var (cpu100ns, processCount) = ReadCpu(job, process);
        var workingSet = ReadWorkingSet(job, process);

        var now = DateTime.UtcNow;
        double percent = 0;

        if (cpu100ns >= 0)
        {
            if (_previousCpu100ns >= 0)
            {
                var elapsed = (now - _previousSampleUtc).TotalSeconds;
                var cpuSeconds = (cpu100ns - _previousCpu100ns) / 10_000_000.0;
                if (elapsed > 0.05 && cpuSeconds >= 0)
                    percent = Math.Clamp(cpuSeconds / elapsed / System.Environment.ProcessorCount * 100.0, 0, 100);
            }
            _previousCpu100ns = cpu100ns;
            _previousSampleUtc = now;
        }

        return new ResourceSample(
            Math.Round(percent, 2),
            workingSet,
            processCount,
            cpu100ns >= 0 ? Math.Round(cpu100ns / 10_000_000.0, 2) : 0);
    }

    /// <summary>Resets the delta baseline, e.g. after the application was restarted.</summary>
    public void Reset() => _previousCpu100ns = -1;

    private static (long Cpu100ns, int ProcessCount) ReadCpu(IntPtr job, IntPtr process)
    {
        if (job != IntPtr.Zero)
        {
            var size = Marshal.SizeOf<Native.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (Native.QueryInformationJobObject(job, Native.JobObjectBasicAccountingInformation, buffer, (uint)size, out _))
                {
                    var info = Marshal.PtrToStructure<Native.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(buffer);
                    return (info.TotalUserTime + info.TotalKernelTime, (int)info.ActiveProcesses);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        if (process != IntPtr.Zero && Native.GetProcessTimes(process, out _, out _, out var kernel, out var user))
            return (kernel + user, 1);

        return (-1, 0);
    }

    private static long ReadWorkingSet(IntPtr job, IntPtr process)
    {
        long total = 0;
        var counted = false;

        foreach (var pid in ProcessIdsInJob(job))
        {
            var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) continue;
            try
            {
                if (Native.GetProcessMemoryInfo(handle, out var counters, (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS>()))
                {
                    total += (long)counters.WorkingSetSize;
                    counted = true;
                }
            }
            finally { Native.CloseHandle(handle); }
        }

        if (counted) return total;

        // No job, or the job list was empty: fall back to the process we started ourselves.
        if (process != IntPtr.Zero &&
            Native.GetProcessMemoryInfo(process, out var single, (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS>()))
            return (long)single.WorkingSetSize;

        return 0;
    }

    private static List<uint> ProcessIdsInJob(IntPtr job)
    {
        var result = new List<uint>();
        if (job == IntPtr.Zero) return result;

        // JOBOBJECT_BASIC_PROCESS_ID_LIST is variable length: two DWORDs (padded to the
        // pointer alignment) followed by the ULONG_PTR array.
        const int headerSize = 8;
        var capacity = 64;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var size = headerSize + capacity * IntPtr.Size;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!Native.QueryInformationJobObject(job, Native.JobObjectBasicProcessIdList, buffer, (uint)size, out _))
                {
                    if (Marshal.GetLastWin32Error() != Native.ERROR_MORE_DATA) return result;

                    var assigned = (uint)Marshal.ReadInt32(buffer, 0);
                    capacity = Math.Max(capacity * 2, (int)assigned + 16);
                    continue;
                }

                var inList = Math.Min((int)(uint)Marshal.ReadInt32(buffer, 4), capacity);
                for (var i = 0; i < inList; i++)
                {
                    var id = (uint)(ulong)Marshal.ReadIntPtr(buffer, headerSize + i * IntPtr.Size);
                    if (id != 0) result.Add(id);
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        return result;
    }
}
