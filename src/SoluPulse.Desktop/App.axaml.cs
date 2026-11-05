using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SoluPulse.Desktop.Services;
using SoluPulse.Desktop.ViewModels;
using SoluPulse.Desktop.Views;

namespace SoluPulse.Desktop;

public sealed partial class App : Application
{
    private MainWindow? _window;
    private MainViewModel? _main;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // L'application reste dans la zone de notification quand la fenêtre est fermée.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var notifications = new NotificationService();
            notifications.Initialize();
            var client = new AgentClient();
            _main = new MainViewModel(client, notifications);
            _window = new MainWindow { DataContext = _main };
            _main.BringToFront = ShowWindow;
            _window.Closing += (_, e) =>
            {
                if (!_quitting)
                {
                    e.Cancel = true;
                    _window.Hide();
                }
            };

            var tray = TrayIcon.GetIcons(this)?.FirstOrDefault();
            if (tray is not null)
            {
                tray.Icon = Palette.TrayIcon(Palette.Unknown);
                tray.Clicked += (_, _) => ShowWindow();
                _main.StatusChanged += (_, status) =>
                {
                    tray.Icon = Palette.TrayIcon(status.ActiveAlerts == 0 ? Palette.Ok : Palette.ForSeverity(status.WorstSeverity));
                    tray.ToolTipText = status.ActiveAlerts == 0
                        ? $"SoluPulse : votre PC va bien ({status.GlobalScore}/100)"
                        : $"SoluPulse : {status.ActiveAlerts} alerte(s), score {status.GlobalScore}/100";
                };
            }

            Program.ShowRequested += (_, _) => Dispatcher.UIThread.Post(ShowWindow);
            desktop.Exit += (_, _) => client.Dispose();
            client.StartWatching();

            var background = desktop.Args?.Contains("--background", StringComparer.OrdinalIgnoreCase) == true;
            if (!background || NotificationService.WasLaunchedByNotification)
            {
                ShowWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _ = _main?.CurrentPage.RefreshAsync();
    }

    private void OnTrayOpen(object? sender, EventArgs e) => ShowWindow();

    private void OnTrayDiagnostic(object? sender, EventArgs e)
    {
        ShowWindow();
        _ = _main?.RunDiagnosticCommand.ExecuteAsync(null);
    }

    private void OnTrayQuit(object? sender, EventArgs e)
    {
        _quitting = true;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
