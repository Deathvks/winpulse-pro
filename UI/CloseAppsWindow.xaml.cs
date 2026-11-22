using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace WinPulseOptimizer;

internal sealed class CloseableAppOption
{
    public CloseableAppOption(CloseableAppGroup group)
    {
        Group = group;
        IsSelected = group.IsMinimized;
    }

    public CloseableAppGroup Group { get; }
    public bool IsSelected { get; set; }
    public string DisplayName => Group.ProcessName;
    public string RamLabel => $"{Group.TotalRamMb:0.0} MB";
    public string StateLabel => Group.IsMinimized ? "· Minimizada" : "· Abierta";
    public string ProcessDetail => Group.Targets.Length == 1
        ? "1 proceso en sesión de usuario"
        : $"{Group.Targets.Length} procesos agrupados en sesión de usuario";
}

public partial class CloseAppsWindow : Window
{
    private readonly List<CloseableAppOption> _options;

    internal IReadOnlyList<CloseableAppGroup> SelectedGroups { get; private set; } = Array.Empty<CloseableAppGroup>();

    internal CloseAppsWindow(IReadOnlyList<CloseableAppGroup> groups)
    {
        InitializeComponent();
        _options = groups.Select(group => new CloseableAppOption(group)).ToList();
        LstCloseableApps.ItemsSource = _options;
        UpdateSummary();
    }

    private void RefreshList(Func<CloseableAppOption, bool> selector)
    {
        foreach (var option in _options) option.IsSelected = selector(option);
        LstCloseableApps.Items.Refresh();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selected = _options.Where(o => o.IsSelected).ToList();
        double totalMb = selected.Sum(o => o.Group.TotalRamMb);
        BtnConfirmClose.IsEnabled = selected.Count > 0;
        TxtSelectionSummary.Text = selected.Count == 0
            ? "Marca al menos una aplicación para cerrarla."
            : $"{selected.Count} app(s) seleccionadas · ~{totalMb:0} MB en memoria residente";
    }

    private void ItemSelection_Changed(object sender, RoutedEventArgs e) => UpdateSummary();
    private void BtnSelectMinimized_Click(object sender, RoutedEventArgs e) => RefreshList(o => o.Group.IsMinimized);
    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => RefreshList(_ => true);
    private void BtnSelectNone_Click(object sender, RoutedEventArgs e) => RefreshList(_ => false);

    private void BtnConfirmClose_Click(object sender, RoutedEventArgs e)
    {
        SelectedGroups = _options.Where(o => o.IsSelected).Select(o => o.Group).ToList();
        DialogResult = SelectedGroups.Count > 0;
    }
}
