using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SoluPulse.Desktop.Views;

public sealed partial class JournalView : UserControl
{
    public JournalView() => AvaloniaXamlLoader.Load(this);
}
