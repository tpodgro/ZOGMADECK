using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZogmaDeck.ViewModels;

namespace ZogmaDeck.Controls;

/// <summary>
/// Réorganisation par glisser-déposer pour un ListBox : la commande reçoit un <see cref="MoveRequest"/>.
/// Alt+flèches fait la même chose au clavier.
/// </summary>
public static class DragReorder
{
    private const string Format = "ZogmaDeck.ReorderIndex";

    public static readonly DependencyProperty MoveCommandProperty = DependencyProperty.RegisterAttached(
        "MoveCommand", typeof(ICommand), typeof(DragReorder), new PropertyMetadata(null, OnChanged));

    private static readonly DependencyProperty StartPointProperty = DependencyProperty.RegisterAttached(
        "StartPoint", typeof(Point?), typeof(DragReorder));

    private static readonly DependencyProperty StartIndexProperty = DependencyProperty.RegisterAttached(
        "StartIndex", typeof(int), typeof(DragReorder), new PropertyMetadata(-1));

    public static ICommand? GetMoveCommand(DependencyObject d) => (ICommand?)d.GetValue(MoveCommandProperty);
    public static void SetMoveCommand(DependencyObject d, ICommand? v) => d.SetValue(MoveCommandProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox lb) return;
        lb.PreviewMouseLeftButtonDown -= OnDown;
        lb.PreviewMouseMove -= OnMove;
        lb.Drop -= OnDrop;
        lb.DragOver -= OnDragOver;
        lb.PreviewKeyDown -= OnKey;
        if (e.NewValue is null) return;
        lb.AllowDrop = true;
        lb.PreviewMouseLeftButtonDown += OnDown;
        lb.PreviewMouseMove += OnMove;
        lb.Drop += OnDrop;
        lb.DragOver += OnDragOver;
        lb.PreviewKeyDown += OnKey;
    }

    private static int IndexAt(ListBox lb, Point p)
    {
        var hit = lb.InputHitTest(p) as DependencyObject;
        while (hit is not null and not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
        return hit is ListBoxItem item ? lb.ItemContainerGenerator.IndexFromContainer(item) : -1;
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        var lb = (ListBox)sender;
        var p = e.GetPosition(lb);
        lb.SetValue(StartPointProperty, p);
        lb.SetValue(StartIndexProperty, IndexAt(lb, p));
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        var lb = (ListBox)sender;
        if (e.LeftButton != MouseButtonState.Pressed || lb.GetValue(StartPointProperty) is not Point start) return;
        var index = (int)lb.GetValue(StartIndexProperty);
        if (index < 0) return;
        var d = e.GetPosition(lb) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        lb.SetValue(StartPointProperty, null);
        DragDrop.DoDragDrop(lb, new DataObject(Format, index), DragDropEffects.Move);
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(Format) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        var lb = (ListBox)sender;
        if (!e.Data.GetDataPresent(Format)) return;
        var from = (int)e.Data.GetData(Format)!;
        var to = IndexAt(lb, e.GetPosition(lb));
        if (to < 0) to = lb.Items.Count - 1;
        if (from != to) GetMoveCommand(lb)?.Execute(new MoveRequest(from, to));
        e.Handled = true;
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        var lb = (ListBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers != ModifierKeys.Alt || lb.SelectedIndex < 0) return;
        var delta = key switch
        {
            Key.Left or Key.Up => -1,
            Key.Right or Key.Down => 1,
            _ => 0,
        };
        if (delta == 0) return;
        var from = lb.SelectedIndex;
        var to = from + delta;
        if (to < 0 || to >= lb.Items.Count) return;
        GetMoveCommand(lb)?.Execute(new MoveRequest(from, to));
        e.Handled = true;
        lb.Dispatcher.BeginInvoke(() => (lb.ItemContainerGenerator.ContainerFromIndex(to) as ListBoxItem)?.Focus());
    }
}
