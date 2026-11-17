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
    private bool _checkingForUpdate;
    private WinPulseRelease? _pendingUpdate;

    private bool CanCheckForUpdate => !_loading && !_startingUp && !_forceExit && IsActive && IsVisible &&
        WindowState != WindowState.Minimized && !_systemChangeInProgress && _benchmarkCancellation == null &&
        !_networkTesting && _processMeasurement == null && _detectedGame == null;

    private async Task CheckForUpdateAsync()
    {
        if (!CanCheckForUpdate || _checkingForUpdate) return;
        if (_pendingUpdate is { } pending)
        {
            OfferUpdate(pending);
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_lastUpdateCheckUtc <= now && now - _lastUpdateCheckUtc < TimeSpan.FromDays(1)) return;
        _lastUpdateCheckUtc = now;
        _checkingForUpdate = true;
        try
        {
            var store = HistoryManager.Load();
            store.LastUpdateCheckUtc = now;
            HistoryManager.Save(store);
            var current = typeof(MainWindow).Assembly.GetName().Version!;
            _pendingUpdate = await GitHubReleaseChecker.GetNewerReleaseAsync(current, CancellationToken.None);
            if (_pendingUpdate is { } available && CanCheckForUpdate) OfferUpdate(available);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            // La comprobación es opcional. Un fallo de red no interrumpe la apertura de WinPulse.
        }
        finally { _checkingForUpdate = false; }
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
