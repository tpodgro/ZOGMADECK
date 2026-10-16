using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ZogmaDeck.Helpers;

/// <summary>Pinceaux et effets du style « verre dépoli » (glassmorphism), figés et partagés entre toutes les tuiles.</summary>
public static class Glass
{
    private static SolidColorBrush White(byte alpha) => ColorHelper.Brush(Color.FromArgb(alpha, 255, 255, 255));

    private static LinearGradientBrush Gradient(Point start, Point end, params (Color Color, double Offset)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (c, o) in stops) b.GradientStops.Add(new GradientStop(c, o));
        b.Freeze();
        return b;
    }

    private static Color W(byte a) => Color.FromArgb(a, 255, 255, 255);

    /// <summary>Remplissage d'une tuile : blanc translucide.</summary>
    public static readonly Brush TileFill = White(0x2E);

    /// <summary>Contour lumineux : plus clair en haut à gauche, comme une arête de verre éclairée.</summary>
    public static readonly Brush TileBorder = Gradient(new Point(0, 0), new Point(1, 1), (W(0xB0), 0), (W(0x40), 0.5), (W(0x26), 1));

    /// <summary>Reflet doux sur la moitié haute.</summary>
    public static readonly Brush Sheen = Gradient(new Point(0, 0), new Point(0, 1), (W(0x22), 0), (W(0x06), 0.5), (W(0x00), 1));

    public static readonly Brush ChipFill = White(0x2A);
    public static readonly Brush ChipBorder = Gradient(new Point(0, 0), new Point(1, 1), (W(0x99), 0), (W(0x33), 1));
    public static readonly Brush Text = Brushes.White;

    public static readonly Brush AddFill = White(0x26);
    public static readonly Brush AddStroke = White(0xB0);

    public static readonly Brush PanelBorder = Gradient(new Point(0, 0), new Point(1, 1), (W(0xC0), 0), (W(0x50), 0.45), (W(0x30), 1));
    public static readonly Brush PanelSheen = Gradient(new Point(0, 0), new Point(0.6, 1), (W(0x14), 0), (W(0x00), 0.5));

    /// <summary>Ombre légère sous le texte blanc, pour rester lisible sur un fond clair.</summary>
    public static readonly Effect TextShadow = CreateShadow(5, 0.55);

    /// <summary>Ombre portée de la pastille d'icône.</summary>
    public static readonly Effect BadgeShadow = CreateShadow(8, 0.35, depth: 2);

    private static Effect CreateShadow(double blur, double opacity, double depth = 0)
    {
        var e = new DropShadowEffect { Color = Colors.Black, BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity };
        e.Freeze();
        return e;
    }

    /// <summary>Pastille d'icône : dégradé vertical de la couleur du bouton (plus clair en haut).</summary>
    public static Brush Badge(Color c) => Gradient(new Point(0, 0), new Point(0, 1), (Lighten(c, 0.28), 0), (c, 1));

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));
}
