using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SoluPulse.Desktop.Services;

/// <summary>
/// Couleurs d'état. Chacune garde un contraste d'au moins 4,5:1 avec du texte blanc (ENF-ERG-03),
/// et l'état est toujours aussi donné en texte, jamais par la couleur seule.
/// </summary>
public static class Palette
{
    public static readonly Color Ok = Color.Parse("#1E7A3C");
    public static readonly Color Information = Color.Parse("#1D5FA8");
    public static readonly Color Attention = Color.Parse("#8C4A00");
    public static readonly Color Critique = Color.Parse("#B3261E");
    public static readonly Color Unknown = Color.Parse("#5F6368");

    public static Color ForSeverity(string? severity) => severity switch
    {
        "critique" => Critique,
        "attention" => Attention,
        "information" => Information,
        "" or null => Ok,
        _ => Unknown,
    };

    public static Color ForScore(int score) => score switch
    {
        >= 80 => Ok,
        >= 50 => Attention,
        _ => Critique,
    };

    public static string SeverityLabel(string? severity) => severity switch
    {
        "critique" => "Critique",
        "attention" => "Attention",
        "information" => "Information",
        _ => "Tout va bien",
    };

    /// <summary>Icône de la zone de notification, colorée selon l'état du PC (EF-IHM-04).</summary>
    public static WindowIcon TrayIcon(Color color)
    {
        const int size = 64;
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            // Même tracé de pouls que la petite version du logo (Assets/solupulse.ico).
            context.DrawRectangle(new SolidColorBrush(color), null, new RoundedRect(new Rect(0, 0, size, size), 15));
            var pen = new Pen(Brushes.White, 7.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var geometry = new PolylineGeometry(
                [new(5.5, 35), new(21, 35), new(26.5, 18), new(34, 51), new(39.5, 27.5), new(43, 35), new(58.5, 35)],
                isFilled: false);
            context.DrawGeometry(null, pen, geometry);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    public static WindowIcon AppIcon() => new(AssetLoader.Open(new Uri("avares://SoluPulse.Desktop/Assets/solupulse.ico")));
}
