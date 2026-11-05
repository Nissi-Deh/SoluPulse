using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SoluPulse.Desktop.Views;

public sealed partial class InventoryView : UserControl
{
    public InventoryView() => AvaloniaXamlLoader.Load(this);
}
