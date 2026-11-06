using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace SoluPulse.Desktop.Views;

/// <summary>
/// Dessine une icône de <see cref="Services.Icons"/> à la taille du contrôle, dans la couleur du texte
/// qui l'entoure (héritée), pour suivre le thème et l'état sélectionné sans style supplémentaire.
/// </summary>
public sealed class IconView : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<IconView, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<IconView>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<IconView, double>(nameof(StrokeThickness), 1.8);

    static IconView()
    {
        AffectsRender<IconView>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        var offset = new Point((Bounds.Width - (24 * scale)) / 2, (Bounds.Height - (24 * scale)) / 2);
        var pen = new Pen(Foreground ?? Brushes.Black, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            context.DrawGeometry(null, pen, Data);
        }
    }
}
