using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WinPulseOptimizer;

public partial class SettingStatusWindow : Window
{
    private sealed record StatusFilter(string Label, SettingState? State);
    private SettingStatusSnapshot? _snapshot;
    private string _profile = "";
    private int _refreshVersion;
    private bool _busy;

    internal SettingStatusWindow()
    {
        InitializeComponent();
        CmbStatusFilter.ItemsSource = new[]
        {
            new StatusFilter("Todos los ajustes", null),
            new StatusFilter("Pendientes de reinicio", SettingState.PendingRestart),
            new StatusFilter("Bloqueados", SettingState.Blocked),
            new StatusFilter("Verificados", SettingState.Verified)
        };
        CmbStatusFilter.SelectedIndex = 0;
        Closed += (_, _) => _refreshVersion++;
    }

    internal async Task UpdateAsync(string profile, bool busy)
    {
        int version = ++_refreshVersion;
        _profile = profile;
        _busy = busy;
        TxtStatusProfile.Text = $"Perfil seleccionado · {profile}";
        BtnRefreshStatus.IsEnabled = false;
        TxtStatusSummary.Text = busy ? "Hay una operación en curso. Se volverá a comprobar al terminar."
            : "Comprobando la configuración de Windows…";
        if (busy) return;

        try
        {
            var snapshot = await Task.Run(OptimizerEngine.ReadSettingStatus);
            if (version != _refreshVersion) return;
            _snapshot = snapshot;
            string summary = $"{snapshot.CheckedAt:HH:mm:ss} · " +
                $"{snapshot.Rows.Count(row => row.State == SettingState.PendingRestart)} pendientes · " +
                $"{snapshot.Rows.Count(row => row.IsBlocked)} bloqueados · " +
                $"{snapshot.Rows.Count(row => row.State == SettingState.Verified)} verificados";
            if (!snapshot.HasTracking)
                summary += "\nEl seguimiento de solicitudes y reinicios empieza con los cambios que hagas en esta versión.";
            if (snapshot.RecoveryPending) summary += "\nHay un cambio pendiente de recuperar en Historial.";
            TxtStatusSummary.Text = summary;
            RenderRows();
        }
        catch (Exception ex)
        {
            if (version == _refreshVersion)
            {
                _snapshot = null;
                GridSettingsStatus.ItemsSource = null;
                TxtStatusSummary.Text = $"No se pudo comprobar la configuración. {ex.Message}";
            }
        }
        finally { if (version == _refreshVersion) BtnRefreshStatus.IsEnabled = true; }
    }

    private void RenderRows()
    {
        if (_snapshot is not { } snapshot) return;
        var filter = (CmbStatusFilter.SelectedItem as StatusFilter)?.State;
        GridSettingsStatus.ItemsSource = filter switch
        {
            SettingState.Blocked => snapshot.Rows.Where(row => row.IsBlocked).ToArray(),
            SettingState state => snapshot.Rows.Where(row => row.State == state).ToArray(),
            _ => snapshot.Rows
        };
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e) => await UpdateAsync(_profile, _busy);
    private void StatusFilter_Changed(object sender, SelectionChangedEventArgs e) => RenderRows();
    private void CloseStatus_Click(object sender, RoutedEventArgs e) => Close();
}
