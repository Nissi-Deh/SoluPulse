using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SoluPulse.Desktop.ViewModels;

namespace SoluPulse.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel main)
            {
                main.ShowCorrection = ShowCorrectionAsync;
            }
        };
    }

    private async Task ShowCorrectionAsync(CorrectionViewModel correction)
    {
        if (!IsVisible)
        {
            Show();
        }

        var dialog = new CorrectionWindow { DataContext = correction };
        correction.CloseRequested += (_, _) => dialog.Close();
        dialog.Closing += (_, e) =>
        {
            if (correction.Running)
            {
                e.Cancel = true; // Une correction en cours ne peut pas être abandonnée en fermant la fenêtre.
            }
        };
        dialog.Closed += (_, _) => _ = correction.ReleaseTokenAsync();
        var shown = dialog.ShowDialog(this);
        await correction.InitializeAsync();
        await shown;
    }
}
