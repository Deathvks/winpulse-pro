using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WinPulseOptimizer
{
    public record SystemInfo(string Cpu, string GpuSummary, string Ram, string OsBuild, bool IsPluggedIn);
    public record GpuTelemetry(string? Name, double? TempC, double? UsedVramGb, double? TotalVramGb,
        double? LoadPercent, double? ClockMhz)
    {
        internal static GpuTelemetry Unavailable { get; } = new(null, null, null, null, null, null);
    }
    public record SystemSnapshot(DateTime Timestamp, long SampleOrder, float CpuPercent, double CpuGhz, double UsedRamGb, double FreeRamGb, double TotalRamGb, int ProcessCount, string PowerScheme, GpuTelemetry Gpu);
    public record ProcessItem(int Pid, string Name, double RamMb, string Category);

    public static class HardwareMonitor
    {
        private static readonly PerformanceCounter? CpuCounter = CreateCpuCounter();
        private static readonly PerformanceCounter? CpuPerfCounter = CreateCpuPerfCounter();
        private static readonly double BaseCpuMhz = GetBaseCpuMhz();

        private static readonly HashSet<string> ProtectedProcs = new(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "smss", "csrss", "wininit", "services", "lsass", "svchost",
            "winlogon", "explorer", "dwm", "audiodg", "MsMpEng", "SecurityHealthService", "WinPulseOptimizer"
        };

        private static readonly HashSet<string> AdditionalProtectedMemoryApps = new(StringComparer.OrdinalIgnoreCase)
        {
            "ApplicationFrameHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SystemSettings",
            "TextInputHost", "RuntimeBroker", "Taskmgr", "resmon"
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MemoryStatusEx
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus sps);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

        private static PerformanceCounter? CreateCpuCounter()
        {
            try { var pc = new PerformanceCounter("Processor", "% Processor Time", "_Total"); pc.NextValue(); return pc; }
            catch { return null; }
        }

        private static PerformanceCounter? CreateCpuPerfCounter()
        {
            try { var pc = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total"); pc.NextValue(); return pc; }
            catch { return null; }
        }

        private static double GetBaseCpuMhz()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
                foreach (ManagementObject o in searcher.Get())
                {
                    if (double.TryParse(o["MaxClockSpeed"]?.ToString(), out double mhz) && mhz > 0) return mhz;
                }
            }
            catch { }
            return 2400.0;
        }

        private static readonly HashSet<string> KnownGameExes = new(StringComparer.OrdinalIgnoreCase)
        {
            "RainbowSix", "RainbowSix_BE", "RainbowSix_Vulkan", "FortniteClient-Win64-Shipping",
            "NMS", "VALORANT-Win64-Shipping", "cs2", "r5apex", "GTA5", "RDR2", "Cyberpunk2077",
            "Overwatch", "League of Legends", "RocketLeague", "eldenring", "bg3", "bg3_dx11",
            "helldivers2", "cod", "ModernWarfare", "javaw", "Minecraft.Windows", "ForzaHorizon5",
            "FC24", "FC25", "EAFC24", "EAFC25", "RustClient", "EscapeFromTarkov", "DayZ_x64"
        };

        public static string NormalizeExecutableName(string name)
        {
            name = System.IO.Path.GetFileName(name.Trim().Trim('"'));
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }

        internal static bool? MatchGameRule(string name, IReadOnlySet<string> included, IReadOnlySet<string> excluded) =>
            excluded.Contains(name) ? false : included.Contains(name) || KnownGameExes.Contains(name) ? true : null;

        internal static bool CanAdjustBackgroundMemory(string name) =>
            !ProtectedProcs.Contains(name) && !AdditionalProtectedMemoryApps.Contains(name) && !KnownGameExes.Contains(name);

        internal static bool IsGameInstallPath(string path) =>
            path.Contains(@"steamapps\common", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"Epic Games\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"Riot Games\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"Ubisoft Game Launcher\games\", StringComparison.OrdinalIgnoreCase);

        public static string? DetectRunningGame(IEnumerable<string> included, IEnumerable<string> excluded,
            IEnumerable<string>? preferred = null)
        {
            var allow = included.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deny = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var order = preferred?.Where(name => !deny.Contains(name)).ToArray() ?? [];
            var preferredNames = order.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var runningPreferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var current = Process.GetCurrentProcess();
            int sessionId = current.SessionId;
            string? detected = null;
            foreach (var p in Process.GetProcesses())
            {
                using var process = p;
                try
                {
                    if (detected != null && order.Length == 0) continue;
                    if (p.Id <= 4 || p.SessionId != sessionId) continue;
                    string name = p.ProcessName;
                    if (preferredNames.Contains(name))
                    {
                        runningPreferred.Add(name);
                        continue;
                    }
                    if (detected != null) continue;
                    bool? rule = MatchGameRule(name, allow, deny);
                    if (rule.HasValue)
                    {
                        if (rule.Value) detected = name;
                        continue;
                    }

                    // Detectar ejecutables lanzados desde carpetas de instalación de juegos (excluyendo launchers en segundo plano)
                    if (p.WorkingSet64 > 350 * 1048576L && p.MainWindowHandle != IntPtr.Zero)
                    {
                        string path = p.MainModule?.FileName ?? "";
                        if (IsGameInstallPath(path))
                        {
                            if (!name.Contains("Launcher", StringComparison.OrdinalIgnoreCase) &&
                                !name.Contains("Client", StringComparison.OrdinalIgnoreCase) &&
                                !name.Equals("Steam", StringComparison.OrdinalIgnoreCase))
                            {
                                detected = name;
                            }
                        }
                    }
                }
                catch { }
            }
            return order.FirstOrDefault(runningPreferred.Contains) ?? detected;
        }

        public static bool IsAcOnline() => GetAcState() == true;

        public static bool? GetAcState() => GetSystemPowerStatus(out var status) && status.ACLineStatus <= 1
            ? status.ACLineStatus == 1 : null;

        internal static bool? HasSystemBattery() => GetSystemPowerStatus(out var status)
            ? InterpretBatteryFlag(status.BatteryFlag) : null;

        internal static bool? InterpretBatteryFlag(byte flag) => flag == 255 ? null : (flag & 128) == 0;

        internal static int? ReadDischargingBatteryPercentage()
        {
            if (!GetSystemPowerStatus(out var status) || status.ACLineStatus != 0 ||
                status.BatteryFlag == 255 || (status.BatteryFlag & 128) != 0 || status.BatteryLifePercent > 100)
                return null;
            return status.BatteryLifePercent;
        }

        private static string? FindNvidiaSmiPath()
        {
            const string fileName = "nvidia-smi.exe";
            string system = Path.Combine(Environment.SystemDirectory, fileName);
            if (File.Exists(system)) return system;
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", fileName);
            if (File.Exists(installed)) return installed;

            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                string directory = entry.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(directory)) continue;
                string candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static readonly string? NvidiaSmiExe = FindNvidiaSmiPath();
        internal static bool HasNvidiaTelemetryTool => NvidiaSmiExe != null;

        public static GpuTelemetry GetNvidiaTelemetry()
        {
            if (NvidiaSmiExe == null) return GpuTelemetry.Unavailable;
            try
            {
                using var p = Process.Start(new ProcessStartInfo(NvidiaSmiExe, "--query-gpu=name,temperature.gpu,memory.used,memory.total,utilization.gpu,clocks.current.graphics --format=csv,noheader,nounits")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                });
                if (p == null) return GpuTelemetry.Unavailable;
                var reading = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(2500))
                {
                    p.Kill();
                    return GpuTelemetry.Unavailable;
                }
                string output = reading.WaitAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult();
                return ParseNvidiaTelemetry(output);
            }
            catch { }
            return GpuTelemetry.Unavailable;
        }

        internal static GpuTelemetry ParseNvidiaTelemetry(string output)
        {
            static double? ReadValue(string field) =>
                double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
                    ? value : null;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = line.Split(',', StringSplitOptions.TrimEntries);
                if (fields.Length != 6 || string.IsNullOrEmpty(fields[0])) continue;
                return new GpuTelemetry(fields[0], ReadValue(fields[1]), ReadValue(fields[2]) / 1024.0,
                    ReadValue(fields[3]) / 1024.0, ReadValue(fields[4]), ReadValue(fields[5]));
            }
            return GpuTelemetry.Unavailable;
        }

        internal static ulong? ReadUsedMemoryBytes()
        {
            var memory = new MemoryStatusEx();
            return GlobalMemoryStatusEx(memory) && memory.ullAvailPhys <= memory.ullTotalPhys
                ? memory.ullTotalPhys - memory.ullAvailPhys : null;
        }

        internal static bool IsPhysicalMemoryLow()
        {
            const ulong MiB = 1024 * 1024;
            var memory = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(memory) || memory.ullTotalPhys == 0) return false;
            ulong threshold = Math.Clamp(memory.ullTotalPhys / 20, 384 * MiB, 1024 * MiB);
            return memory.ullAvailPhys < threshold;
        }

        public static SystemSnapshot CaptureSnapshot(string powerScheme, bool includeGpu = true)
        {
            var mem = new MemoryStatusEx();
            GlobalMemoryStatusEx(mem);
            double totalGb = Math.Round(mem.ullTotalPhys / 1073741824.0, 2);
            double freeGb = Math.Round(mem.ullAvailPhys / 1073741824.0, 2);
            double usedGb = Math.Round(totalGb - freeGb, 2);
            float cpu = 0f;
            double cpuGhz = Math.Round(BaseCpuMhz / 1000.0, 2);
            try
            {
                cpu = (float)Math.Round(CpuCounter?.NextValue() ?? 0f, 1);
                float perfPct = CpuPerfCounter?.NextValue() ?? 100f;
                if (perfPct > 5f) cpuGhz = Math.Round((BaseCpuMhz * (perfPct / 100.0)) / 1000.0, 2);
            }
            catch { }
            var processes = Process.GetProcesses();
            var timestamp = DateTime.Now;
            long sampleOrder = Stopwatch.GetTimestamp();
            int procs = processes.Length;
            foreach (var process in processes) process.Dispose();

            var gpu = includeGpu ? GetNvidiaTelemetry() : GpuTelemetry.Unavailable;
            return new SystemSnapshot(timestamp, sampleOrder, cpu, cpuGhz, usedGb, freeGb, totalGb, procs, powerScheme, gpu);
        }

        public static List<ProcessItem> GetTopMemoryProcesses(int count = 15)
        {
            var list = new List<ProcessItem>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id <= 4 || ProtectedProcs.Contains(p.ProcessName)) continue;
                    double mb = Math.Round(p.WorkingSet64 / 1048576.0, 1);
                    if (mb < 15) continue;
                    string cat = p.SessionId == 0 ? "Servicio" : "Usuario";
                    list.Add(new ProcessItem(p.Id, p.ProcessName, mb, cat));
                }
                catch { }
            }
            return list.OrderByDescending(x => x.RamMb).Take(count).ToList();
        }

        public static SystemInfo GetSystemInfo()
        {
            string cpu = QueryWmi("Win32_Processor", "Name").FirstOrDefault() ?? "CPU Desconocida";
            var gpus = QueryWmi("Win32_VideoController", "Name").Distinct().ToArray();
            string gpuSummary = gpus.Length > 0 ? string.Join(" + ", gpus) : "GPU Desconocida";

            var mem = new MemoryStatusEx();
            GlobalMemoryStatusEx(mem);
            string ram = $"{Math.Round(mem.ullTotalPhys / 1073741824.0)} GB RAM";

            string displayVer = Environment.OSVersion.Version.Build >= 26100 ? "11 (Build " + Environment.OSVersion.Version.Build + ")" : "11";
            return new SystemInfo(cpu.Trim(), gpuSummary, ram, $"Windows {displayVer}", IsAcOnline());
        }

        private static string[] QueryWmi(string table, string prop)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT {prop} FROM {table}");
                return searcher.Get().Cast<ManagementObject>()
                    .Select(o => o[prop]?.ToString()?.Trim() ?? "")
                    .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            }
            catch { return Array.Empty<string>(); }
        }
    }
}
