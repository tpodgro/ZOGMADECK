using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ZogmaDeck.Native;
using ZogmaDeck.Services;
using ZogmaDeck.ViewModels;

namespace ZogmaDeck.Views;

/// <summary>
/// Panneau d'utilisation rapide : apparaît sur l'écran utilisé (centré, ou à la position mémorisée après un déplacement),
/// sans déplacer les autres fenêtres. Fond transparent possible (fenêtre « layered »), déplaçable par glisser,
/// redimensionnable par la poignée ou Ctrl + molette (agrandit les boutons du profil affiché).
/// </summary>
public partial class PanelWindow : Window, IPanelHost
{
    private readonly DeckRuntime _rt;
    private readonly PanelViewModel _vm;
    private IntPtr _monitor;
    private DateTime _lastAutoHide = DateTime.MinValue;
    private bool _resizing;
    private Win32.POINT _resizeStart;
    private double _resizeStartWidth, _resizeStartHeight;
    private ResizeEdge _resizeEdge;

    private enum ResizeEdge { Right, Bottom, Corner }
    private bool _previewMode;
    private bool? _blurApplied;
    private const double PanelCornerRadius = 20; // identique à PanelViewModel.PanelCorner

    public PanelWindow(DeckRuntime runtime, PanelViewModel vm)
    {
        InitializeComponent();
        _rt = runtime;
        _vm = vm;
        DataContext = vm;
        Topmost = runtime.Config.Settings.AlwaysOnTop;
        runtime.SettingsChanged += (_, _) =>
        {
            Topmost = _rt.Config.Settings.AlwaysOnTop;
            ApplyBackdrop();
            if (!_resizing) ApplySizeMode();
            if (IsVisible) Reposition();
        };
        SourceInitialized += (_, _) => ApplyBackdrop();
        vm.CloseRequested += (_, _) => HidePanel();
        SizeChanged += (_, _) =>
        {
            if (IsVisible && !_resizing) Reposition();
        };
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
        MouseLeftButtonDown += OnBackgroundMouseDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
        foreach (var thumb in new[] { ResizeGrip, ResizeRight, ResizeBottom })
        {
            thumb.DragStarted += OnResizeStarted;
            thumb.DragDelta += OnResizeDelta;
            thumb.DragCompleted += OnResizeCompleted;
        }
        ApplySizeMode();
        Root.ContextMenuOpening += (_, e) =>
        {
            // Le menu des tuiles reste prioritaire ; celui-ci concerne le fond du panneau.
            if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is not null) return;
            Root.ContextMenu = BuildPanelMenu();
        };
        Closing += (_, e) =>
        {
            // Fermer le panneau le masque seulement ; « Quitter » arrête l'application.
            if (!((App)Application.Current).IsShuttingDown)
            {
                e.Cancel = true;
                HidePanel();
            }
        };
    }

    public bool IsPanelVisible => IsVisible;
    private Core.Model.PanelAppearance Look => _rt.Config.Settings.Panel;

    public void HidePanel()
    {
        _previewMode = false;
        if (IsVisible) Hide();
    }

    /// <summary>Aperçu depuis l'éditeur : affiché sans prendre le focus et sans masquage automatique.</summary>
    public void ShowPreview()
    {
        _previewMode = true;
        Win32.GetCursorPos(out var pt);
        _monitor = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
        new WindowInteropHelper(this).EnsureHandle();
        _vm.Refresh();
        ShowActivated = false;
        if (!IsVisible) Show();
        ShowActivated = true;
        Reposition();
    }

    /// <summary>Affiche ou masque. Un clic sur l'icône juste après un masquage automatique (perte de focus) n'est pas ré-affiché.</summary>
    public void Toggle(bool fromHotkey)
    {
        if (IsVisible && IsActive)
        {
            HidePanel();
            return;
        }
        if (!fromHotkey && (DateTime.UtcNow - _lastAutoHide).TotalMilliseconds < 400) return;
        ShowPanel(fromHotkey);
    }

    public void ShowPanel(bool fromHotkey)
    {
        _previewMode = false;
        _rt.Focus.CaptureForeground();
        _monitor = PickMonitor(fromHotkey);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!IsVisible)
        {
            _vm.Refresh();
            ApplyMaxSize();
            Reposition();
            Show();
        }
        Reposition();
        Activate();
        Win32.SetForegroundWindow(hwnd);
        // Le focus reste sur la fenêtre (pas d'anneau de focus parasite) ; les flèches le placent sur la première tuile.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Keyboard.FocusedElement is not Button b || !IsAncestorOf(b)) Focus();
        });
    }

    private IntPtr PickMonitor(bool fromHotkey)
    {
        var target = _rt.Focus.Target;
        if (fromHotkey && target != IntPtr.Zero && Win32.IsWindowVisible(target))
            return Win32.MonitorFromWindow(target, Win32.MONITOR_DEFAULTTONEAREST);
        Win32.GetCursorPos(out var pt);
        return Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
    }

    private static (Win32.RECT Work, double Scale) MonitorInfo(IntPtr monitor)
    {
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(monitor, ref mi);
        var scale = Win32.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
        return (mi.rcWork, scale);
    }

    private void ApplyMaxSize()
    {
        if (_monitor == IntPtr.Zero) return;
        var (work, scale) = MonitorInfo(_monitor);
        MaxHeight = work.Height / scale * 0.96;
        MaxWidth = work.Width / scale * 0.98;
    }

    /// <summary>
    /// Place la fenêtre avec sa taille réelle en pixels (gère les écrans de DPI différents) :
    /// centrée, ou centrée sur la position mémorisée (en fraction de l'écran), toujours entièrement visible.
    /// </summary>
    private void Reposition()
    {
        if (_monitor == IntPtr.Zero) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var r)) return;
        ApplyMaxSize();
        var (work, _) = MonitorInfo(_monitor);
        var remembered = Look.Position == Core.Model.PanelPositionMode.Remembered;
        var cx = work.Left + (remembered ? Look.PositionX : 0.5) * work.Width;
        var cy = work.Top + (remembered ? Look.PositionY : 0.5) * work.Height;
        var x = (int)Math.Round(Math.Clamp(cx - r.Width / 2.0, work.Left, Math.Max(work.Left, work.Right - r.Width)));
        var y = (int)Math.Round(Math.Clamp(cy - r.Height / 2.0, work.Top, Math.Max(work.Top, work.Bottom - r.Height)));
        if (x != r.Left || y != r.Top)
            Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    /// <summary>Mémorise le centre actuel du panneau, en fraction de la zone de travail de son écran.</summary>
    private void RememberPosition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var r)) return;
        _monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var (work, _) = MonitorInfo(_monitor);
        Look.Position = Core.Model.PanelPositionMode.Remembered;
        Look.PositionX = Math.Clamp((r.Left + r.Width / 2.0 - work.Left) / Math.Max(1, work.Width), 0, 1);
        Look.PositionY = Math.Clamp((r.Top + r.Height / 2.0 - work.Top) / Math.Max(1, work.Height), 0, 1);
        _rt.NotifySettingsChanged();
    }

    // ---------- Verre dépoli ----------

    /// <summary>Flou de l'arrière-plan (composition Windows) et découpe arrondie qui lui donne la forme du panneau.</summary>
    private void ApplyBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var blur = Look.BlurBehind;
        if (_blurApplied != blur)
        {
            if (!Win32.SetBlurBehind(hwnd, blur) && blur) Core.Diagnostics.Log.Warn("Flou d'arrière-plan non pris en charge par ce système.");
            _blurApplied = blur;
        }
        // Avec le flou, DWM arrondit et lisse les coins (le flou est découpé avec) ; sans flou, WPF dessine ses propres coins.
        Win32.SetSystemRoundedCorners(hwnd, blur);
    }

    /// <summary>Taille automatique (selon les colonnes) ou libre (choisie à la souris, mémorisée).</summary>
    private void ApplySizeMode()
    {
        if (Look.SizeMode == Core.Model.PanelSizeMode.Manual)
        {
            SizeToContent = SizeToContent.Manual;
            Width = Look.PanelWidth;
            Height = Look.PanelHeight;
        }
        else
        {
            ClearValue(WidthProperty);
            ClearValue(HeightProperty);
            SizeToContent = SizeToContent.WidthAndHeight;
        }
    }

    // ---------- Déplacement ----------

    /// <summary>Clic gauche sur le fond (pas sur un bouton, qui consomme l'événement) : déplacement de la fenêtre.</summary>
    private void OnBackgroundMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || _resizing) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return; // bouton déjà relâché
        }
        RememberPosition();
    }

    // ---------- Redimensionnement (taille du panneau, pas des boutons) ----------

    private void OnResizeStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _resizing = true;
        _resizeEdge = sender == ResizeRight ? ResizeEdge.Right : sender == ResizeBottom ? ResizeEdge.Bottom : ResizeEdge.Corner;
        Win32.GetCursorPos(out _resizeStart);
        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        if (Look.SizeMode != Core.Model.PanelSizeMode.Manual)
        {
            // Premier redimensionnement : on passe en taille libre à partir de la taille actuelle.
            Look.SizeMode = Core.Model.PanelSizeMode.Manual;
            Look.PanelWidth = ActualWidth;
            Look.PanelHeight = ActualHeight;
            ApplySizeMode();
            _vm.Refresh();
        }
    }

    private void OnResizeDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = Win32.GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        Win32.GetCursorPos(out var now);
        var tile = _rt.Config.ActiveProfile.ButtonSize + Look.TileSpacing;
        // Minimum au plus juste : marges réelles du panneau (traits de profils, bords) + une rangée / une colonne de boutons.
        var chromeWidth = Math.Max(0, ActualWidth - Scroller.ActualWidth);
        var chromeHeight = Math.Max(0, ActualHeight - Scroller.ActualHeight);
        var minWidth = Math.Max(Core.Model.PanelAppearance.MinPanelSize, chromeWidth + tile);
        var minHeight = Math.Max(Core.Model.PanelAppearance.MinPanelSize, chromeHeight + tile);
        if (_resizeEdge != ResizeEdge.Bottom)
            Width = Math.Clamp(_resizeStartWidth + (now.X - _resizeStart.X) / scale, minWidth, Math.Max(minWidth, MaxWidth));
        if (_resizeEdge != ResizeEdge.Right)
            Height = Math.Clamp(_resizeStartHeight + (now.Y - _resizeStart.Y) / scale, minHeight, Math.Max(minHeight, MaxHeight));
    }

    private void OnResizeCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _resizing = false;
        Look.PanelWidth = ActualWidth;
        Look.PanelHeight = ActualHeight;
        RememberPosition(); // le coin haut-gauche reste où l'utilisateur l'a laissé
    }

    /// <summary>Ctrl + molette : taille des boutons (indépendante de la taille du panneau).</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        SetButtonSize(_rt.Config.ActiveProfile.ButtonSize + Math.Sign(e.Delta) * 4);
        e.Handled = true;
    }

    private void SetButtonSize(int size)
    {
        var profile = _rt.Config.ActiveProfile;
        size = Math.Clamp(size, Core.Model.Profile.MinButtonSize, Core.Model.Profile.MaxButtonSize);
        if (size == profile.ButtonSize) return;
        profile.ButtonSize = size;
        _vm.Refresh();
        _rt.NotifyLayoutChanged();
    }

    private void SetColumns(int delta)
    {
        var profile = _rt.Config.ActiveProfile;
        var columns = Math.Clamp(profile.Columns + delta, Core.Model.Profile.MinColumns, Core.Model.Profile.MaxColumns);
        if (columns == profile.Columns) return;
        profile.Columns = columns;
        _vm.Refresh();
        _rt.NotifyLayoutChanged();
    }

    private void SetAutoSize()
    {
        Look.SizeMode = Core.Model.PanelSizeMode.Auto;
        _rt.NotifySettingsChanged();
    }

    private ContextMenu BuildPanelMenu()
    {
        var profile = _rt.Config.ActiveProfile;
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action, bool enabled = true) =>
            new() { Header = header, IsEnabled = enabled, Command = new RelayCommand(action) };
        if (Look.SizeMode == Core.Model.PanelSizeMode.Manual)
        {
            menu.Items.Add(Item("Taille automatique (selon les colonnes du profil)", SetAutoSize));
        }
        else
        {
            menu.Items.Add(Item("Ajouter une colonne", () => SetColumns(1), profile.Columns < Core.Model.Profile.MaxColumns));
            menu.Items.Add(Item("Retirer une colonne", () => SetColumns(-1), profile.Columns > Core.Model.Profile.MinColumns));
        }
        menu.Items.Add(Item("Boutons plus grands (Ctrl + molette)", () => SetButtonSize(profile.ButtonSize + 8)));
        menu.Items.Add(Item("Boutons plus petits", () => SetButtonSize(profile.ButtonSize - 8)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Recentrer le panneau", () =>
        {
            Look.Position = Core.Model.PanelPositionMode.Centered;
            _rt.NotifySettingsChanged();
            Reposition();
        }, Look.Position == Core.Model.PanelPositionMode.Remembered));
        menu.Items.Add(Item("Ajouter un bouton…", () => _vm.AddButtonCommand.Execute(null)));
        menu.Items.Add(Item("Ouvrir l'éditeur… (Ctrl+E)", () => ((App)Application.Current).OpenEditor()));
        menu.Items.Add(Item("Personnaliser l'apparence…", () => ((App)Application.Current).OpenEditor(tab: 2)));
        menu.Items.Add(Item("Masquer (Échap)", HidePanel));
        return menu;
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!IsVisible || _previewMode || !_rt.Config.Settings.HideOnDeactivate || _rt.BusyCount > 0) return;
        _lastAutoHide = DateTime.UtcNow;
        HidePanel();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (key == Key.Escape)
        {
            HidePanel();
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.Control && key == Key.E)
        {
            _vm.EditCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (mods.HasFlag(ModifierKeys.Control) && key is Key.Tab or Key.PageDown or Key.PageUp)
        {
            var back = key == Key.PageUp || (key == Key.Tab && mods.HasFlag(ModifierKeys.Shift));
            _rt.SwitchRelative(back ? -1 : 1);
            e.Handled = true;
            return;
        }
        var digit = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
            _ => -1,
        };
        if (digit >= 0 && mods == ModifierKeys.Control)
        {
            if (digit < _rt.Config.Profiles.Count) _rt.SwitchTo(_rt.Config.Profiles[digit].Id);
            e.Handled = true;
            return;
        }
        if (digit >= 0 && mods == ModifierKeys.None)
        {
            var tiles = _vm.Tiles.OfType<PanelButtonViewModel>().ToList();
            if (digit < tiles.Count)
            {
                FocusTile(digit, showFocus: true);
                _vm.RunCommand.Execute(tiles[digit]);
            }
            e.Handled = true;
            return;
        }
        if (key is Key.Left or Key.Right or Key.Up or Key.Down && (Keyboard.FocusedElement is not Button b || !TilesHost.IsAncestorOf(b)))
        {
            FocusTile(0, showFocus: true);
            e.Handled = true;
        }
    }

    private void FocusTile(int index, bool showFocus)
    {
        if (TilesHost.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container) return;
        if (showFocus && FindChild<Button>(container) is { } button) Keyboard.Focus(button);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
