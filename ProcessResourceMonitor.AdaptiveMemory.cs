using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WinPulseOptimizer;

internal sealed record BackgroundMemoryCandidate(int Pid, string Name, IntPtr Window, double RamMb, long Created,
    long CpuTicks, ulong IoOperations);

internal static partial class ProcessResourceMonitor
{
    internal static async Task<BackgroundMemoryCandidate?> FindQuietBackgroundAppAsync(IReadOnlySet<string> excluded)
    {
        var foreground = GetForegroundProcessName();
        if (foreground == null) return null;

        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        var candidates = new List<BackgroundMemoryCandidate>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id <= 4 || process.SessionId != session) continue;
                    string name = process.ProcessName;
                    if (excluded.Contains(name) || !HardwareMonitor.CanAdjustBackgroundMemory(name) ||
                        string.Equals(name, foreground, StringComparison.OrdinalIgnoreCase)) continue;

                    double ram = process.WorkingSet64 / BytesPerMiB;
                    if (ram < 256) continue;
                    var window = process.MainWindowHandle;
                    if (!IsMinimizedWindow(window, process.Id)) continue;

                    if (!IsEligibleAppPath(process.MainModule?.FileName)) continue;
                    using var handle = OpenProcess(QueryLimitedInformation, false, process.Id);
                    if (TryReadActivity(handle, out long created, out long cpuTicks, out ulong ioOperations))
                        candidates.Add(new(process.Id, name, window, ram, created, cpuTicks, ioOperations));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
        }

        if (candidates.Count == 0) return null;
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        foreground = GetForegroundProcessName();
        if (foreground == null || !HardwareMonitor.IsPhysicalMemoryLow()) return null;
        foreach (var candidate in candidates.OrderByDescending(app => app.RamMb))
        {
            if (string.Equals(candidate.Name, foreground, StringComparison.OrdinalIgnoreCase) ||
                !IsFullyMinimized(candidate)) continue;
            using var handle = OpenProcess(QueryLimitedInformation, false, candidate.Pid);
            if (IsQuiet(candidate, handle)) return candidate;
        }
        return null;
    }

    internal static bool TryAdjustBackgroundMemory(BackgroundMemoryCandidate candidate)
    {
        const uint SetQuota = 0x0100;
        using var handle = OpenProcess(QueryLimitedInformation | SetQuota, false, candidate.Pid);
        if (!IsQuiet(candidate, handle) || !HardwareMonitor.IsPhysicalMemoryLow()) return false;

        var foreground = GetForegroundProcessName();
        return foreground != null && !string.Equals(foreground, candidate.Name, StringComparison.OrdinalIgnoreCase) &&
            IsFullyMinimized(candidate) && EmptyWorkingSet(handle);
    }

    internal static bool IsEligibleAppPath(string? path) => !string.IsNullOrEmpty(path) &&
        !path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) &&
        !path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) &&
        !HardwareMonitor.IsGameInstallPath(path);

    private static bool IsQuiet(BackgroundMemoryCandidate candidate, SafeProcessHandle handle) =>
        TryReadActivity(handle, out long created, out long cpuTicks, out ulong ioOperations) &&
        created == candidate.Created && cpuTicks >= candidate.CpuTicks &&
        cpuTicks - candidate.CpuTicks <= 25 * TimeSpan.TicksPerMillisecond &&
        ioOperations == candidate.IoOperations;

    private static bool TryReadActivity(SafeProcessHandle handle, out long created, out long cpuTicks, out ulong ioOperations)
    {
        created = cpuTicks = 0;
        ioOperations = 0;
        if (handle.IsInvalid || !GetProcessTimes(handle, out created, out _, out long kernel, out long user) ||
            !GetProcessIoCounters(handle, out var counters)) return false;

        cpuTicks = kernel + user;
        ioOperations = counters.ReadOperations + counters.WriteOperations + counters.OtherOperations;
        return true;
    }

    private static bool IsMinimizedWindow(IntPtr window, int pid) =>
        window != IntPtr.Zero && GetWindowThreadProcessId(window, out int owner) != 0 && owner == pid && IsIconic(window);

    private static bool IsFullyMinimized(BackgroundMemoryCandidate candidate) =>
        IsMinimizedWindow(candidate.Window, candidate.Pid) && EnumWindows((window, _) =>
            !IsWindowVisible(window) || IsIconic(window) ||
            GetWindowThreadProcessId(window, out int owner) == 0 || owner != candidate.Pid, IntPtr.Zero);

    private static string? GetForegroundProcessName()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out int pid) == 0) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(SafeProcessHandle process);
}
