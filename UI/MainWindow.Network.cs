using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WinPulseOptimizer
{
    public partial class MainWindow
    {
        private sealed record NetworkRun(DateTime Time, string Profile, PingMeasurement[] Results);
        private sealed record NetworkRow(string Title, string Endpoint, string Summary, string Comparison);
        private sealed record LoadedNetworkRow(string Title, string Transfer, string Results);

        private bool _networkTesting;
        private CancellationTokenSource? _loadedPingCancellation;
        private long _systemChangeSequence;
        private NetworkRun? _latestNetworkRun;
        private NetworkRun? _networkReference;
        private bool _readingEthernetAdapters;

        private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, MainTabs)) return;
            if (!_loading && MainTabs.SelectedItem is TabItem tab && tab.Content is FrameworkElement view)
                AnimateSlideFadeIn(view, 0, 12, 220);
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(UpdateLiveMetricsSchedule));
            if (!_loading && ReferenceEquals(MainTabs.SelectedItem, TabNetwork))
                await RefreshEthernetAdaptersAsync();
            else if (!_loading && ReferenceEquals(MainTabs.SelectedItem, TabProcesses))
                await RefreshTopProcessesAsync();
        }

        private async void BtnRefreshEthernet_Click(object sender, RoutedEventArgs e) =>
            await RefreshEthernetAdaptersAsync();

        private async Task RefreshEthernetAdaptersAsync()
        {
            if (_readingEthernetAdapters || _systemChangeInProgress) return;
            var selected = CmbEthernetAdapter.SelectedItem as EthernetAdapter;
            _readingEthernetAdapters = true;
            BtnRefreshEthernet.IsEnabled = false;
            CmbEthernetAdapter.IsEnabled = false;
            TxtEthernetStatus.Text = "Consultando adaptadores Ethernet…";
            RenderEthernetOptions();
            try
            {
                var adapters = await Task.Run(OptimizerEngine.GetEthernetAdapters);
                CmbEthernetAdapter.ItemsSource = adapters;
                CmbEthernetAdapter.SelectedItem = adapters.FirstOrDefault(adapter => adapter.Id == selected?.Id)
                    ?? adapters.FirstOrDefault();
                TxtEthernetStatus.Text = adapters.Length == 0 ? "Windows no encontró un adaptador Ethernet físico." : "";
            }
            catch (Exception ex)
            {
                CmbEthernetAdapter.ItemsSource = null;
                TxtEthernetStatus.Text = $"No se pudieron leer los ajustes de Ethernet. {ex.Message}";
            }
            finally
            {
                _readingEthernetAdapters = false;
                BtnRefreshEthernet.IsEnabled = true;
                CmbEthernetAdapter.IsEnabled = true;
                RenderEthernetOptions();
            }
        }

        private void CmbEthernetAdapter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading) RenderEthernetOptions();
        }

        private void RenderEthernetOptions()
        {
            var adapter = CmbEthernetAdapter.SelectedItem as EthernetAdapter;
            bool? moderation = adapter?.GetOption(EthernetOption.InterruptModeration);
            bool? eee = adapter?.GetOption(EthernetOption.EnergyEfficientEthernet);
            RenderEthernetOption(TxtEthernetModeration, BtnEthernetModeration, moderation);
            RenderEthernetOption(TxtEthernetEee, BtnEthernetEee, eee);
        }

        private void RenderEthernetOption(TextBlock status, System.Windows.Controls.Button button, bool? enabled)
        {
            status.Text = enabled switch { true => "Activado", false => "Desactivado", null => "No disponible" };
            button.Content = enabled == false ? "Activar" : "Desactivar";
            button.IsEnabled = !_readingEthernetAdapters && enabled.HasValue;
        }

        private async void BtnEthernetOption_Click(object sender, RoutedEventArgs e)
        {
            if (_networkTesting)
            {
                TxtEthernetStatus.Text = "Espera a que termine la medición de red.";
                return;
            }
            if (_readingEthernetAdapters || _systemChangeInProgress || _benchmarkCancellation != null ||
                CmbEthernetAdapter.SelectedItem is not EthernetAdapter adapter) return;

            bool isModeration = ReferenceEquals(sender, BtnEthernetModeration);
            var option = isModeration ? EthernetOption.InterruptModeration : EthernetOption.EnergyEfficientEthernet;
            if (adapter.GetOption(option) is not bool previous) return;
            string label = isModeration ? "Moderación de interrupciones" : "Ethernet de bajo consumo (EEE)";
            string action = previous ? "Desactivaste" : "Activaste";
            await ApplySystemChangeAsync($"{action} {label.ToLowerInvariant()}",
                $"Cambió el ajuste del controlador de {adapter.Name}. El adaptador puede desconectarse durante unos segundos.",
                "Pestaña 3: Red y Ratón", 3, "RowEthernet",
                new(SystemAction.Ethernet, Enabled: !previous, Adapter: adapter.Id, Ethernet: option));
            await RefreshEthernetAdaptersAsync();
        }

        private async void BtnPingTest_Click(object sender, RoutedEventArgs e)
        {
            if (_networkTesting || _systemChangeInProgress || _benchmarkCancellation != null) return;
            string host = TxtPingTarget.Text.Trim();
            if (!NetworkDiagnostics.IsValidTarget(host))
            {
                TxtPingStatus.Text = "Introduce un nombre de servidor o una dirección IP.";
                return;
            }

            SetNetworkTesting(true);
            TxtPingStatus.Text = "Midiendo latencia, variación y respuestas ICMP…";
            long changeSequence = _systemChangeSequence;
            string profile = TxtActiveModeTitle.Text;
            try
            {
                var results = await NetworkDiagnostics.MeasureAsync(host);
                if (_systemChangeSequence != changeSequence)
                {
                    TxtPingStatus.Text = "Cambió la configuración durante la prueba. Vuelve a medir.";
                    return;
                }

                _latestNetworkRun = new(DateTime.Now, profile, results);
                RenderNetworkRun();
                string summary = string.Join(Environment.NewLine,
                    results.Select(result => $"{result.Target} ({result.Host}) · {DescribePing(result)}"));
                RecordAction("Mediste la conexión", "Prueba ICMP a las puertas de enlace y destinos elegidos.",
                    "Pestaña 3: Red y Ratón", 3, "RowPingTest", summary);
                Log($"Prueba de red completada. Respondieron {results.Count(result => result.Received > 0)} de {results.Length} destinos.");
            }
            catch (Exception ex)
            {
                TxtPingStatus.Text = $"No se pudo completar la prueba. {ex.Message}";
                Log(TxtPingStatus.Text);
            }
            finally
            {
                SetNetworkTesting(false);
            }
        }

        private void SetNetworkTesting(bool testing)
        {
            _networkTesting = testing;
            BtnPingTest.IsEnabled = !testing;
            BtnLoadedPing.IsEnabled = !testing;
            BtnSavePingReference.IsEnabled = !testing && _latestNetworkRun != null;
            TxtPingTarget.IsEnabled = !testing;
            BtnCancelLoadedPing.IsEnabled = _loadedPingCancellation != null;
        }

        private async void BtnLoadedPing_Click(object sender, RoutedEventArgs e)
        {
            if (_networkTesting || _systemChangeInProgress || _benchmarkCancellation != null) return;
            string host = TxtPingTarget.Text.Trim();
            if (!NetworkDiagnostics.IsValidTarget(host))
            {
                TxtLoadedPingStatus.Text = "Introduce un nombre de servidor o una dirección IP.";
                TxtPingTarget.Focus();
                return;
            }

            using var cancellation = new CancellationTokenSource();
            _loadedPingCancellation = cancellation;
            long changeSequence = _systemChangeSequence;
            string profile = TxtActiveModeTitle.Text;
            LstLoadedPing.ItemsSource = null;
            SetNetworkTesting(true);
            TxtLoadedPingStatus.Text = "Identificando la ruta de red…";
            var progress = new Progress<PingLoadPhase>(phase =>
            {
                if (_loadedPingCancellation == cancellation && !cancellation.IsCancellationRequested)
                    TxtLoadedPingStatus.Text = $"Fase {(int)phase + 1} de 3 · {DescribeLoadPhase(phase)}…";
            });

            try
            {
                var run = await Task.Run(() => NetworkDiagnostics.MeasureLoadedAsync(host, progress, cancellation.Token));
                cancellation.Token.ThrowIfCancellationRequested();
                var rows = run.Phases.Select(phase => DescribeLoadedPhase(phase, run.Phases[0])).ToArray();
                LstLoadedPing.ItemsSource = rows;
                TxtLoadedPingStatus.Text = $"Medición · {DateTime.Now:HH:mm:ss} · {run.Adapter} · {profile}";
                if (!run.Phases[0].Results.Any(result => result.Target == "Router"))
                    TxtLoadedPingStatus.Text += "\nWindows no encontró una puerta de enlace en esta ruta.";
                bool hasLoadedSamples = run.Phases.Skip(1).Any(phase => phase.Results.Any(result => result.Sent > 0));
                if (!hasLoadedSamples)
                    TxtLoadedPingStatus.Text += "\nNo se obtuvieron muestras bajo carga.";
                string summary = string.Join(Environment.NewLine,
                    rows.Select(row => $"{row.Title} · {row.Transfer}\n{row.Results}"));
                RecordAction("Mediste el ping bajo carga", TxtLoadedPingStatus.Text,
                    "Pestaña 3: Red y Ratón", 3, "RowLoadedPing", summary);
                Log(hasLoadedSamples ? "Prueba de ping bajo carga completada." : "La prueba solo obtuvo datos en conexión libre.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                TxtLoadedPingStatus.Text = changeSequence == _systemChangeSequence ? "Prueba cancelada."
                    : "Cambió la configuración de WinPulse. Vuelve a medir.";
            }
            catch (Exception ex)
            {
                TxtLoadedPingStatus.Text = $"No se pudo completar la prueba. {ex.Message}";
                Log(TxtLoadedPingStatus.Text);
            }
            finally
            {
                _loadedPingCancellation = null;
                SetNetworkTesting(false);
            }
        }

        private void BtnCancelLoadedPing_Click(object sender, RoutedEventArgs e) =>
            _loadedPingCancellation?.Cancel();

        private static string DescribeLoadPhase(PingLoadPhase phase) => phase switch
        {
            PingLoadPhase.Download => "Durante la descarga",
            PingLoadPhase.Upload => "Durante la subida",
            _ => "Conexión libre"
        };

        private static LoadedNetworkRow DescribeLoadedPhase(LoadedPingPhase phase, LoadedPingPhase baseline)
        {
            bool loaded = phase.Phase != PingLoadPhase.Idle;
            string transfer = loaded ? $"{phase.Bytes / 1_000_000.0:0.##} MB de tráfico de prueba" : "";
            if (phase.Notice != null) transfer += $"{(loaded ? " · " : "")}{phase.Notice}";
            string results = string.Join(Environment.NewLine, phase.Results.Select(result =>
            {
                string label = result.Host == result.Address?.ToString()
                    ? $"{result.Target} ({result.Address})" : $"{result.Target} · {result.Host} ({result.Address})";
                string summary = result.Sent > 0 ? DescribePing(result) : "Sin muestras.";
                var idle = baseline.Results.FirstOrDefault(result.IsSameDestination);
                if (loaded && result.Received >= 20 && idle?.Received >= 20 &&
                    idle.P95Ms is long idleP95 && result.P95Ms is long loadedP95)
                    summary += $" · Δ p95 frente a libre {loadedP95 - idleP95:+0;-0;0} ms";
                if (result.Received is > 0 and < 20)
                    summary += " · Pocas respuestas para estimar el p95.";
                return $"{label}\n{summary}";
            }));
            return new(DescribeLoadPhase(phase.Phase), transfer, results);
        }

        private void BtnSavePingReference_Click(object sender, RoutedEventArgs e)
        {
            if (_latestNetworkRun == null) return;
            _networkReference = _latestNetworkRun;
            RenderNetworkRun();
        }

        private void RenderNetworkRun()
        {
            if (_latestNetworkRun is not { } latest) return;
            var reference = _networkReference;
            TxtPingStatus.Text = $"Última medición · {latest.Time:HH:mm:ss} · {latest.Profile}";
            if (reference != null)
                TxtPingStatus.Text += $"\nReferencia · {reference.Time:HH:mm:ss} · {reference.Profile}";
            bool showDelta = reference != null && !ReferenceEquals(reference, latest);
            LstPingResults.ItemsSource = latest.Results.Select(result =>
            {
                var previous = reference?.Results.FirstOrDefault(result.IsSameDestination);
                string endpoint = result.Address == null || result.Address.ToString() == result.Host
                    ? result.Host
                    : $"{result.Host} ({result.Address})";
                return new NetworkRow(result.Target, endpoint, DescribePing(result),
                    showDelta ? DescribePingDelta(result, previous) : "");
            }).ToArray();
        }

        private static string DescribePing(PingMeasurement result)
        {
            if (result.MissingPercent is not double missing)
                return result.Error ?? "Sin medición.";
            string missingText = $"Sin respuesta {missing:0}% ({result.Received}/{result.Sent} respuestas)";
            if (result.Error != null) return $"{result.Error} {missingText}";
            string variation = result.VariationMs is double rtt ? $"{rtt:0.0} ms" : "—";
            return $"Media {result.AverageMs:0.0} ms · p95 {result.P95Ms:0} ms · Variación {variation} · {missingText}";
        }

        private static string DescribePingDelta(PingMeasurement result, PingMeasurement? reference)
        {
            if (reference == null) return "Sin referencia para esta dirección.";
            string Difference(double? value, double? previous, string unit) =>
                value.HasValue && previous.HasValue ? $"{value - previous:+0.0;-0.0;0.0} {unit}" : "—";
            return $"Respecto a la referencia · Media {Difference(result.AverageMs, reference.AverageMs, "ms")} · " +
                $"Variación {Difference(result.VariationMs, reference.VariationMs, "ms")} · " +
                $"Sin respuesta {Difference(result.MissingPercent, reference.MissingPercent, "pp")}";
        }
    }
}
