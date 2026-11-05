using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SoluPulse.Desktop.Views;

public sealed partial class DashboardView : UserControl
{
    public DashboardView() => AvaloniaXamlLoader.Load(this);
}
