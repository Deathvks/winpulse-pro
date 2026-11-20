using System.Windows;

namespace WinPulseOptimizer;

public partial class GameProfileImportWindow : Window
{
    private readonly GameProfile[] _existing;
    private readonly PortableGameProfile[] _incoming;
    internal GameProfileImportResult Preview { get; private set; }
    internal bool ReplaceNames => SwReplaceNames.IsChecked == true;

    internal GameProfileImportWindow(string fileName, GameProfile[] existing, PortableGameProfile[] incoming)
    {
        _existing = existing;
        _incoming = incoming;
        InitializeComponent();
        TxtFileName.Text = fileName;
        Preview = GameProfileTransfer.Preview(_existing, _incoming, ReplaceNames);
        RenderPreview();
    }

    private void RenderPreview()
    {
        GridImport.ItemsSource = Preview.Rows;
        BtnConfirmImport.IsEnabled = Preview.Added + Preview.Replaced > 0;
        TxtImportSummary.Text = $"{Preview.Added} nuevos · {Preview.Replaced} reemplazos · " +
            $"{Preview.Rows.Length - Preview.Added - Preview.Replaced} omitidos";
    }

    private void ReplaceNames_Changed(object sender, RoutedEventArgs e)
    {
        Preview = GameProfileTransfer.Preview(_existing, _incoming, ReplaceNames);
        RenderPreview();
    }

    private void ConfirmImport_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
