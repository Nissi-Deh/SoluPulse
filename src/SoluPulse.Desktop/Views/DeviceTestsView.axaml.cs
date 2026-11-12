using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SoluPulse.Desktop.ViewModels;

namespace SoluPulse.Desktop.Views;

public sealed partial class DeviceTestsView : UserControl
{
    public DeviceTestsView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DeviceTestsViewModel model)
            {
                model.OpenTestWindow = OpenAsync;
            }
        };
    }

    private Task OpenAsync(string test)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        Window window = test == "ecran" ? ScreenTest() : KeyboardTest();
        return owner is null ? Task.CompletedTask : window.ShowDialog(owner);
    }

    /// <summary>Plein écran par couleurs unies, pour repérer les pixels morts ou bloqués.</summary>
    private static Window ScreenTest()
    {
        Color[] colors = [Colors.Black, Colors.White, Colors.Red, Color.FromRgb(0, 255, 0), Colors.Blue, Color.FromRgb(128, 128, 128)];
        var index = 0;
        var hint = new TextBlock
        {
            Text = "Clic ou Espace : couleur suivante · Échap : quitter",
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 24),
        };
        var window = new Window
        {
            WindowState = WindowState.FullScreen,
            SystemDecorations = SystemDecorations.None,
            Background = new SolidColorBrush(colors[0]),
            Content = hint,
            Cursor = new Cursor(StandardCursorType.None),
        };

        void Next()
        {
            index++;
            if (index >= colors.Length)
            {
                window.Close();
                return;
            }

            hint.IsVisible = false;
            window.Background = new SolidColorBrush(colors[index]);
        }

        window.PointerPressed += (_, _) => Next();
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                window.Close();
            }
            else if (e.Key is Key.Space or Key.Enter or Key.Right)
            {
                Next();
            }
        };
        return window;
    }

    /// <summary>Chaque touche pressée s'affiche : une touche absente de la liste ne répond pas.</summary>
    private static Window KeyboardTest()
    {
        var pressed = new HashSet<Key>();
        var keys = new WrapPanel { Orientation = Orientation.Horizontal };
        var count = new TextBlock { Text = "Appuyez sur chaque touche du clavier.", FontSize = 16 };
        var close = new Button { Content = "Fermer", HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = "SoluPulse — Test du clavier",
            Width = 760,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new StackPanel { Spacing = 6, [DockPanel.DockProperty] = Dock.Top, Children = { count, new TextBlock { Text = "Échap compte comme une touche : fermez avec le bouton.", Opacity = 0.7 } } },
                    new Panel { [DockPanel.DockProperty] = Dock.Bottom, Children = { close } },
                    new ScrollViewer { Margin = new Thickness(0, 12), Content = keys },
                },
            },
        };
        close.Click += (_, _) => window.Close();
        window.KeyDown += (_, e) =>
        {
            e.Handled = true;
            if (!pressed.Add(e.Key))
            {
                return;
            }

            keys.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(14, 116, 144)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
                Margin = new Thickness(4),
                Child = new TextBlock { Text = Label(e.Key), Foreground = Brushes.White },
            });
            count.Text = $"{pressed.Count} touche(s) différentes détectées.";
        };
        return window;
    }

    private static string Label(Key key) => key switch
    {
        Key.Space => "Espace",
        Key.Enter => "Entrée",
        Key.Back => "Retour arrière",
        Key.Tab => "Tab",
        Key.Escape => "Échap",
        Key.LeftShift or Key.RightShift => key == Key.LeftShift ? "Maj gauche" : "Maj droite",
        Key.LeftCtrl or Key.RightCtrl => key == Key.LeftCtrl ? "Ctrl gauche" : "Ctrl droite",
        Key.LeftAlt => "Alt",
        Key.RightAlt => "Alt Gr",
        Key.LWin or Key.RWin => "Windows",
        Key.CapsLock => "Verr. Maj",
        Key.Delete => "Suppr",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Left => "←",
        Key.Right => "→",
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        >= Key.NumPad0 and <= Key.NumPad9 => $"Pavé {(int)(key - Key.NumPad0)}",
        _ => key.ToString(),
    };
}
