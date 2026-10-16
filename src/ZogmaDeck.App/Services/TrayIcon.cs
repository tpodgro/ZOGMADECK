using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using ZogmaDeck.Core.Diagnostics;
using ZogmaDeck.Native;

namespace ZogmaDeck.Services;

public enum BalloonKind { Info, Warning, Error }

/// <summary>Icône de la zone de notification (Shell_NotifyIcon), sans Windows Forms. Le menu est un ContextMenu WPF (thème Fluent).</summary>
public sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = Win32.WM_APP + 1;
    private readonly MessageWindow _window;
    private readonly uint _taskbarCreated;
    private IntPtr _hIcon;
    private string _tooltip;
    private bool _added;

    public TrayIcon(MessageWindow window, string tooltip)
    {
        _window = window;
        _tooltip = tooltip;
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        _hIcon = LoadIcon();
        _window.AddHook(WndProc);
        Add();
    }

    public event EventHandler? Activated;
    public event EventHandler? MenuRequested;

    private void Add()
    {
        var data = NewData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data);
        if (!_added)
        {
            Log.Warn("Ajout de l'icône de notification impossible (Explorateur non prêt ?)");
            return;
        }
        data.uVersion = Win32.NOTIFYICON_VERSION_4;
        Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref data);
    }

    public void SetTooltip(string text)
    {
        _tooltip = text;
        if (!_added) return;
        var data = NewData(Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
    }

    public void ShowBalloon(string title, string text, BalloonKind kind = BalloonKind.Info)
    {
        if (!_added) return;
        var data = NewData(Win32.NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = kind switch
        {
            BalloonKind.Warning => Win32.NIIF_WARNING,
            BalloonKind.Error => Win32.NIIF_ERROR,
            _ => Win32.NIIF_INFO,
        };
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
    }

    private Win32.NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = Truncate(_tooltip, 127),
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            var evt = (int)(lParam.ToInt64() & 0xFFFF);
            switch (evt)
            {
                case Win32.NIN_SELECT:
                case Win32.NIN_KEYSELECT:
                    Activated?.Invoke(this, EventArgs.Empty);
                    break;
                case Win32.WM_CONTEXTMENU:
                    MenuRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // L'Explorateur a redémarré : il faut recréer l'icône.
            Add();
        }
        return IntPtr.Zero;
    }

    /// <summary>Charge l'icône embarquée à la taille « petite icône » du DPI courant.</summary>
    private IntPtr LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/ZogmaDeck;component/Assets/ZogmaDeck.ico"));
            using var ms = new MemoryStream();
            info!.Stream.CopyTo(ms);
            var ico = ms.ToArray();
            var dpi = Win32.GetDpiForWindow(_window.Handle);
            var size = Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, dpi == 0 ? 96 : dpi);
            if (size <= 0) size = 16;

            int count = BitConverter.ToUInt16(ico, 4);
            int bestOffset = 0, bestLength = 0, bestSize = int.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var e = 6 + 16 * i;
                var w = ico[e] == 0 ? 256 : ico[e];
                var len = BitConverter.ToInt32(ico, e + 8);
                var off = BitConverter.ToInt32(ico, e + 12);
                // plus petite image ≥ taille voulue (sinon la plus grande)
                var score = w >= size ? w : 10000 - w;
                if (score < bestSize)
                {
                    bestSize = score;
                    bestOffset = off;
                    bestLength = len;
                }
            }
            var bytes = ico.AsSpan(bestOffset, bestLength).ToArray();
            return Win32.CreateIconFromResourceEx(bytes, (uint)bytes.Length, true, 0x00030000, size, size, 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Chargement de l'icône impossible", ex);
            return IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData(0);
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
            _added = false;
        }
        if (_hIcon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
