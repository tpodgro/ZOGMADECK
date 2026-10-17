using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZogmaDeck.Helpers;

namespace ZogmaDeck.Controls;

/// <summary>
/// Sélecteur de couleur : bouton avec aperçu, et fenêtre surgissante (zone saturation/luminosité, barre de teinte, code
/// hexadécimal). La couleur choisie est appliquée en direct à <see cref="Color"/> (#RRGGBB, liaison bidirectionnelle).
/// </summary>
public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("#2F6FDB", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorPicker)d).OnColorChanged()));

    private double _h, _s = 1, _v = 1;
    private bool _updating;
    private UIElement? _dragging;

    public ColorPicker()
    {
        InitializeComponent();
        Loaded += (_, _) => OnColorChanged();
        SvArea.SizeChanged += (_, _) => UpdateThumbs();
        HueArea.SizeChanged += (_, _) => UpdateThumbs();
    }

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    private void OnColorChanged()
    {
        if (_updating) return;
        var c = ColorHelper.Parse(Color, Colors.Gray);
        var (h, s, v) = ToHsv(c);
        // Garde la teinte courante pour les gris (teinte indéfinie).
        if (s > 0.001 && v > 0.001) _h = h;
        _s = s;
        _v = v;
        Refresh(updateHex: true);
    }

    private void Refresh(bool updateHex)
    {
        var current = FromHsv(_h, _s, _v);
        var brush = ColorHelper.Brush(current);
        Swatch.Background = brush;
        Preview.Background = brush;
        HueFill.Background = ColorHelper.Brush(FromHsv(_h, 1, 1));
        if (updateHex)
        {
            _updating = true;
            HexBox.Text = ToHex(current);
            _updating = false;
        }
        UpdateThumbs();
    }

    private void UpdateThumbs()
    {
        Canvas.SetLeft(SvThumb, _s * SvArea.ActualWidth - SvThumb.Width / 2);
        Canvas.SetTop(SvThumb, (1 - _v) * SvArea.ActualHeight - SvThumb.Height / 2);
        Canvas.SetLeft(HueThumb, _h / 360 * HueArea.ActualWidth - HueThumb.Width / 2);
    }

    private void Commit()
    {
        var hex = ToHex(FromHsv(_h, _s, _v));
        _updating = true;
        Color = hex;
        HexBox.Text = hex;
        _updating = false;
        Refresh(updateHex: false);
    }

    // ----- Zone saturation / luminosité -----
    private void SvArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = SvArea;
        SvArea.CaptureMouse();
        SvArea.Focus();
        PickSv(e.GetPosition(SvArea));
    }

    private void SvArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging == SvArea && e.LeftButton == MouseButtonState.Pressed) PickSv(e.GetPosition(SvArea));
    }

    private void PickSv(Point p)
    {
        _s = Math.Clamp(p.X / Math.Max(1, SvArea.ActualWidth), 0, 1);
        _v = 1 - Math.Clamp(p.Y / Math.Max(1, SvArea.ActualHeight), 0, 1);
        Commit();
    }

    private void SvArea_KeyDown(object sender, KeyEventArgs e)
    {
        const double step = 0.02;
        switch (e.Key)
        {
            case Key.Left: _s = Math.Max(0, _s - step); break;
            case Key.Right: _s = Math.Min(1, _s + step); break;
            case Key.Up: _v = Math.Min(1, _v + step); break;
            case Key.Down: _v = Math.Max(0, _v - step); break;
            default: return;
        }
        e.Handled = true;
        Commit();
    }

    // ----- Teinte -----
    private void HueArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = HueArea;
        HueArea.CaptureMouse();
        HueArea.Focus();
        PickHue(e.GetPosition(HueArea));
    }

    private void HueArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging == HueArea && e.LeftButton == MouseButtonState.Pressed) PickHue(e.GetPosition(HueArea));
    }

    private void PickHue(Point p)
    {
        _h = Math.Clamp(p.X / Math.Max(1, HueArea.ActualWidth), 0, 1) * 360;
        if (_h >= 360) _h = 359.9;
        Commit();
    }

    private void HueArea_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: _h = (_h + 357) % 360; break;
            case Key.Right: _h = (_h + 3) % 360; break;
            default: return;
        }
        e.Handled = true;
        Commit();
    }

    private void Area_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    // ----- Code hexadécimal -----
    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        var text = HexBox.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7 || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return;
        _updating = true;
        Color = text.ToUpperInvariant();
        _updating = false;
        var (h, s, v) = ToHsv(ColorHelper.Parse(text, Colors.Gray));
        if (s > 0.001 && v > 0.001) _h = h;
        _s = s;
        _v = v;
        Refresh(updateHex: false);
    }

    // ----- Conversions -----
    internal static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * ((g - b) / d % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    internal static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
