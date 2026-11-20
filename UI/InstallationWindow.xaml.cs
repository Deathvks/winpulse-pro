using System;
using System.Windows;

namespace WinPulseOptimizer;

public partial class InstallationWindow : Window
{
    internal InstallationWindow(bool alreadyInstalled, Version version)
    {
        InitializeComponent();
        Title = alreadyInstalled ? "Actualizar WinPulse Pro" : "Instalar WinPulse Pro";
        Heading.Text = $"{Title} {version.ToString(3)}";
        Description.Text = alreadyInstalled
            ? "Se preparará la copia instalada y su entrada en Aplicaciones instaladas. Tus perfiles e historial se conservarán."
            : "Se añadirá un acceso a Inicio y podrás quitar WinPulse desde Aplicaciones instaladas.";
        Confirm.Content = alreadyInstalled ? "Actualizar" : "Instalar";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
