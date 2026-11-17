using System.Windows;

namespace WinPulseOptimizer;

public partial class ProfileReportWindow : Window
{
    internal ProfileReportWindow(HistoryEntry entry)
    {
        InitializeComponent();
        DataContext = entry;
    }
}
