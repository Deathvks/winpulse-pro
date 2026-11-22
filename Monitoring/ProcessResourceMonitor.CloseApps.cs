using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WinPulseOptimizer;

internal sealed record CloseableAppTarget(int Pid, long? CreationTime, bool HasWindow);

internal sealed record CloseableAppGroup(
    string ProcessName,
    CloseableAppTarget[] Targets,
    double TotalRamMb,
    bool IsMinimized,
    bool IsForeground);

internal sealed record CloseAppsResult(
    int ClosedApps,
    int ClosedProcesses,
    int RemainingProcesses,
    ulong? BeforeBytes,
    ulong? AfterBytes);

internal static partial class ProcessResourceMonitor
{
    private const double MinCloseableAppRamMb = 50.0;

    internal static bool IsCloseableAppPath(string? path) =>
        !string.IsNullOrEmpty(path) &&
        !path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) &&
        !path.Contains(@"\WindowsApps\MicrosoftWindows.", StringComparison.OrdinalIgnoreCase) &&
        !HardwareMonitor.IsGameInstallPath(path);

    internal static bool IsCloseableAppCandidate(string name, string? path, IReadOnlySet<string> excluded,
        string? foregroundName, string? activeGameName)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!HardwareMonitor.CanAdjustBackgroundMemory(name) || RealtimeAppExclusions.Contains(name)) return false;
        if (excluded.Contains(name)) return false;
        if (!string.IsNullOrEmpty(activeGameName) && string.Equals(name, activeGameName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(foregroundName) && string.Equals(name, foregroundName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.StartsWith("nordvpn", StringComparison.OrdinalIgnoreCase)) return false;
        return IsCloseableAppPath(path);
    }

    internal static List<CloseableAppGroup> FindCloseableApps(IReadOnlySet<string> excluded, string? activeGameName)
    {
        string? foreground = GetForegroundProcessName();
        using var current = Process.GetCurrentProcess();
        int sessionId = current.SessionId;

        var grouped = new Dictionary<string, (List<CloseableAppTarget> Targets, double RamMb, bool HasWindow, bool Minimized, bool Foreground)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                try
                {
                    if (proc.Id <= 4 || proc.SessionId != sessionId) continue;
                    string name = proc.ProcessName;
                    string? path = proc.MainModule?.FileName;
                    if (!IsCloseableAppCandidate(name, path, excluded, foreground, activeGameName)) continue;

                    double ramMb = proc.WorkingSet64 / BytesPerMiB;
                    IntPtr window = proc.MainWindowHandle;
                    bool hasWindow = window != IntPtr.Zero && IsWindowVisible(window);
                    bool minimized = hasWindow && IsMinimizedWindow(window, proc.Id);
                    long? creation = ReadRunningCreationTime(proc.Id);
                    if (!creation.HasValue) continue;

                    if (!grouped.TryGetValue(name, out var entry))
                        entry = (new List<CloseableAppTarget>(), 0, false, false,
                            string.Equals(name, foreground, StringComparison.OrdinalIgnoreCase));

                    entry.Targets.Add(new CloseableAppTarget(proc.Id, creation, window != IntPtr.Zero));
                    entry.RamMb += ramMb;
                    entry.HasWindow |= hasWindow || window != IntPtr.Zero;
                    entry.Minimized |= minimized;
                    grouped[name] = entry;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or UnauthorizedAccessException)
                {
                }
            }
        }

        return grouped
            .Where(pair => pair.Value.HasWindow && pair.Value.RamMb >= MinCloseableAppRamMb)
            .Select(pair => new CloseableAppGroup(
                pair.Key,
                pair.Value.Targets.ToArray(),
                Math.Round(pair.Value.RamMb, 1),
                pair.Value.Minimized,
                pair.Value.Foreground))
            .OrderByDescending(group => group.TotalRamMb)
            .ToList();
    }

    internal static async Task<CloseAppsResult> CloseAppGroupsAsync(IReadOnlyList<CloseableAppGroup> selectedGroups)
    {
        ulong? before = HardwareMonitor.ReadUsedMemoryBytes();
        int closedApps = 0, closedProcesses = 0, remainingProcesses = 0;

        foreach (var group in selectedGroups)
        {
            int appClosed = 0, appRemaining = 0;
            var gracefulWaitList = new List<(Process Proc, long Creation)>();

            // Primero solicita cierre ordenado a las ventanas principales para no perder datos sin guardar.
            foreach (var target in group.Targets.OrderByDescending(t => t.HasWindow))
            {
                try
                {
                    if (target.CreationTime is not long expectedCreation ||
                        ReadRunningCreationTime(target.Pid) != expectedCreation)
                        continue;

                    var proc = Process.GetProcessById(target.Pid);
                    if (proc.MainWindowHandle != IntPtr.Zero && proc.CloseMainWindow())
                    {
                        gracefulWaitList.Add((proc, expectedCreation));
                    }
                    else
                    {
                        proc.Dispose();
                    }
                }
                catch { }
            }

            if (gracefulWaitList.Count > 0)
            {
                await Task.Delay(1600).ConfigureAwait(false);
                foreach (var (proc, _) in gracefulWaitList)
                {
                    using (proc)
                    {
                        try
                        {
                            if (proc.HasExited) appClosed++;
                        }
                        catch { }
                    }
                }
            }

            // Cierra procesos auxiliares o minimizados en bandeja restantes del mismo grupo elegido.
            // Si una ventana principal sigue visible (p. ej. pidiendo guardar un documento), se respeta sin forzar Kill().
            foreach (var target in group.Targets)
            {
                try
                {
                    if (target.CreationTime is not long expectedCreation ||
                        ReadRunningCreationTime(target.Pid) != expectedCreation)
                        continue;

                    using var proc = Process.GetProcessById(target.Pid);
                    if (proc.HasExited) continue;
                    proc.Refresh();
                    if (target.HasWindow && proc.MainWindowHandle != IntPtr.Zero)
                    {
                        appRemaining++;
                        continue;
                    }
                    proc.Kill();
                    if (proc.WaitForExit(2500)) appClosed++;
                    else appRemaining++;
                }
                catch
                {
                    if (target.CreationTime is long expected && ReadRunningCreationTime(target.Pid) == expected)
                        appRemaining++;
                }
            }

            closedProcesses += appClosed;
            remainingProcesses += appRemaining;
            if (appClosed > 0 && appRemaining == 0) closedApps++;
        }

        await Task.Delay(250).ConfigureAwait(false);
        ulong? after = HardwareMonitor.ReadUsedMemoryBytes();
        return new(closedApps, closedProcesses, remainingProcesses, before, after);
    }
}
