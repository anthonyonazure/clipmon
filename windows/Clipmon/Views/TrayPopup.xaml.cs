using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Clipmon.Models;
using Clipmon.ViewModels;

namespace Clipmon.Views;

public partial class TrayPopup : Window
{
    public event EventHandler? OpenWindowRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler? SettingsRequested;

    public TrayPopup()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private System.Windows.Point? _dragStart;

    private void OnClipsPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _dragStart = e.GetPosition(null);
        }
    }

    private void OnClipsMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStart is null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        var dx = Math.Abs(pos.X - _dragStart.Value.X);
        var dy = Math.Abs(pos.Y - _dragStart.Value.Y);
        if (dx < SystemParameters.MinimumHorizontalDragDistance && dy < SystemParameters.MinimumVerticalDragDistance) return;

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ClipboardEntry entry) return;

        try
        {
            var data = BuildDragData(entry);
            if (data is null) { _dragStart = null; return; }

            // Don't dismiss on lose-focus during a drag.
            DragDrop.DoDragDrop(item, data, DragDropEffects.Copy);
        }
        catch
        {
            // Drag can throw if another process steals focus mid-drop.
        }
        finally
        {
            _dragStart = null;
        }
    }

    private static DataObject? BuildDragData(ClipboardEntry entry)
    {
        var data = new DataObject();

        switch (entry.Kind)
        {
            case ClipboardContentKind.Image when entry.PayloadData is { Length: > 0 }:
            {
                var path = WriteTempFile($"{Sha12(entry.Fingerprint)}.png", entry.PayloadData);
                if (path is null) return null;
                var files = new StringCollection { path };
                data.SetFileDropList(files);
                return data;
            }

            case ClipboardContentKind.Audio:
            case ClipboardContentKind.File:
            {
                // Prefer existing file URL when available; otherwise materialize the cached bytes.
                var localPath = TryGetLocalPath(entry.FileUrl);
                if (localPath is null && entry.PayloadData is { Length: > 0 })
                {
                    localPath = WriteTempFile(entry.FileName ?? Sha12(entry.Fingerprint) + ".bin", entry.PayloadData);
                }
                if (localPath is null) return null;
                var files = new StringCollection { localPath };
                data.SetFileDropList(files);
                return data;
            }

            case ClipboardContentKind.RichText when entry.PayloadData is { Length: > 0 }:
            {
                data.SetData(DataFormats.Rtf, System.Text.Encoding.UTF8.GetString(entry.PayloadData));
                if (!string.IsNullOrEmpty(entry.TextContent)) data.SetText(entry.TextContent);
                return data;
            }

            default:
            {
                if (string.IsNullOrEmpty(entry.TextContent)) return null;
                data.SetText(entry.TextContent);
                return data;
            }
        }
    }

    private static string? TryGetLocalPath(string? fileUrl)
    {
        if (string.IsNullOrEmpty(fileUrl)) return null;
        try
        {
            var uri = new Uri(fileUrl);
            return uri.IsFile && File.Exists(uri.LocalPath) ? uri.LocalPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? WriteTempFile(string suggestedName, byte[] bytes)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Clipmon", "drag-cache");
            Directory.CreateDirectory(dir);
            var safe = string.Join("_", suggestedName.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(dir, safe);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string Sha12(string s) => s.Length <= 12 ? s : s[..12];

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    public void SetSyncStatus(string state)
    {
        SyncStatusText.Text = string.IsNullOrEmpty(state) ? string.Empty : $"sync · {state}";
        SyncStatusText.Visibility = string.IsNullOrEmpty(state) || state == "Disabled" || state == "Disconnected"
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>
    /// Show the popup anchored near the system tray (bottom-right of the working area for
    /// a typical Windows taskbar, but adjusts to taskbar position if the user moved it).
    /// </summary>
    public void ShowNearTray()
    {
        // Make sure layout is measured before we read ActualHeight.
        if (!IsLoaded)
        {
            // First time: position once we know the size.
            ContentRendered += FirstShowAnchor;
            Show();
        }
        else
        {
            AnchorToTrayCorner();
            Show();
        }

        Activate();
        SearchBox.Focus();
    }

    private void FirstShowAnchor(object? sender, EventArgs e)
    {
        ContentRendered -= FirstShowAnchor;
        AnchorToTrayCorner();
    }

    private void AnchorToTrayCorner()
    {
        var work = SystemParameters.WorkArea; // logical pixels, excludes taskbar
        const double margin = 6;

        // Default: bottom-right (taskbar at bottom or right)
        Left = work.Right - ActualWidth - margin;
        Top = work.Bottom - ActualHeight - margin;

        // If taskbar is at the top, drop down from the top-right.
        if (work.Top > 0 && work.Top >= margin)
        {
            Top = work.Top + margin;
        }

        // Keep on-screen for high-DPI / small displays.
        if (Left < work.Left) Left = work.Left + margin;
        if (Top < work.Top) Top = work.Top + margin;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Hide the popup when it loses focus (Mac menu-bar feel).
        Hide();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Number keys 1-9 copy the corresponding visible item.
        if (!SearchBox.IsKeyboardFocusWithin
            && e.Key >= Key.D1 && e.Key <= Key.D9
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            var index = e.Key - Key.D1;
            if (index < ClipsList.Items.Count
                && ClipsList.Items[index] is ClipboardEntry entry
                && ViewModel is not null)
            {
                ViewModel.CopyCommand.Execute(entry);
                Hide();
                e.Handled = true;
            }
            return;
        }

        // Delete removes the selected entry.
        if (e.Key == Key.Delete && ClipsList.SelectedItem is ClipboardEntry toDelete && ViewModel is not null)
        {
            ViewModel.DeleteCommand.Execute(toDelete);
            e.Handled = true;
            return;
        }

        // Ctrl+P toggles pin on the selected entry.
        if (e.Key == Key.P
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && ClipsList.SelectedItem is ClipboardEntry toPin
            && ViewModel is not null)
        {
            ViewModel.TogglePinCommand.Execute(toPin);
            e.Handled = true;
        }
    }

    private void OnOpenWindowClicked(object sender, RoutedEventArgs e)
    {
        OpenWindowRequested?.Invoke(this, EventArgs.Empty);
        Hide();
    }

    private void OnQuitClicked(object sender, RoutedEventArgs e)
    {
        QuitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
        Hide();
    }

    private void OnPinToggleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: ClipboardEntry entry } && ViewModel is not null)
        {
            ViewModel.TogglePinCommand.Execute(entry);
        }
        e.Handled = true;
    }

    private void OnClipDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ClipsList.SelectedItem is ClipboardEntry entry && ViewModel is not null)
        {
            ViewModel.CopyCommand.Execute(entry);
            Hide();
        }
    }

    private void OnClipsListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ClipsList.SelectedItem is ClipboardEntry entry && ViewModel is not null)
        {
            ViewModel.CopyCommand.Execute(entry);
            Hide();
            e.Handled = true;
        }
    }

    // -------- Hide window from Alt+Tab (tool window style) --------
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
