using System.Windows.Interop;

namespace ZogmaDeck.Services;

/// <summary>
/// Fenêtre Win32 invisible recevant les messages système : raccourci global (WM_HOTKEY), icône de notification,
/// changement de thème (WM_SETTINGCHANGE) et redémarrage de l'Explorateur (TaskbarCreated).
/// Fenêtre de premier niveau (pas « message-only ») car ces deux derniers messages ne sont diffusés qu'à ce type de fenêtre.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly HwndSource _source;

    public MessageWindow()
    {
        var p = new HwndSourceParameters("ZogmaDeck.MessageWindow")
        {
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        };
        _source = new HwndSource(p);
    }

    public IntPtr Handle => _source.Handle;

    public void AddHook(HwndSourceHook hook) => _source.AddHook(hook);

    public void Dispose() => _source.Dispose();
}
