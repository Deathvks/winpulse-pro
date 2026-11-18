using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinPulseOptimizer;

internal sealed record SmartGameSchedulingStatus(bool Active, string GameName, bool GameBoosted,
    bool GameProtected, int ThrottledCount);

internal static partial class ProcessResourceMonitor
{
    private const uint SetInformation = 0x0200;
    private const int ProcessPowerThrottlingClass = 4;
    private const uint ProcessPowerThrottlingVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;

    private static readonly object SchedulingLock = new();
    private static BoostedGameEntry? _boostedGame;
    private static readonly Dictionary<int, ThrottledAppEntry> ThrottledApps = new();

    private static readonly HashSet<string> RealtimeAppExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64", "obs32", "streamlabs obs", "discord", "discordptb", "discordcanary",
        "teamspeak", "ts3client_win64", "voicemeeter", "voicemeeterpro", "voicemeeter8",
        "nvidia broadcast", "nvcontainer", "steelseriesgg", "lghub", "icue", "razer synapse",
        "audiodg", "WinPulseOptimizer"
    };

    private sealed record BoostedGameEntry(int Pid, string Name, long? CreationTime,
        ProcessPriorityClass? OriginalPriority, bool Boosted, bool Protected);

    private sealed record ThrottledAppEntry(int Pid, string Name, long? CreationTime,
        ProcessPriorityClass OriginalPriority, bool AppliedEcoQos);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(SafeProcessHandle hProcess, int processInformationClass,
        ref ProcessPowerThrottlingState processInformation, uint processInformationSize);

    internal static bool HasActiveSmartGameScheduling
    {
        get
        {
            lock (SchedulingLock) return _boostedGame != null || ThrottledApps.Count > 0;
        }
    }

    internal static SmartGameSchedulingStatus ApplySmartGameScheduling(RunningGame game,
        IReadOnlySet<string> excludedGames)
    {
        lock (SchedulingLock)
        {
            if (_boostedGame != null && (_boostedGame.Pid != game.Pid || _boostedGame.CreationTime != game.CreationTime))
                RestoreAllLocked();

            _boostedGame ??= TryBoostGameProcess(game);
            ReconcileThrottledAppsLocked(game.Name, excludedGames);
            return new SmartGameSchedulingStatus(true, _boostedGame.Name, _boostedGame.Boosted,
                _boostedGame.Protected, ThrottledApps.Count);
        }
    }

    internal static void RestoreSmartGameScheduling()
    {
        lock (SchedulingLock) RestoreAllLocked();
    }

    private static BoostedGameEntry TryBoostGameProcess(RunningGame game)
    {
        if (!game.CreationTime.HasValue)
            return new(game.Pid, game.Name, game.CreationTime, null, false, false);

        try
        {
            using var proc = Process.GetProcessById(game.Pid);
            if (ReadRunningCreationTime(game.Pid) != game.CreationTime)
                return new(game.Pid, game.Name, game.CreationTime, null, false, false);

            var original = proc.PriorityClass;
            if (original is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal)
            {
                proc.PriorityClass = ProcessPriorityClass.AboveNormal;
                return new(game.Pid, game.Name, game.CreationTime, original, true, false);
            }
            return new(game.Pid, game.Name, game.CreationTime, null, false, false);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            return new(game.Pid, game.Name, game.CreationTime, null, false, true);
        }
        catch
        {
            return new(game.Pid, game.Name, game.CreationTime, null, false, false);
        }
    }

    private static void ReconcileThrottledAppsLocked(string activeGameName, IReadOnlySet<string> excludedGames)
    {
        string? foreground = GetForegroundProcessName();
        foreach (int pid in ThrottledApps.Keys.ToArray())
        {
            var entry = ThrottledApps[pid];
            if (!IsProcessSameAndMinimized(entry, foreground))
            {
                RestoreBackgroundApp(entry);
                ThrottledApps.Remove(pid);
            }
        }

        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                try
                {
                    if (proc.Id <= 4 || proc.SessionId != session || ThrottledApps.ContainsKey(proc.Id)) continue;
                    string name = proc.ProcessName;
                    if (string.Equals(name, activeGameName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, foreground, StringComparison.OrdinalIgnoreCase) ||
                        excludedGames.Contains(name) || RealtimeAppExclusions.Contains(name) ||
                        !HardwareMonitor.CanAdjustBackgroundMemory(name))
                    {
                        continue;
                    }

                    if (proc.WorkingSet64 < 120 * BytesPerMiB) continue;
                    IntPtr window = proc.MainWindowHandle;
                    if (!IsMinimizedWindow(window, proc.Id)) continue;
                    if (!IsEligibleAppPath(proc.MainModule?.FileName)) continue;

                    long? creation = ReadRunningCreationTime(proc.Id);
                    if (!creation.HasValue) continue;
                    var originalPriority = proc.PriorityClass;
                    if (originalPriority != ProcessPriorityClass.Normal) continue;
                    if (!IsFullyMinimized(new(proc.Id, name, window, 0, creation.Value, 0, 0))) continue;

                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                    bool ecoQos = TrySetEcoQos(proc.Id, enabled: true);
                    ThrottledApps[proc.Id] = new(proc.Id, name, creation, originalPriority, ecoQos);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static bool IsProcessSameAndMinimized(ThrottledAppEntry entry, string? foreground)
    {
        if (string.Equals(entry.Name, foreground, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var proc = Process.GetProcessById(entry.Pid);
            if (ReadRunningCreationTime(entry.Pid) != entry.CreationTime) return false;
            IntPtr window = proc.MainWindowHandle;
            return IsMinimizedWindow(window, entry.Pid) &&
                IsFullyMinimized(new(entry.Pid, entry.Name, window, 0, entry.CreationTime ?? 0, 0, 0));
        }
        catch { return false; }
    }

    private static void RestoreAllLocked()
    {
        if (_boostedGame is { Boosted: true, OriginalPriority: { } original } boosted)
        {
            try
            {
                if (ReadRunningCreationTime(boosted.Pid) == boosted.CreationTime)
                {
                    using var proc = Process.GetProcessById(boosted.Pid);
                    if (proc.PriorityClass == ProcessPriorityClass.AboveNormal)
                        proc.PriorityClass = original;
                }
            }
            catch { }
        }
        _boostedGame = null;

        foreach (var entry in ThrottledApps.Values)
            RestoreBackgroundApp(entry);
        ThrottledApps.Clear();
    }

    private static void RestoreBackgroundApp(ThrottledAppEntry entry)
    {
        try
        {
            if (ReadRunningCreationTime(entry.Pid) != entry.CreationTime) return;
            if (entry.AppliedEcoQos) TrySetEcoQos(entry.Pid, enabled: false);
            using var proc = Process.GetProcessById(entry.Pid);
            if (proc.PriorityClass == ProcessPriorityClass.BelowNormal)
                proc.PriorityClass = entry.OriginalPriority;
        }
        catch { }
    }

    private static bool TrySetEcoQos(int pid, bool enabled)
    {
        try
        {
            using var handle = OpenProcess(SetInformation, false, pid);
            if (handle.IsInvalid) return false;
            var state = new ProcessPowerThrottlingState
            {
                Version = ProcessPowerThrottlingVersion,
                ControlMask = enabled ? ProcessPowerThrottlingExecutionSpeed : 0u,
                StateMask = enabled ? ProcessPowerThrottlingExecutionSpeed : 0u
            };
            return SetProcessInformation(handle, ProcessPowerThrottlingClass, ref state,
                (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        catch { return false; }
    }
}
