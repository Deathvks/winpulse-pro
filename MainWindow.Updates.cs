using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WinPulseOptimizer;

public partial class MainWindow
{
    private DateTimeOffset _lastUpdateCheckUtc;
    private DateTimeOffset _lastSessionUpdateCheckUtc;
    private DateTimeOffset _lastFailedUpdateCheckUtc;
    private bool _checkingForUpdate;
    private WinPulseRelease? _pendingUpdate;
    private WinPulseRelease? _availableUpdate;

    private bool CanCheckForUpdate => !_loading && !_startingUp && !_forceExit && IsActive && IsVisible &&
        WindowState != WindowState.Minimized && !_systemChangeInProgress && _benchmarkCancellation == null &&
        !_networkTesting && !_showingPrivacy && _processMeasurement == null && _detectedGame == null;

    private void InitializeUpdateButton(Version? version)
    {
        if (BtnUpdateStatus == null) return;
        string current = version?.ToString(3) ?? "—";
        BtnUpdateStatus.Content = $"v{current} · Buscar actualización";
    }

    private void RenderUpdateButton(Version current, bool upToDate = false)
    {
        if (BtnUpdateStatus == null) return;
        if (_availableUpdate is { } available)
        {
            BtnUpdateStatus.Style = (Style)FindResource("PrimaryBtn");
            BtnUpdateStatus.Content = $"Actualizar a v{available.Version.ToString(3)}";
            BtnUpdateStatus.ToolTip = "Hay una nueva versión disponible en GitHub. Haz clic para verla o descargarla.";
        }
        else
        {
            BtnUpdateStatus.Style = (Style)FindResource("LinkBtn");
            BtnUpdateStatus.Content = upToDate
                ? $"v{current.ToString(3)} · Al día"
                : $"v{current.ToString(3)} · Buscar actualización";
            BtnUpdateStatus.ToolTip = "Comprobar si hay una versión nueva disponible en GitHub.";
        }
    }

    private async void BtnUpdateStatus_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdateAsync(manual: true);

    private async Task CheckForUpdateAsync(bool manual = false)
    {
        if (_loading || _startingUp || _forceExit || _checkingForUpdate) return;
        if (!manual)
        {
            if (!CanCheckForUpdate) return;
            if (_pendingUpdate is { } pending)
            {
                OfferUpdate(pending);
                return;
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!manual)
        {
            if (_lastSessionUpdateCheckUtc != default && now >= _lastSessionUpdateCheckUtc &&
                now - _lastSessionUpdateCheckUtc < TimeSpan.FromHours(2))
                return;
            if (_lastFailedUpdateCheckUtc != default && now >= _lastFailedUpdateCheckUtc &&
                now - _lastFailedUpdateCheckUtc < TimeSpan.FromMinutes(5))
                return;
            if (_lastSessionUpdateCheckUtc == default && _lastUpdateCheckUtc <= now &&
                now - _lastUpdateCheckUtc < TimeSpan.FromMinutes(10))
                return;
        }

        Version current = typeof(MainWindow).Assembly.GetName().Version!;
        _checkingForUpdate = true;
        if (manual)
        {
            BtnUpdateStatus.IsEnabled = false;
            BtnUpdateStatus.Content = "Comprobando…";
        }

        try
        {
            var latest = await GitHubReleaseChecker.GetNewerReleaseAsync(current, CancellationToken.None);
            _lastSessionUpdateCheckUtc = now;
            _lastUpdateCheckUtc = now;
            _lastFailedUpdateCheckUtc = default;

            var store = HistoryManager.Load();
            store.LastUpdateCheckUtc = now;
            HistoryManager.Save(store);

            if (latest is { } available)
            {
                bool firstTime = _availableUpdate == null || _availableUpdate.Version != available.Version;
                _availableUpdate = available;
                RenderUpdateButton(current);
                if (firstTime)
                    Log($"Nueva versión disponible: WinPulse Pro {available.Version.ToString(3)}.");
                if (manual || CanCheckForUpdate)
                    OfferUpdate(available);
                else
                    _pendingUpdate = available;
            }
            else
            {
                _availableUpdate = null;
                _pendingUpdate = null;
                RenderUpdateButton(current, upToDate: manual);
                if (manual)
                {
                    Log($"Comprobación de actualizaciones: ya tienes la última versión (v{current.ToString(3)}).");
                    System.Windows.MessageBox.Show(this,
                        $"Ya tienes la última versión publicada de WinPulse Pro (v{current.ToString(3)}).",
                        "Sin actualizaciones pendientes", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            _lastFailedUpdateCheckUtc = now;
            RenderUpdateButton(current);
            if (manual)
            {
                if (_availableUpdate is { } fallback)
                {
                    OfferUpdate(fallback);
                    return;
                }
                Log($"No se pudo comprobar si hay actualizaciones. {ex.Message}");
                System.Windows.MessageBox.Show(this,
                    "No se pudo consultar GitHub para comprobar si hay actualizaciones. Revisa tu conexión a internet e inténtalo de nuevo.",
                    "Comprobación de actualizaciones", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _checkingForUpdate = false;
            BtnUpdateStatus.IsEnabled = true;
        }
    }

    private void OfferUpdate(WinPulseRelease release)
    {
        _pendingUpdate = null;
        var dialog = new UpdateAvailableWindow(release.Version) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(release.Page.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log($"No se pudo abrir la página de la actualización. {ex.Message}");
        }
    }
}
