using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SoluPulse.Desktop.Views;

public sealed partial class NetworkView : UserControl
{
    public NetworkView() => AvaloniaXamlLoader.Load(this);
}
