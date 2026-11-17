using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WinPulseOptimizer;

public partial class MainWindow
{
    private CancellationTokenSource? _processMeasurement;

    private async void BtnMeasureProcesses_Click(object sender, RoutedEventArgs e)
    {
        if (_processMeasurement != null) return;
        if (_systemChangeInProgress || _benchmarkCancellation != null || _networkTesting)
        {
            TxtProcessMeasurement.Text = "Espera a que termine la operación actual para medir.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _processMeasurement = cancellation;
        BtnMeasureProcesses.IsEnabled = false;
        GridProcessUsage.Visibility = Visibility.Collapsed;
        TxtProcessMeasurement.Text = $"Midiendo durante {ProcessResourceMonitor.DurationSeconds} segundos…";
        try
        {
            var rows = await Task.Run(() => ProcessResourceMonitor.MeasureAsync(cancellation.Token), cancellation.Token);
            if (_forceExit || cancellation.IsCancellationRequested) return;
            GridProcessUsage.ItemsSource = rows;
            GridProcessUsage.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            TxtProcessMeasurement.Text = rows.Length == 0 ? "Windows no devolvió procesos." :
                $"{rows.Length} procesos · {DateTime.Now:T} · Pulsa una columna para ordenar. «—» indica un dato no disponible.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_forceExit) TxtProcessMeasurement.Text = $"No se pudo medir el uso de recursos. {ex.Message}";
        }
        finally
        {
            _processMeasurement = null;
            if (!_forceExit) BtnMeasureProcesses.IsEnabled = true;
        }
    }

    private void BtnOpenResourceMonitor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var monitor = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "resmon.exe"))
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) { TxtProcessMeasurement.Text = $"No se pudo abrir el Monitor de recursos. {ex.Message}"; }
    }
}
