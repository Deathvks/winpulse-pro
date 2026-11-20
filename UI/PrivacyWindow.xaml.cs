using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace WinPulseOptimizer;

public partial class PrivacyWindow : Window
{
    private readonly UsageStatistics _statistics;
    private readonly bool _initialNotice;
    private readonly UsageReportingSettings? _reporting;

    internal PrivacyWindow(UsageStatistics statistics, bool initialNotice, UsageReportingSettings? reporting)
    {
        _statistics = statistics;
        _initialNotice = initialNotice;
        _reporting = reporting;
        InitializeComponent();
        if (reporting != null)
        {
            UsageDestination.Text = $"Si aceptas, WinPulse enviará a {reporting.Controller} la versión de la app y recuentos de uso, como máximo una vez al día al abrir la ventana, fuera de las partidas. El responsable recibe los totales agrupados por Telegram. Los nombres de juegos, archivos y el historial detallado quedan fuera del resumen.";
            UsageWithdrawal.Text = $"Puedes usar WinPulse con cualquier elección y retirar tu consentimiento desde Privacidad. Al retirarlo se borran los datos pendientes de envío. Para ejercer tus derechos o consultar los datos ya enviados, escribe a {reporting.Contact}.";
            BtnUsagePolicy.Visibility = Visibility.Visible;
            BtnAcceptUsage.Content = "Compartir estadísticas";
        }
        Refresh();
    }

    private void AcceptUsage_Click(object sender, RoutedEventArgs e) => SaveChoice(true);

    private void DeclineUsage_Click(object sender, RoutedEventArgs e) => SaveChoice(false);

    private void SaveChoice(bool enabled)
    {
        bool saved = _statistics.SetConsent(enabled);
        Refresh();
        if (!saved)
        {
            PrivacyStatus.Text = enabled ? "No se guardó la elección. Las estadísticas siguen desactivadas." :
                "La recopilación se detuvo en esta sesión. Windows impidió guardar la elección o borrar el archivo local. Vuelve a intentarlo.";
            return;
        }
        if (_initialNotice) Close();
    }

    private void Refresh()
    {
        BtnAcceptUsage.IsEnabled = !_statistics.IsEnabled;
        BtnDeclineUsage.Content = _statistics.IsEnabled ? "Desactivar y borrar" : _reporting == null ? "Rechazar" : "No compartir";
        PrivacyStatus.Text = !_statistics.IsEnabled ? "Recopilación desactivada" :
            _reporting == null ? "Recopilación local activada" : "Envío de estadísticas activado";
        string? json = _statistics.GetExportJson();
        UsagePreview.Text = json ?? "";
        UsagePreview.Visibility = BtnExportUsage.Visibility = json == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (_reporting == null) return;
        try { using var browser = Process.Start(new ProcessStartInfo(_reporting.Policy.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { PrivacyStatus.Text = "No se pudo abrir la política de privacidad."; }
    }

    private void ExportUsage_Click(object sender, RoutedEventArgs e)
    {
        if (_statistics.GetExportJson() is not { } json) return;
        var dialog = new SaveFileDialog
        {
            Title = "Exportar resumen de uso", FileName = "winpulse-resumen-de-uso.json", Filter = "Resumen JSON (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, json);
            PrivacyStatus.Text = "Resumen exportado. Puedes revisar el archivo antes de compartirlo.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PrivacyStatus.Text = "Windows impidió guardar el resumen en esa ubicación.";
        }
    }
}
