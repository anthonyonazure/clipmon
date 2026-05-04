using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Clipmon.Services;

/// <summary>
/// Registers a system-wide hotkey via the Win32 RegisterHotKey API.
/// Default chord: Ctrl+Shift+V toggles the Clipmon tray popup.
///
/// We allocate a tiny hidden HwndSource just to receive WM_HOTKEY messages —
/// the real popup window cannot be the receiver because it is hidden by default.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private const int HotkeyId = 0x4E61;

    private HwndSource? _source;
    private bool _registered;

    public event EventHandler? Triggered;

    public GlobalHotkeyService()
    {
        var parameters = new HwndSourceParameters("ClipmonHotkey")
        {
            Width = 0,
            Height = 0,
            ParentWindow = IntPtr.Zero,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        _registered = RegisterHotKey(
            _source.Handle,
            HotkeyId,
            MOD_CONTROL | MOD_SHIFT,
            (uint)KeyInterop.VirtualKeyFromKey(Key.V));
    }

    public bool IsRegistered => _registered;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Triggered?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            if (_registered)
            {
                UnregisterHotKey(_source.Handle, HotkeyId);
                _registered = false;
            }
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
