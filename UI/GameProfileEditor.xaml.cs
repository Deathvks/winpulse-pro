using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace WinPulseOptimizer;

public partial class GameProfileEditor : Window
{
    private sealed record BaseChoice(UsageProfile Profile, string Label);

    private sealed class Adjustment(string label, bool enabled)
    {
        public string Label { get; } = label;
        public bool Enabled { get; set; } = enabled;
    }

    private readonly GameProfile? _previous;
    private readonly GameProfile[] _existing;
    private readonly List<string> _selectedExecutablePaths = new();
    private Adjustment[] _adjustments = [];

    internal GameProfile? Profile { get; private set; }
    internal IReadOnlyList<string> SelectedExecutablePaths => _selectedExecutablePaths;

    internal GameProfileEditor(GameProfile? previous, GameProfile[] existing)
    {
        InitializeComponent();
        _previous = previous;
        _existing = existing;
        BaseChoice[] bases =
        [
            new(UsageProfile.GamingMax, "Jugar (Máximo rendimiento)"),
            new(UsageProfile.StudyWork, "Estudiar / Trabajar"),
            new(UsageProfile.ChillBattery, "Estar Chill")
        ];
        CmbPowerBase.ItemsSource = bases;
        CmbPowerBase.SelectedItem = bases.First(choice => choice.Profile == (previous?.PowerBase ?? UsageProfile.GamingMax));
        TxtName.Text = previous?.Name ?? "";
        if (previous != null)
            TxtExecutables.Text = string.Join(Environment.NewLine, previous.Executables.Select(exe => exe + ".exe"));
        ChkPreferDedicatedGpu.IsChecked = previous?.PreferDedicatedGpu == true;
        LoadAdjustments(previous?.Adjustments ?? MainWindow.DefaultProfileAdjustments(UsageProfile.GamingMax));
    }

    private void LoadAdjustments(TweakState state)
    {
        _adjustments =
        [
            new("Modo Juego de Windows", state.GameMode),
            new("Aceleración de GPU (HAGS)", state.Hags),
            new("Optimizar juegos en ventana", state.WindowedOptimizations),
            new("Temporizador de alta precisión", state.HighResTimer),
            new("Apagar la grabación de juegos", state.DisableGameDvr),
            new("Prioridad MMCSS para juegos", state.MmcssGaming),
            new("Priorizar la ventana activa", state.ForegroundPriority),
            new("Mantener los núcleos activos", state.DisableCoreParking),
            new("Pausar servicios de Windows", state.SuspendHeavyServices),
            new("Evitar ahorro en USB y PCIe", state.DisableUsbPciePowerSave),
            new("Ajustar latencia TCP", state.DisableNagle),
            new("Limitar apps en segundo plano", state.LimitBackgroundApps)
        ];
        LstAdjustments.ItemsSource = _adjustments;
    }

    private void BtnLoadDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (CmbPowerBase.SelectedItem is BaseChoice choice)
            LoadAdjustments(MainWindow.DefaultProfileAdjustments(choice.Profile));
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecciona el ejecutable del juego",
            Filter = "Ejecutables (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (string fullPath in dialog.FileNames)
        {
            if (!_selectedExecutablePaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                _selectedExecutablePaths.Add(fullPath);
        }
        var names = dialog.FileNames.Select(Path.GetFileName);
        TxtExecutables.Text = string.Join(Environment.NewLine,
            new[] { TxtExecutables.Text.Trim() }.Concat(names).Where(name => !string.IsNullOrEmpty(name)));
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string name = TxtName.Text.Trim();
            if (name.Length == 0) throw new FormatException("Escribe un nombre para el perfil.");
            if (_existing.Any(profile => profile.Id != _previous?.Id &&
                string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException("Ya existe un perfil con ese nombre.");
            string[] executables = MainWindow.ReadRules(TxtExecutables.Text);
            if (executables.Length == 0) throw new FormatException("Añade al menos un ejecutable.");
            var used = _existing.Where(profile => profile.Id != _previous?.Id)
                .FirstOrDefault(profile => profile.Executables.Intersect(executables, StringComparer.OrdinalIgnoreCase).Any());
            if (used != null) throw new FormatException($"Uno de los ejecutables ya pertenece a «{used.Name}».");
            if (CmbPowerBase.SelectedItem is not BaseChoice choice) throw new FormatException("Selecciona un plan base.");

            var settings = _adjustments.Select(adjustment => adjustment.Enabled).ToArray();
            var tweaks = new TweakState(settings[0], settings[1], settings[2], settings[3], settings[4], settings[5],
                settings[6], settings[7], settings[8], settings[9], settings[10], settings[11]);
            bool preferDgpu = ChkPreferDedicatedGpu.IsChecked == true;
            Guid revision = _previous != null && _previous.PowerBase == choice.Profile && _previous.Adjustments == tweaks &&
                _previous.PreferDedicatedGpu == preferDgpu
                ? _previous.Revision : Guid.NewGuid();
            Profile = new(_previous?.Id ?? Guid.NewGuid(), revision, name, choice.Profile, tweaks, executables, preferDgpu);
            DialogResult = true;
        }
        catch (FormatException ex)
        {
            TxtValidation.Text = ex.Message;
        }
    }
}
