using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SoluPulse.Desktop.Views;

public sealed partial class AppsView : UserControl
{
    public AppsView() => AvaloniaXamlLoader.Load(this);
}
