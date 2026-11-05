using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SoluPulse.Desktop;

public sealed class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}
