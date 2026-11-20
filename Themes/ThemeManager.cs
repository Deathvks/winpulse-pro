using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinPulseOptimizer;

public enum AppThemeMode { System, Dark, Light }

internal static class ThemeManager
{
    private const string PersonalizePath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    internal static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["CanvasBrush"] = "#101114",
        ["StageBrush"] = "#080909",
        ["PanelBrush"] = "#17181C",
        ["RaisedBrush"] = "#1D1F24",
        ["RowBrush"] = "#131418",
        ["RowStrokeBrush"] = "#26282E",
        ["BadgeBrush"] = "#15171C",
        ["ProgressTrackBrush"] = "#242730",
        ["StrokeBrush"] = "#35373C",
        ["DividerBrush"] = "#303236",
        ["HoverBrush"] = "#262930",
        ["TextBrush"] = "#F4F4F5",
        ["MutedBrush"] = "#B9BBC1",
        ["QuietBrush"] = "#8E9199",
        ["AccentBrush"] = "#C5FF41",
        ["AccentHoverBrush"] = "#D6FF75",
        ["AccentInkBrush"] = "#101114",
        ["AccentSoftBrush"] = "#1D2027",
        ["AccentBorderSoftBrush"] = "#4A4E58",
        ["SuccessBrush"] = "#75DFB6",
        ["WarningBrush"] = "#F2C97D",
        ["DangerBrush"] = "#FFB0B7",
        ["DangerSoftBrush"] = "#2E1D23",
        ["DangerHoverBrush"] = "#422630"
    };

    internal static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["CanvasBrush"] = "#F3F5F8",
        ["StageBrush"] = "#EAECEF",
        ["PanelBrush"] = "#FFFFFF",
        ["RaisedBrush"] = "#F8F9FC",
        ["RowBrush"] = "#F7F8FB",
        ["RowStrokeBrush"] = "#E1E5ED",
        ["BadgeBrush"] = "#EAECEF",
        ["ProgressTrackBrush"] = "#DCE0E8",
        ["StrokeBrush"] = "#CDD3DF",
        ["DividerBrush"] = "#E2E6EE",
        ["HoverBrush"] = "#EBEEF5",
        ["TextBrush"] = "#14161B",
        ["MutedBrush"] = "#4B5160",
        ["QuietBrush"] = "#6B7280",
        ["AccentBrush"] = "#4E8A00",
        ["AccentHoverBrush"] = "#3F7000",
        ["AccentInkBrush"] = "#FFFFFF",
        ["AccentSoftBrush"] = "#EEF6DF",
        ["AccentBorderSoftBrush"] = "#C2D99B",
        ["SuccessBrush"] = "#0F7A52",
        ["WarningBrush"] = "#B45309",
        ["DangerBrush"] = "#B91C1C",
        ["DangerSoftBrush"] = "#FEE2E2",
        ["DangerHoverBrush"] = "#FECACA"
    };

    private static bool _listening;
    private static bool _windowClassRegistered;

    internal static AppThemeMode CurrentMode { get; private set; } = AppThemeMode.System;
    internal static bool IsLightActive { get; private set; }
    internal static event Action? ThemeApplied;

    internal static AppThemeMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => AppThemeMode.Light,
        "dark" => AppThemeMode.Dark,
        _ => AppThemeMode.System
    };

    internal static string SerializeMode(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => "Light",
        AppThemeMode.Dark => "Dark",
        _ => "System"
    };

    internal static bool ResolveIsLightTheme(AppThemeMode mode, int? appsUseLightTheme) => mode switch
    {
        AppThemeMode.Light => true,
        AppThemeMode.Dark => false,
        _ => appsUseLightTheme == 1
    };

    internal static int? ReadSystemAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizePath);
            return key?.GetValue("AppsUseLightTheme") is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    internal static void ApplyTheme(AppThemeMode mode)
    {
        CurrentMode = mode;
        IsLightActive = ResolveIsLightTheme(mode, ReadSystemAppsUseLightTheme());

        if (Application.Current?.Resources is { } resources)
        {
            var palette = IsLightActive ? LightPalette : DarkPalette;
            foreach (var (key, hex) in palette)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                resources[key] = brush;
            }

            foreach (Window window in Application.Current.Windows)
                ApplyTitleBar(window);
        }

        ThemeApplied?.Invoke();
    }

    internal static void ApplyTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int useDarkMode = IsLightActive ? 0 : 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref useDarkMode, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref useDarkMode, sizeof(int));
        }
        catch { }
    }

    internal static void StartSystemSync()
    {
        if (!_windowClassRegistered)
        {
            _windowClassRegistered = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => { if (sender is Window w) ApplyTitleBar(w); }));
        }
        if (_listening) return;
        _listening = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    internal static void StopSystemSync()
    {
        if (!_listening) return;
        _listening = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (CurrentMode != AppThemeMode.System ||
            e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle))
            return;

        Application.Current?.Dispatcher.BeginInvoke(() => ApplyTheme(AppThemeMode.System));
    }
}
