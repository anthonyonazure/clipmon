using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Clipmon.ViewModels;

namespace Clipmon.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => SyncScopeButtons();

    private void OnLoaded(object sender, RoutedEventArgs e)
        => SyncScopeButtons();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Mica is intentionally disabled — without WindowChrome's ExtendsContentIntoTitleBar
        // the transparent fallback renders black. The gradient background looks better.
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            if (ViewModel is not null)
            {
                ViewModel.SearchText = string.Empty;
            }
            EntryList.Focus();
            e.Handled = true;
        }
    }

    private void SyncScopeButtons()
    {
        if (ViewModel is null) return;
        ScopeAll.IsChecked = ViewModel.Scope == EntryScope.All;
        ScopePinned.IsChecked = ViewModel.Scope == EntryScope.Pinned;
    }

    private void OnScopeAllClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.Scope = EntryScope.All;
    }

    private void OnScopePinnedClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.Scope = EntryScope.Pinned;
    }

    private void OnClearSearchClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SearchText = string.Empty;
        SearchBox.Focus();
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        var result = MessageBox.Show(
            owner: this,
            messageBoxText: "Clear clipboard history?\n\nYes — keep pinned items\nNo — clear everything\nCancel — keep history",
            caption: "Clipmon",
            button: MessageBoxButton.YesNoCancel,
            icon: MessageBoxImage.Question,
            defaultResult: MessageBoxResult.Yes);

        switch (result)
        {
            case MessageBoxResult.Yes:
                ViewModel.ClearKeepingPinnedCommand.Execute(null);
                break;
            case MessageBoxResult.No:
                ViewModel.ClearEverythingCommand.Execute(null);
                break;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (ViewModel is null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        ViewModel.ImportFiles(paths);
        e.Handled = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Hide instead of closing — quitting happens via tray menu.
        e.Cancel = true;
        Hide();
    }

    // -------------------- Mica backdrop (Windows 11 22H2+) --------------------

    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMSBT_MAINWINDOW = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void TryEnableMicaBackdrop()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return; // Mica needs Windows 11 22H2+
        }

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var backdrop = DWMSBT_MAINWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            // Make the window background fully transparent so Mica shows through.
            // Our cards already have their own opaque surfaces.
            Background = Brushes.Transparent;
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            {
                target.BackgroundColor = System.Windows.Media.Colors.Transparent;
            }
        }
        catch
        {
            // If anything fails, fall back to the gradient background.
        }
    }
}
