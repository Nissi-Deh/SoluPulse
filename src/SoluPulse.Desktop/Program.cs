using System.Globalization;
using Avalonia;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop;

internal static class Program
{
    private const string MutexName = @"Local\SoluPulse.Desktop";
    private const string ShowEventName = @"Local\SoluPulse.Desktop.Show";

    public static event EventHandler? ShowRequested;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            // Appelé par le programme de désinstallation : retire les notifications de SoluPulse.
            NotificationService.Uninstall();
            return 0;
        }

        var french = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.DefaultThreadCurrentCulture = french;
        CultureInfo.DefaultThreadCurrentUICulture = french;

        // Une seule instance par session : une deuxième ouverture ramène simplement la fenêtre.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            showEvent.Set();
            return 0;
        }

        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                ShowRequested?.Invoke(null, EventArgs.Empty);
            }
        })
        { IsBackground = true, Name = "SoluPulse.ShowListener" };
        listener.Start();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
