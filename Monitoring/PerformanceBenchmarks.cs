using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;

namespace WinPulseOptimizer
{
    public sealed record GameMeasurement(string Executable, string Scenario, string Profile, int Seconds,
        bool? OnAc, int Frames, double AverageFps, double LowFps, double MedianMs, double P95Ms, double P99Ms,
        DateTimeOffset CapturedAt, GameGpuTemperature? Temperature = null);

    public sealed record GameGpuTemperature(string Device, int Samples, double AverageC, double MaximumC);

    public sealed record GameComparison(Guid Id, GameMeasurement A, GameMeasurement? B = null)
    {
        [JsonIgnore]
        public string Label => $"{A.Executable}.exe · {A.CapturedAt:g} · {(B == null ? "Solo A" : "A y B")}";
    }

    public sealed record BatteryMeasurement(string Scenario, string Profile, int Seconds, int StartPercent, int EndPercent,
        double? AverageWatts, double? WattHours, DateTimeOffset CapturedAt);

    public sealed class BenchmarkResults
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public GameMeasurement? GameA { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public GameMeasurement? GameB { get; set; }
        public List<GameComparison> GameComparisons { get; set; } = new();
        public BatteryMeasurement? BatteryA { get; set; }
        public BatteryMeasurement? BatteryB { get; set; }

        internal void MigrateGameComparison()
        {
            GameComparisons ??= new();
            if (GameA is { } first && !GameComparisons.Any(comparison => comparison.A == first))
            {
                var second = GameB is { } legacy && PerformanceBenchmarks.MatchesGameRun(first, legacy.Executable,
                    legacy.Scenario, legacy.Seconds, legacy.OnAc) ? legacy : null;
                GameComparisons.Add(new(Guid.NewGuid(), first, second));
            }
            GameA = GameB = null;
        }
    }

    internal sealed record CaptureTarget(int Pid, string Executable, string WindowTitle)
    {
        public string Label => $"{Executable}.exe · {Pid} · {WindowTitle}";
    }

    internal static class PerformanceBenchmarks
    {
        private const int GameStartDelay = 5;

        internal static bool MatchesGameRun(GameMeasurement first, string executable, string scenario, int seconds, bool? onAc) =>
            first.Seconds == seconds && first.OnAc == onAc &&
            string.Equals(first.Executable, executable, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(first.Scenario, scenario, StringComparison.OrdinalIgnoreCase);

        public static CaptureTarget[] ListCaptureTargets()
        {
            var targets = new List<CaptureTarget>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero &&
                            !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                            targets.Add(new(process.Id, process.ProcessName, process.MainWindowTitle));
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            return targets.OrderBy(p => p.Executable, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static async Task<GameMeasurement> CaptureGameAsync(string tool, CaptureTarget target, string scenario,
            string profile, Guid activePlan, int seconds, CancellationToken cancellationToken)
        {
            using var game = Process.GetProcessById(target.Pid);
            if (!game.ProcessName.Equals(target.Executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("El proceso elegido ya cambió. Actualiza la lista de ventanas.");
            var onAc = HardwareMonitor.GetAcState();
            string csv = Path.Combine(Path.GetTempPath(), $"winpulse-{Guid.NewGuid():N}.csv");
            string session = $"WinPulse-{Guid.NewGuid():N}";
            try
            {
                var start = new ProcessStartInfo(tool)
                {
                    WorkingDirectory = Path.GetDirectoryName(tool)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                foreach (string arg in new[]
                {
                    "--process_id", target.Pid.ToString(CultureInfo.InvariantCulture),
                    "--delay", GameStartDelay.ToString(CultureInfo.InvariantCulture),
                    "--timed", seconds.ToString(CultureInfo.InvariantCulture), "--terminate_after_timed", "--terminate_on_proc_exit",
                    "--output_file", csv, "--v1_metrics", "--no_console_stats", "--no_track_input",
                    "--session_name", session
                })
                    start.ArgumentList.Add(arg);

                using var monitor = Process.Start(start) ?? throw new IOException("No se pudo iniciar PresentMon.");
                var stderr = monitor.StandardError.ReadToEndAsync();
                var stdout = monitor.StandardOutput.ReadToEndAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using var stopTemperature = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(seconds + GameStartDelay + 15));
                var sampling = SampleGameTemperatureAsync(seconds, stopTemperature.Token);
                GameGpuTemperature? temperature;
                try { await monitor.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!monitor.HasExited)
                        {
                            try { monitor.Kill(entireProcessTree: true); }
                            catch (InvalidOperationException) when (monitor.HasExited) { }
                            await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        }
                    }
                    finally { await StopTraceSessionAsync(session); }
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("PresentMon agotó el tiempo de captura.");
                }
                finally
                {
                    stopTemperature.Cancel();
                    temperature = await sampling;
                }
                var output = await Task.WhenAll(stderr, stdout).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                if (monitor.ExitCode != 0)
                {
                    string details = string.Join(" ", output).Trim();
                    if (!WindowsElevation.IsAdministrator && details.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("PresentMon necesita permisos de captura. Pide al administrador que habilite el grupo Performance Log Users para esta cuenta.");
                    throw new IOException($"PresentMon terminó con código {monitor.ExitCode}. {details}");
                }
                if (!File.Exists(csv))
                    throw new IOException("PresentMon terminó sin registrar fotogramas para ese proceso.");
                if (onAc != HardwareMonitor.GetAcState() || activePlan != ReadActivePlan())
                    throw new InvalidOperationException("Cambió el plan o la fuente de energía durante la captura. Repite la pasada.");
                return ReadGameCsv(csv, target.Pid, target.Executable, scenario, profile, seconds, onAc, cancellationToken)
                    with { Temperature = temperature };
            }
            finally
            {
                try { File.Delete(csv); } catch (IOException) { }
            }
        }

        private static async Task<GameGpuTemperature?> SampleGameTemperatureAsync(int seconds, CancellationToken token)
        {
            if (!HardwareMonitor.HasNvidiaTelemetryTool) return null;
            var samples = new List<(string Device, double Temperature)>();
            var clock = Stopwatch.StartNew();
            TimeSpan end = TimeSpan.FromSeconds(GameStartDelay + seconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(GameStartDelay), token);
                while (clock.Elapsed < end && !token.IsCancellationRequested)
                {
                    var gpu = HardwareMonitor.GetNvidiaTelemetry();
                    if (clock.Elapsed >= end || token.IsCancellationRequested) break;
                    if (gpu.Name is { } device && gpu.TempC is double temperature) samples.Add((device, temperature));
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            return SummarizeGameTemperature(samples);
        }

        internal static GameGpuTemperature? SummarizeGameTemperature(IEnumerable<(string Device, double Temperature)> samples)
        {
            var device = samples.GroupBy(sample => sample.Device, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count()).FirstOrDefault();
            return device == null ? null : new(device.Key, device.Count(),
                device.Average(sample => sample.Temperature), device.Max(sample => sample.Temperature));
        }

        private static async Task StopTraceSessionAsync(string session)
        {
            try
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "logman.exe"))
                    { UseShellExecute = false, CreateNoWindow = true };
                foreach (string arg in new[] { "stop", session, "-ets" }) start.ArgumentList.Add(arg);
                using var cleanup = Process.Start(start);
                if (cleanup != null) await cleanup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
        }

        internal static GameMeasurement ReadGameCsv(string path, int pid, string executable, string scenario, string profile,
            int seconds, bool? onAc, CancellationToken cancellationToken)
        {
            using var parser = new TextFieldParser(path);
            parser.SetDelimiters(",");
            var header = parser.ReadFields() ?? Array.Empty<string>();
            int process = Array.IndexOf(header, "ProcessID");
            int chain = Array.IndexOf(header, "SwapChainAddress");
            int timing = Array.FindIndex(header, column => column.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
            int timestamp = Array.IndexOf(header, "TimeInSeconds");
            if (new[] { process, chain, timing, timestamp }.Any(i => i < 0))
                throw new FormatException("El CSV no contiene las columnas de PresentMon 1.x necesarias.");
            int lastColumn = new[] { process, chain, timing, timestamp }.Max();
            var chains = new Dictionary<string, List<(double Time, double Ms)>>();
            while (!parser.EndOfData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = parser.ReadFields();
                if (row == null || row.Length <= lastColumn || row[process] != pid.ToString(CultureInfo.InvariantCulture) ||
                    !TryNumber(row[timing], out double ms) || ms <= 0 || !TryNumber(row[timestamp], out double time))
                    continue;
                if (!chains.TryGetValue(row[chain], out var frames))
                    chains[row[chain]] = frames = new();
                frames.Add((time, ms));
            }
            var main = chains.Values.OrderByDescending(frames => frames.Count).FirstOrDefault();
            if (main == null || main.Count < 100 || main.Max(f => f.Time) - main.Min(f => f.Time) < seconds * 0.8)
                throw new InvalidOperationException("No hay una pasada completa. Mantén el juego activo durante toda la captura.");
            var values = main.Select(f => f.Ms).OrderBy(ms => ms).ToArray();
            int slowest = (int)Math.Ceiling(values.Length / 100.0);
            return new(executable, scenario, profile, seconds, onAc, values.Length,
                1000 / values.Average(), 1000 / values.TakeLast(slowest).Average(), Percentile(values, 0.5),
                Percentile(values, 0.95), Percentile(values, 0.99), DateTimeOffset.Now);
        }

        private static bool TryNumber(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

        private static double Percentile(double[] sorted, double percentile)
        {
            double index = (sorted.Length - 1) * percentile;
            int floor = (int)index;
            return sorted[floor] + (sorted[(int)Math.Ceiling(index)] - sorted[floor]) * (index - floor);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemBatteryState
        {
            public byte AcOnLine, BatteryPresent, Charging, Discharging;
            public byte Spare0, Spare1, Spare2, Tag;
            public uint MaxCapacity, RemainingCapacity;
            public int Rate;
            public uint EstimatedTime, DefaultAlert1, DefaultAlert2;
        }

        [DllImport("powrprof.dll")]
        private static extern uint CallNtPowerInformation(int informationLevel, IntPtr input, uint inputSize,
            out SystemBatteryState output, uint outputSize);

        private static double? ReadDischargeWatts()
        {
            const int systemBatteryState = 5;
            uint status = CallNtPowerInformation(systemBatteryState, IntPtr.Zero, 0, out var battery,
                (uint)Marshal.SizeOf<SystemBatteryState>());
            return status == 0 && battery.BatteryPresent != 0 && battery.AcOnLine == 0 && battery.Discharging != 0 &&
                battery.Rate is < 0 and > int.MinValue ? -(double)battery.Rate / 1000 : null;
        }

        public static async Task<BatteryMeasurement> MeasureBatteryAsync(string scenario, string profile, Guid activePlan,
            int seconds, IProgress<int> remainingSeconds, CancellationToken cancellationToken)
        {
            int startPercent = ReadBatteryPercent();
            int endPercent = startPercent;
            var timer = Stopwatch.StartNew();
            double? previousWatts = ReadDischargeWatts();
            double previousSecond = 0, measuredSeconds = 0, wattSeconds = 0;
            while (previousSecond < seconds)
            {
                remainingSeconds.Report((int)Math.Ceiling(seconds - previousSecond));
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(3, seconds - previousSecond)), cancellationToken);
                double elapsed = timer.Elapsed.TotalSeconds;
                if (elapsed - previousSecond > 10)
                    throw new InvalidOperationException("La medición se interrumpió. Repite la sesión con el equipo despierto.");
                endPercent = ReadBatteryPercent();
                var watts = ReadDischargeWatts();
                if (previousWatts.HasValue && watts.HasValue)
                {
                    wattSeconds += (previousWatts.Value + watts.Value) / 2 * (elapsed - previousSecond);
                    measuredSeconds += elapsed - previousSecond;
                }
                previousWatts = watts;
                previousSecond = elapsed;
            }
            if (activePlan != ReadActivePlan())
                throw new InvalidOperationException("Cambió el plan de energía durante la medición.");
            bool enoughSamples = measuredSeconds >= previousSecond * 0.8;
            double? average = enoughSamples ? wattSeconds / measuredSeconds : null;
            return new(scenario, profile, seconds, startPercent, endPercent, average, average * previousSecond / 3600,
                DateTimeOffset.Now);
        }

        private static int ReadBatteryPercent() => HardwareMonitor.ReadDischargingBatteryPercentage() ??
            throw new InvalidOperationException("La prueba requiere un equipo funcionando con batería y con nivel de carga disponible.");

        public static Guid ReadActivePlan()
        {
            var value = OptimizerEngine.GetPowerPlanDetails().ActivePlanGuid;
            return Guid.TryParse(value, out var plan) && plan != Guid.Empty
                ? plan : throw new InvalidOperationException("No se pudo comprobar el plan de energía activo.");
        }
    }
}
