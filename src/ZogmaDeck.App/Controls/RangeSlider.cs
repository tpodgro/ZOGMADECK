using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ZogmaDeck.Controls;

/// <summary>
/// Curseur à deux poignées (début et fin d'un intervalle). Glisser une poignée, cliquer sur la piste pour déplacer la
/// poignée la plus proche, ou utiliser les flèches (Maj : pas ×10) quand une poignée a le focus.
/// </summary>
public sealed class RangeSlider : Control
{
    public static readonly DependencyProperty MinimumProperty = Register(nameof(Minimum), 0d);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), 1d);
    public static readonly DependencyProperty LowerValueProperty = Register(nameof(LowerValue), 0d, twoWay: true);
    public static readonly DependencyProperty UpperValueProperty = Register(nameof(UpperValue), 1d, twoWay: true);
    public static readonly DependencyProperty SmallChangeProperty = Register(nameof(SmallChange), 0.1d);

    /// <summary>Fin d'un glissement (pour, par exemple, réécouter l'intervalle choisi).</summary>
    public static readonly RoutedEvent RangeCommittedEvent = EventManager.RegisterRoutedEvent(
        nameof(RangeCommitted), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RangeSlider));

    private const double ThumbSize = 18;
    private readonly Canvas _canvas = new();
    private readonly Rectangle _track = new() { Height = 4, RadiusX = 2, RadiusY = 2 };
    private readonly Rectangle _range = new() { Height = 4, RadiusX = 2, RadiusY = 2 };
    private readonly Thumb _lower, _upper;

    private static DependencyProperty Register(string name, double value, bool twoWay = false) =>
        DependencyProperty.Register(name, typeof(double), typeof(RangeSlider), new FrameworkPropertyMetadata(value,
            (twoWay ? FrameworkPropertyMetadataOptions.BindsTwoWayByDefault : 0) | FrameworkPropertyMetadataOptions.AffectsArrange,
            (d, _) => ((RangeSlider)d).UpdateLayoutPositions()));

    public RangeSlider()
    {
        Focusable = false;
        Height = 32;
        _track.SetResourceReference(Shape.FillProperty, "Pd.BorderStrong");
        _range.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
        _lower = CreateThumb("Début de l'extrait");
        _upper = CreateThumb("Fin de l'extrait");
        _lower.DragDelta += (_, e) => MoveThumb(lower: true, e.HorizontalChange);
        _upper.DragDelta += (_, e) => MoveThumb(lower: false, e.HorizontalChange);
        _lower.DragCompleted += (_, _) => RaiseEvent(new RoutedEventArgs(RangeCommittedEvent, this));
        _upper.DragCompleted += (_, _) => RaiseEvent(new RoutedEventArgs(RangeCommittedEvent, this));
        _canvas.Children.Add(_track);
        _canvas.Children.Add(_range);
        _canvas.Children.Add(_lower);
        _canvas.Children.Add(_upper);
        _canvas.Background = Brushes.Transparent; // clics sur la piste
        _canvas.MouseLeftButtonDown += OnTrackClick;
        AddVisualChild(_canvas);
        AddLogicalChild(_canvas);
    }

    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double LowerValue { get => (double)GetValue(LowerValueProperty); set => SetValue(LowerValueProperty, value); }
    public double UpperValue { get => (double)GetValue(UpperValueProperty); set => SetValue(UpperValueProperty, value); }
    public double SmallChange { get => (double)GetValue(SmallChangeProperty); set => SetValue(SmallChangeProperty, value); }

    public event RoutedEventHandler RangeCommitted
    {
        add => AddHandler(RangeCommittedEvent, value);
        remove => RemoveHandler(RangeCommittedEvent, value);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _canvas;

    private Thumb CreateThumb(string name)
    {
        var ellipse = new FrameworkElementFactory(typeof(Ellipse));
        ellipse.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
        ellipse.SetValue(Shape.StrokeProperty, Brushes.White);
        ellipse.SetValue(Shape.StrokeThicknessProperty, 2.0);
        var thumb = new Thumb
        {
            Width = ThumbSize,
            Height = ThumbSize,
            Cursor = Cursors.SizeWE,
            Focusable = true,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = ellipse },
            FocusVisualStyle = null,
        };
        AutomationProperties.SetName(thumb, name);
        thumb.KeyDown += (_, e) => OnThumbKey(thumb == _lower, e);
        thumb.GotKeyboardFocus += (_, _) => thumb.Opacity = 0.8;
        thumb.LostKeyboardFocus += (_, _) => thumb.Opacity = 1;
        return thumb;
    }

    private double _width;
    private double Usable => Math.Max(1, _width - ThumbSize);
    private double Span => Math.Max(1e-9, Maximum - Minimum);
    private double ToX(double value) => (Math.Clamp(value, Minimum, Maximum) - Minimum) / Span * Usable;
    private double ToValue(double x) => Minimum + Math.Clamp(x / Usable, 0, 1) * Span;

    private void MoveThumb(bool lower, double dx)
    {
        var x = ToX(lower ? LowerValue : UpperValue) + dx;
        SetValueClamped(lower, ToValue(x));
    }

    private void SetValueClamped(bool lower, double value)
    {
        var minGap = SmallChange;
        if (lower) LowerValue = Math.Clamp(value, Minimum, Math.Max(Minimum, UpperValue - minGap));
        else UpperValue = Math.Clamp(value, Math.Min(Maximum, LowerValue + minGap), Maximum);
    }

    private void OnTrackClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Thumb || e.OriginalSource is FrameworkElement { TemplatedParent: Thumb }) return;
        var value = ToValue(e.GetPosition(_canvas).X - ThumbSize / 2);
        var lower = Math.Abs(value - LowerValue) <= Math.Abs(value - UpperValue);
        SetValueClamped(lower, value);
        (lower ? _lower : _upper).Focus();
        RaiseEvent(new RoutedEventArgs(RangeCommittedEvent, this));
        e.Handled = true;
    }

    private void OnThumbKey(bool lower, KeyEventArgs e)
    {
        var step = SmallChange * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
        var current = lower ? LowerValue : UpperValue;
        switch (e.Key)
        {
            case Key.Left or Key.Down: SetValueClamped(lower, current - step); break;
            case Key.Right or Key.Up: SetValueClamped(lower, current + step); break;
            case Key.Home: SetValueClamped(lower, Minimum); break;
            case Key.End: SetValueClamped(lower, Maximum); break;
            default: return;
        }
        e.Handled = true;
        RaiseEvent(new RoutedEventArgs(RangeCommittedEvent, this));
    }

    protected override Size MeasureOverride(Size constraint)
    {
        _canvas.Measure(constraint);
        return new Size(double.IsInfinity(constraint.Width) ? 200 : constraint.Width, Height);
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        _canvas.Arrange(new Rect(arrangeBounds));
        UpdateLayoutPositions(arrangeBounds.Width);
        return arrangeBounds;
    }

    private void UpdateLayoutPositions() => UpdateLayoutPositions(ActualWidth);

    private void UpdateLayoutPositions(double width)
    {
        if (width <= 0) return;
        _width = width; // ActualWidth n'est pas encore à jour pendant ArrangeOverride
        var mid = Height / 2;
        _track.Width = Math.Max(0, width - ThumbSize);
        Canvas.SetLeft(_track, ThumbSize / 2);
        Canvas.SetTop(_track, mid - _track.Height / 2);
        var x1 = ToX(LowerValue);
        var x2 = ToX(UpperValue);
        Canvas.SetLeft(_lower, x1);
        Canvas.SetTop(_lower, mid - ThumbSize / 2);
        Canvas.SetLeft(_upper, x2);
        Canvas.SetTop(_upper, mid - ThumbSize / 2);
        // Valeurs lisibles par les lecteurs d'écran (et par les tests d'interface).
        AutomationProperties.SetHelpText(_lower, LowerValue.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + " / " + Maximum.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
        AutomationProperties.SetHelpText(_upper, UpperValue.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + " / " + Maximum.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
        Canvas.SetLeft(_range, x1 + ThumbSize / 2);
        Canvas.SetTop(_range, mid - _range.Height / 2);
        _range.Width = Math.Max(0, x2 - x1);
    }
}
