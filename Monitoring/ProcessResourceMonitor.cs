using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WinPulseOptimizer;

internal sealed record ProcessResourceUsage(int Pid, string Name, double RamMb, double? CpuPercent, double? IoMbPerSecond);

internal static partial class ProcessResourceMonitor
{
    internal const int DurationSeconds = 6;
    private const uint QueryLimitedInformation = 0x1000;
    private const double BytesPerMiB = 1024 * 1024;

    private sealed record Sample(string Name, double RamMb, long Timestamp, long? Created, long CpuTicks, ulong? IoBytes);

    internal static long? ReadRunningCreationTime(int pid)
    {
        const uint Synchronize = 0x00100000;
        const uint WaitTimeout = 0x00000102;
        using var handle = OpenProcess(QueryLimitedInformation | Synchronize, false, pid);
        return !handle.IsInvalid && GetProcessTimes(handle, out long creation, out _, out _, out _) &&
            WaitForSingleObject(handle, 0) == WaitTimeout ? creation : null;
    }

    internal static async Task<ProcessResourceUsage[]> MeasureAsync(CancellationToken cancellation)
    {
        var before = ReadProcesses(cancellation);
        await Task.Delay(TimeSpan.FromSeconds(DurationSeconds), cancellation).ConfigureAwait(false);
        var after = ReadProcesses(cancellation);
        uint processors = GetActiveProcessorCount(ushort.MaxValue);

        return after.Select(pair =>
        {
            var (pid, end) = pair;
            double? cpu = null;
            double? io = null;
            if (before.TryGetValue(pid, out var start) && start.Created.HasValue && start.Created == end.Created)
            {
                double elapsed = Stopwatch.GetElapsedTime(start.Timestamp, end.Timestamp).TotalSeconds;
                if (end.CpuTicks >= start.CpuTicks && elapsed > 0 && processors > 0)
                    cpu = 100 * (end.CpuTicks - start.CpuTicks) / (TimeSpan.TicksPerSecond * elapsed * processors);
                if (start.IoBytes is ulong first && end.IoBytes is ulong last && last >= first && elapsed > 0)
                    io = (last - first) / (BytesPerMiB * elapsed);
            }
            return new ProcessResourceUsage(pid, end.Name, end.RamMb, cpu, io);
        }).OrderByDescending(row => row.CpuPercent).ThenByDescending(row => row.RamMb).ToArray();
    }

    private static Dictionary<int, Sample> ReadProcesses(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var samples = new Dictionary<int, Sample>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                cancellation.ThrowIfCancellationRequested();
                if (process.Id == 0) continue;
                try
                {
                    string name = process.ProcessName;
                    double ram = process.WorkingSet64 / BytesPerMiB;
                    using var handle = OpenProcess(QueryLimitedInformation, false, process.Id);
                    long timestamp = Stopwatch.GetTimestamp();
                    if (handle.IsInvalid || !GetProcessTimes(handle, out long created, out _, out long kernel, out long user))
                    {
                        samples[process.Id] = new(name, ram, timestamp, null, 0, null);
                        continue;
                    }

                    ulong? io = GetProcessIoCounters(handle, out var counters) ? counters.ReadBytes + counters.WriteBytes : null;
                    samples[process.Id] = new(name, ram, timestamp, created, kernel + user, io);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // El proceso terminó o Windows restringió el acceso durante la lectura.
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return samples;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations;
        public ulong ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort group);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
