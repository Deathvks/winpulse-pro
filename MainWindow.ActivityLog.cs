using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace WinPulseOptimizer;

public partial class MainWindow
{
    private const double MinMainTabsWidth = 730;
    private const double MinActivityLogWidth = 270;
    private const double MaxActivityLogWidth = 520;
    private const double ActivityLogSplitterWidth = 10;
    private double _activityLogWidth = 340;

    private void MainLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && ActivityLogPanel?.Visibility == Visibility.Visible) UpdateActivityLogLayout();
    }

    private void BtnToggleActivityLog_Click(object sender, RoutedEventArgs e) =>
        SetActivityLogOpen(ActivityLogPanel.Visibility != Visibility.Visible);

    private void SetActivityLogOpen(bool open)
    {
        ActivityLogPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        UpdateActivityLogLayout();
        if (open) AnimateSlideFadeIn(ActivityLogPanel, 20, 0, 210);
        string action = open ? "Cerrar registro de actividad" : "Abrir registro de actividad";
        BtnToggleActivityLog.ToolTip = action;
        AutomationProperties.SetName(BtnToggleActivityLog, action);

        if (!open)
        {
            BtnToggleActivityLog.Focus();
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (ActivityLogPanel.Visibility != Visibility.Visible) return;
            TxtLog.Focus();
            TxtLog.ScrollToEnd();
        }));
    }

    private void UpdateActivityLogLayout()
    {
        bool open = ActivityLogPanel.Visibility == Visibility.Visible;
        double available = MainLayout.ActualWidth;
        double dockWidth = Math.Min(MaxActivityLogWidth, available - MinMainTabsWidth - ActivityLogSplitterWidth);
        bool docked = open && dockWidth >= MinActivityLogWidth;

        ActivityLogSplitter.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        ActivityLogGap.Width = new GridLength(docked ? ActivityLogSplitterWidth : 0);
        ActivityLogColumn.MinWidth = docked ? MinActivityLogWidth : 0;
        ActivityLogColumn.MaxWidth = docked ? dockWidth : 0;
        ActivityLogColumn.Width = new GridLength(docked ? Math.Clamp(_activityLogWidth, MinActivityLogWidth, dockWidth) : 0);

        Grid.SetColumn(ActivityLogPanel, docked ? 2 : 0);
        ActivityLogPanel.HorizontalAlignment = docked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        ActivityLogPanel.Width = docked ? double.NaN : Math.Min(_activityLogWidth, available);
    }

    private void ActivityLogSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!e.Canceled && ActivityLogSplitter.Visibility == Visibility.Visible) _activityLogWidth = ActivityLogColumn.Width.Value;
    }

    private void ActivityLogSplitter_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right && ActivityLogSplitter.Visibility == Visibility.Visible)
            _activityLogWidth = ActivityLogColumn.Width.Value;
    }

    private void ActivityLogPanel_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SetActivityLogOpen(false);
        e.Handled = true;
    }
}
