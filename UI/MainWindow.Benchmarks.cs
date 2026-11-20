using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WinPulseOptimizer
{
    public partial class MainWindow
    {
        private CancellationTokenSource? _benchmarkCancellation;
        private bool? _benchmarkPowerAtStart;
        private bool _benchmarkPowerChanged;
        private string? _presentMonPath;
        private BenchmarkResults _benchmarkResults = new();
        private bool _loadingGameComparisons;
        private GameComparison? SelectedGameComparison => CmbSavedGameComparisons.SelectedItem as GameComparison;

        public sealed record BenchmarkRow(string Metric, string A, string B, string Delta);

        private void LoadBenchmarks(AuditStore store)
        {
            _benchmarkResults = store.Benchmarks ?? new();
            _benchmarkResults.MigrateGameComparison();
            CmbGameDuration.ItemsSource = new[] { 30, 60, 90, 120 };
            LoadGameComparisonList(_benchmarkResults.GameComparisons.MaxBy(comparison => comparison.A.CapturedAt)?.Id);
            CmbBatteryDuration.ItemsSource = new[] { 5, 10, 15, 20, 30 };
            CmbBatteryDuration.SelectedItem = _benchmarkResults.BatteryA?.Seconds / 60 ?? 10;
            TxtBatteryScenario.Text = _benchmarkResults.BatteryA?.Scenario ?? "";
            SwPauseAutomationForTests.IsChecked = store.PauseAutomationForBenchmarks;
            RenderBenchmarkResults();
        }

        private void BenchmarkPause_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var store = HistoryManager.Load();
            store.PauseAutomationForBenchmarks = SwPauseAutomationForTests.IsChecked == true;
            if (!HistoryManager.Save(store))
                Log("La pausa de automatización se aplicó en esta sesión. No se pudo guardar para el próximo inicio.");
            if (store.PauseAutomationForBenchmarks)
                TxtAutoStatus.Text = "Pausado para pruebas A/B";
            else
                ResetAutomationOverride();
        }

        private void BtnChoosePresentMon_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Selecciona la consola PresentMon descargada de Intel",
                Filter = "Consola PresentMon (PresentMon*.exe)|PresentMon*.exe",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;
            _presentMonPath = dialog.FileName;
            TxtPresentMonFile.Text = Path.GetFileName(_presentMonPath);
        }

        private void BtnPresentMonDownload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var browser = Process.Start(new ProcessStartInfo("https://github.com/GameTechDev/PresentMon/releases")
                    { UseShellExecute = true });
            }
            catch (Exception ex) { TxtBenchmarkStatus.Text = $"No se pudo abrir la descarga. {ex.Message}"; }
        }

        private async void BtnRefreshGameTargets_Click(object sender, RoutedEventArgs e)
        {
            var button = (System.Windows.Controls.Button)sender;
            button.IsEnabled = false;
            try
            {
                var previous = CmbGameTarget.SelectedItem as CaptureTarget;
                var targets = await Task.Run(PerformanceBenchmarks.ListCaptureTargets);
                CmbGameTarget.ItemsSource = targets;
                CmbGameTarget.SelectedItem = targets.FirstOrDefault(t => t.Pid == previous?.Pid)
                    ?? targets.FirstOrDefault(t => t.Executable.Equals(SelectedGameComparison?.A.Executable, StringComparison.OrdinalIgnoreCase));
                if (targets.Length == 0) TxtBenchmarkStatus.Text = "Abre el juego antes de buscar su ventana.";
            }
            catch (Exception ex) { TxtBenchmarkStatus.Text = $"No se pudieron buscar ventanas. {ex.Message}"; }
            finally { button.IsEnabled = true; }
        }

        private void LoadGameComparisonList(Guid? selectedId)
        {
            var comparisons = _benchmarkResults.GameComparisons.OrderByDescending(comparison => comparison.A.CapturedAt).ToArray();
            _loadingGameComparisons = true;
            CmbSavedGameComparisons.ItemsSource = comparisons;
            CmbSavedGameComparisons.SelectedItem = comparisons.FirstOrDefault(comparison => comparison.Id == selectedId);
            _loadingGameComparisons = false;
            ApplyGameComparisonSelection();
        }

        private void SavedGameComparison_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading && !_loadingGameComparisons) ApplyGameComparisonSelection();
        }

        private void BtnNewGameComparison_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedGameComparison == null) return;
            CmbSavedGameComparisons.SelectedItem = null;
            TxtBenchmarkStatus.Text = "Nueva comparación lista para medir A.";
        }

        private void ApplyGameComparisonSelection()
        {
            var first = SelectedGameComparison?.A;
            TxtGameScenario.Text = first?.Scenario ?? "";
            TxtGameScenario.IsReadOnly = first != null;
            CmbGameDuration.SelectedItem = first?.Seconds ?? 60;
            CmbGameDuration.IsEnabled = first == null;
            if (first != null && CmbGameTarget.ItemsSource is CaptureTarget[] targets &&
                (CmbGameTarget.SelectedItem is not CaptureTarget selected || !Same(selected.Executable, first.Executable)))
                CmbGameTarget.SelectedItem = targets.FirstOrDefault(target => Same(target.Executable, first.Executable));
            RenderGameBenchmark();
        }

        private async void BtnGameCapture_Click(object sender, RoutedEventArgs e)
        {
            bool isB = ((System.Windows.Controls.Button)sender).Tag as string == "B";
            var comparison = SelectedGameComparison;
            if (isB != (comparison != null)) return;
            string scenario = TxtGameScenario.Text.Trim();
            string? tool = _presentMonPath;
            if (tool == null || !File.Exists(tool))
            {
                TxtBenchmarkStatus.Text = "Selecciona el ejecutable de consola de PresentMon 2.x.";
                return;
            }
            if (CmbGameTarget.SelectedItem is not CaptureTarget target || scenario.Length == 0 ||
                CmbGameDuration.SelectedItem is not int seconds)
            {
                TxtBenchmarkStatus.Text = "Selecciona la ventana del juego e indica la escena y los ajustes.";
                return;
            }
            if (comparison != null && !PerformanceBenchmarks.MatchesGameRun(comparison.A, target.Executable, scenario,
                seconds, HardwareMonitor.GetAcState()))
            {
                TxtBenchmarkStatus.Text = "Para medir B, usa el mismo juego, escena, duración y fuente de energía que en A.";
                return;
            }

            await RunBenchmarkAsync($"Juego {(isB ? "B" : "A")}: vuelve al juego. Captura de {seconds} s tras 5 s de espera.",
                async (profile, plan, token) =>
                {
                    var result = await Task.Run(() => PerformanceBenchmarks.CaptureGameAsync(tool, target, scenario, profile, plan, seconds, token), token);
                    token.ThrowIfCancellationRequested();
                    GameComparison saved;
                    if (comparison != null)
                    {
                        int index = _benchmarkResults.GameComparisons.FindIndex(item => item.Id == comparison.Id);
                        saved = comparison with { B = result };
                        _benchmarkResults.GameComparisons[index] = saved;
                    }
                    else
                    {
                        saved = new(Guid.NewGuid(), result);
                        _benchmarkResults.GameComparisons.Add(saved);
                    }
                    LoadGameComparisonList(saved.Id);
                });
        }

        private async void BtnBatteryCapture_Click(object sender, RoutedEventArgs e)
        {
            bool isB = ((System.Windows.Controls.Button)sender).Tag as string == "B";
            string scenario = TxtBatteryScenario.Text.Trim();
            if (scenario.Length == 0 || CmbBatteryDuration.SelectedItem is not int minutes)
            {
                TxtBenchmarkStatus.Text = "Indica la tarea que repetirás y elige cuánto durará.";
                return;
            }
            if (HardwareMonitor.ReadDischargingBatteryPercentage() == null)
            {
                TxtBenchmarkStatus.Text = "Desenchufa el cargador para medir el consumo en batería.";
                return;
            }
            if (isB && (_benchmarkResults.BatteryA is not { } a || a.Seconds != minutes * 60 || !Same(a.Scenario, scenario)))
            {
                TxtBenchmarkStatus.Text = "Para medir B, usa la misma tarea y duración que en A.";
                return;
            }

            string pass = isB ? "B" : "A";
            await RunBenchmarkAsync($"Batería {pass}: empieza la tarea de la sesión.", async (profile, plan, token) =>
            {
                var progress = new Progress<int>(seconds =>
                {
                    if (!token.IsCancellationRequested && _benchmarkCancellation?.Token == token)
                        TxtBenchmarkStatus.Text = $"Batería {pass} · quedan {seconds / 60}:{seconds % 60:00}";
                });
                var result = await Task.Run(() => PerformanceBenchmarks.MeasureBatteryAsync(scenario, profile, plan, minutes * 60, progress, token), token);
                if (isB) _benchmarkResults.BatteryB = result;
                else
                {
                    _benchmarkResults.BatteryA = result;
                    _benchmarkResults.BatteryB = null;
                }
            });
        }

        private async Task RunBenchmarkAsync(string startingMessage, Func<string, Guid, CancellationToken, Task> measure)
        {
            if (_systemChangeInProgress || _benchmarkCancellation != null || _networkTesting) return;
            if (_processMeasurement != null)
            {
                TxtBenchmarkStatus.Text = "Espera a que termine la medición de procesos.";
                return;
            }
            SwPauseAutomationForTests.IsChecked = true;
            using var cancellation = new CancellationTokenSource();
            _benchmarkCancellation = cancellation;
            _benchmarkPowerAtStart = HardwareMonitor.GetAcState();
            _benchmarkPowerChanged = false;
            SetBenchmarkControls(true);
            TxtBenchmarkStatus.Text = startingMessage;
            try
            {
                string profile = _activeProfileTag == "" ? TxtActiveModeDesc.Text : TxtActiveModeTitle.Text;
                Guid plan = await Task.Run(PerformanceBenchmarks.ReadActivePlan, cancellation.Token);
                await measure(profile, plan, cancellation.Token);
                var store = HistoryManager.Load();
                store.Benchmarks = _benchmarkResults;
                TxtBenchmarkStatus.Text = HistoryManager.Save(store)
                    ? "Medición guardada. La automatización sigue pausada para que puedas repetir la sesión."
                    : "Medición disponible en esta ventana. No se pudo guardar en disco.";
                RenderBenchmarkResults();
            }
            catch (OperationCanceledException)
            {
                TxtBenchmarkStatus.Text = _benchmarkPowerChanged
                    ? "Cambió la fuente de energía durante la sesión. Repite la medición."
                    : "Medición cancelada.";
            }
            catch (Exception ex) { TxtBenchmarkStatus.Text = $"No se completó la medición. {ex.Message}"; }
            finally
            {
                _benchmarkCancellation = null;
                SetBenchmarkControls(false);
            }
        }

        private void CheckBenchmarkPower()
        {
            if (_benchmarkCancellation == null || _benchmarkPowerAtStart == HardwareMonitor.GetAcState()) return;
            _benchmarkPowerChanged = true;
            _benchmarkCancellation.Cancel();
        }

        private void SetBenchmarkControls(bool measuring)
        {
            UpdateLiveMetricsSchedule();
            BenchmarkInputs.IsEnabled = !measuring;
            BtnCancelBenchmark.IsEnabled = measuring;
            SwPauseAutomationForTests.IsEnabled = !measuring;
            CardProfiles.IsEnabled = !measuring;
            foreach (var tab in MainTabs.Items.OfType<TabItem>().Where(tab => tab != TabBenchmarks))
                tab.IsEnabled = !measuring;
        }

        private void BtnCancelBenchmark_Click(object sender, RoutedEventArgs e) => _benchmarkCancellation?.Cancel();

        private static bool Same(string first, string second) => first.Equals(second, StringComparison.OrdinalIgnoreCase);

        private static BenchmarkRow Metric(string name, double? a, double? b, int precision = 1, bool compare = true)
        {
            string Format(double? value) => value?.ToString($"F{precision}", CultureInfo.CurrentCulture) ?? "—";
            double? difference = compare && a.HasValue && b.HasValue ? b.Value - a.Value : null;
            string delta = difference > 0 ? "+" + Format(difference) : Format(difference);
            return new(name, Format(a), Format(b), delta);
        }

        private void RenderGameBenchmark()
        {
            var comparison = SelectedGameComparison;
            var ga = comparison?.A;
            var gb = comparison?.B;
            BtnNewGameComparison.IsEnabled = ga != null;
            BtnGamePassA.IsEnabled = ga == null;
            BtnGamePassB.IsEnabled = ga != null;
            BtnGamePassB.Content = gb == null ? "Medir B" : "Volver a medir B";
            string power = ga?.OnAc switch { true => "conectado a la corriente", false => "en batería", _ => "con la misma fuente de energía" };
            TxtGameBenchmarkGuide.Text = ga == null
                ? "Paso 1 de 2 · Selecciona el juego, anota la escena y mide A con el perfil de referencia."
                : gb == null
                    ? $"Paso 2 de 2 · Repite la escena en {ga.Executable}.exe, con el equipo {power}. Para comparar perfiles, cambia al segundo antes de medir B."
                    : "Comparación completa. Puedes revisarla desde Comparaciones guardadas o iniciar una nueva.";
            string DescribeGame(GameMeasurement? run) => run is null ? "—" :
                $"{run.Profile} · {run.Executable}.exe · {run.Seconds} s · {run.CapturedAt:g}";
            TxtGameBenchmarkSummary.Text = $"A · {DescribeGame(ga)}{Environment.NewLine}B · {DescribeGame(gb)}";
            var tempA = ga?.Temperature;
            var tempB = gb?.Temperature;
            bool sameSensor = tempA != null && tempB != null && Same(tempA.Device, tempB.Device);
            string DescribeSensor(GameMeasurement? run) => run?.Temperature is { } temperature
                ? $"{temperature.Device} ({temperature.Samples} muestras)" : "sin lecturas";
            TxtGameTemperatureSource.Text = ga == null || (tempA == null && tempB == null) ? "" : $"Sensor de GPU · A: {DescribeSensor(ga)}"
                + (gb == null ? "" : $" · B: {DescribeSensor(gb)}");
            if (tempA != null && tempB != null && !sameSensor)
                TxtGameTemperatureSource.Text += " · Los sensores son distintos.";
            GridGameBenchmark.ItemsSource = new[]
            {
                Metric("FPS medios", ga?.AverageFps, gb?.AverageFps),
                Metric("1 % low (FPS)", ga?.LowFps, gb?.LowFps),
                Metric("Tiempo mediano (ms)", ga?.MedianMs, gb?.MedianMs, 2),
                Metric("Tiempo p95 (ms)", ga?.P95Ms, gb?.P95Ms, 2),
                Metric("Tiempo p99 (ms)", ga?.P99Ms, gb?.P99Ms, 2),
                Metric("GPU · temperatura media (°C)", tempA?.AverageC, tempB?.AverageC, compare: sameSensor),
                Metric("GPU · temperatura máxima (°C)", tempA?.MaximumC, tempB?.MaximumC, compare: sameSensor),
                Metric("Fotogramas medidos", ga?.Frames, gb?.Frames, 0),
            };
        }

        private void RenderBenchmarkResults()
        {
            RenderGameBenchmark();
            var ba = _benchmarkResults.BatteryA;
            var bb = _benchmarkResults.BatteryB;
            string DescribeBattery(BatteryMeasurement? run) => run is null ? "—" :
                $"{run.Profile} · {run.Seconds / 60} min · {run.StartPercent}% → {run.EndPercent}% · {run.CapturedAt:g}";
            string detail = (ba != null && ba.AverageWatts == null) || (bb != null && bb.AverageWatts == null)
                ? $"{Environment.NewLine}Windows no informó potencia suficiente para una de las sesiones. El nivel de carga se muestra en puntos porcentuales enteros."
                : "";
            TxtBatteryBenchmarkSummary.Text = $"A · {DescribeBattery(ba)}{Environment.NewLine}B · {DescribeBattery(bb)}{detail}";
            GridBatteryBenchmark.ItemsSource = new[]
            {
                Metric("Potencia media (W)", ba?.AverageWatts, bb?.AverageWatts, 2),
                Metric("Consumo estimado (Wh)", ba?.WattHours, bb?.WattHours, 2),
                Metric("Descarga (puntos porcentuales)", ba?.StartPercent - ba?.EndPercent, bb?.StartPercent - bb?.EndPercent, 0),
            };
        }
    }
}
