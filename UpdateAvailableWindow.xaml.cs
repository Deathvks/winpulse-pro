using System;
using System.Windows;

namespace WinPulseOptimizer;

public partial class UpdateAvailableWindow : Window
{
    internal UpdateAvailableWindow(Version version)
    {
        InitializeComponent();
        UpdateHeading.Text = $"WinPulse {version.ToString(3)} disponible";
    }

    private void ShowUpdate_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
