using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Clipmon.Services;

/// <summary>
/// Hidden message-only window that subscribes to native WM_CLIPBOARDUPDATE
/// notifications via AddClipboardFormatListener. Far cheaper than polling.
/// </summary>
public sealed class ClipboardWindowMessageSink : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string? lpWindowName,
        int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    private readonly HwndSource _source;
    private bool _disposed;

    public event EventHandler? ClipboardChanged;

    public ClipboardWindowMessageSink()
    {
        var parameters = new HwndSourceParameters("ClipmonClipboardSink")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            ParentWindow = HWND_MESSAGE
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        if (!AddClipboardFormatListener(_source.Handle))
        {
            throw new InvalidOperationException(
                $"AddClipboardFormatListener failed (Win32 error {Marshal.GetLastWin32Error()})");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            RemoveClipboardFormatListener(_source.Handle);
        }
        catch
        {
            // ignore — we're tearing down anyway
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
