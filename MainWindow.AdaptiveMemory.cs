using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace WinPulseOptimizer;

public partial class MainWindow
{
    private bool _adaptiveMemoryEnabled;
    private int _adaptiveMemorySettingsVersion;
    private bool _checkingAdaptiveMemory;
    private bool _adjustedDuringPressure;
    private long? _previousLowMemoryAt;
    private long _nextMemoryProbeAt;
    private long _nextMemoryAdjustmentAt;

    private void LoadAdaptiveMemorySettings(AuditStore store)
    {
        _adaptiveMemoryEnabled = store.AdaptiveMemoryAutomatic == true;
        SwAdaptiveMemory.IsChecked = _adaptiveMemoryEnabled;
        TxtAdaptiveMemoryStatus.Text = store.AdaptiveMemoryAutomatic switch
        {
            true => "Activo · en espera",
            null when store.AdaptiveMemoryEnabled => "Activa el ajuste para que WinPulse elija las apps automáticamente.",
            _ => "Desactivado"
        };
        _previousLowMemoryAt = null;
        _adaptiveMemorySettingsVersion++;
    }

    private void SwAdaptiveMemory_Click(object sender, RoutedEventArgs e)
    {
        var store = HistoryManager.Load();
        store.AdaptiveMemoryAutomatic = SwAdaptiveMemory.IsChecked == true;
        if (HistoryManager.Save(store))
        {
            LoadAdaptiveMemorySettings(store);
            return;
        }

        SwAdaptiveMemory.IsChecked = _adaptiveMemoryEnabled;
        TxtAdaptiveMemoryStatus.Text = "No se pudo guardar la optimización adaptativa.";
    }

    private bool IsAdaptiveGame(string name) =>
        string.Equals(name, _detectedGame, StringComparison.OrdinalIgnoreCase) ||
        _includedGames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        _gameProfiles.Any(profile => profile.Executables.Contains(name, StringComparer.OrdinalIgnoreCase));

    private bool CanCheckAdaptiveMemory => _automationTimer.IsEnabled && !_forceExit &&
        !_systemChangeInProgress && !_checkingAutomation && _benchmarkCancellation == null && !_networkTesting &&
        _processMeasurement == null && SwPauseAutomationForTests.IsChecked != true;

    private async Task CheckAdaptiveMemoryAsync()
    {
        if (!_adaptiveMemoryEnabled || _checkingAdaptiveMemory || !CanCheckAdaptiveMemory) return;

        long now = Environment.TickCount64;
        if (now < _nextMemoryProbeAt) return;
        _nextMemoryProbeAt = now + 12_000;
        if (!HardwareMonitor.IsPhysicalMemoryLow())
        {
            if (_previousLowMemoryAt.HasValue && !_adjustedDuringPressure)
                TxtAdaptiveMemoryStatus.Text = "Activo · la memoria disponible se ha recuperado.";
            _previousLowMemoryAt = null;
            _adjustedDuringPressure = false;
            return;
        }

        bool sustained = _previousLowMemoryAt is long last && now - last <= 30_000;
        _previousLowMemoryAt = now;
        if (!sustained || _adjustedDuringPressure || now < _nextMemoryAdjustmentAt) return;

        _checkingAdaptiveMemory = true;
        _nextMemoryAdjustmentAt = now + 60_000;
        int settingsVersion = _adaptiveMemorySettingsVersion;
        try
        {
            var excluded = _includedGames.Concat(_gameProfiles.SelectMany(profile => profile.Executables))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_detectedGame is { } game) excluded.Add(game);
            var candidate = await Task.Run(() => ProcessResourceMonitor.FindQuietBackgroundAppAsync(excluded));
            if (!_adaptiveMemoryEnabled || !CanCheckAdaptiveMemory || settingsVersion != _adaptiveMemorySettingsVersion ||
                !HardwareMonitor.IsPhysicalMemoryLow()) return;
            if (candidate == null || IsAdaptiveGame(candidate.Name))
            {
                TxtAdaptiveMemoryStatus.Text = "RAM baja · sin apps minimizadas que cumplan los criterios.";
                return;
            }
            if (!ProcessResourceMonitor.TryAdjustBackgroundMemory(candidate)) return;

            _adjustedDuringPressure = true;
            _nextMemoryAdjustmentAt = Environment.TickCount64 + 300_000;
            TxtAdaptiveMemoryStatus.Text = $"Último ajuste · {candidate.Name} · {DateTime.Now:t}";
            Log($"Ajuste automático de memoria en {candidate.Name}.exe por falta sostenida de RAM disponible.");
        }
        catch (Exception ex) { if (!_forceExit) Log($"No se pudo comprobar el ajuste automático de memoria. {ex.Message}"); }
        finally { _checkingAdaptiveMemory = false; }
    }
}
