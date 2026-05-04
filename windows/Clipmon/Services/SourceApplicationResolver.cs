using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Clipmon.Services;

public static class SourceApplicationResolver
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static string? Resolve()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            using var process = Process.GetProcessById((int)pid);

            // MainModule may throw for system processes — fall back to process name
            try
            {
                return process.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } description
                    ? description
                    : process.ProcessName;
            }
            catch
            {
                return process.ProcessName;
            }
        }
        catch
        {
            return null;
        }
    }
}
