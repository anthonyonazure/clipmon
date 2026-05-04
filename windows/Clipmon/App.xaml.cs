using System.Windows;
using Clipmon.Models;
using Clipmon.Services;
using Clipmon.ViewModels;
using Clipmon.Views;

namespace Clipmon;

public partial class App : Application
{
    private SettingsService? _settings;
    private EncryptionService? _crypto;
    private SensitiveContentFilter? _filter;
    private ClipboardDatabase? _database;
    private ClipboardMonitor? _monitor;
    private SyncClient? _sync;
    private GlobalHotkeyService? _hotkey;
    private TrayIconService? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private TrayPopup? _trayPopup;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.ToString(),
                "Clipmon — unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        _settings = new SettingsService();
        _crypto = new EncryptionService();
        _filter = new SensitiveContentFilter(_settings);
        _database = new ClipboardDatabase(_crypto);
        _monitor = new ClipboardMonitor(_database, _filter, _settings);
        _viewModel = new MainViewModel(_database, _monitor);

        _sync = new SyncClient(_settings, _monitor, limit => _database!.GetRecent(limit));
        _sync.EntryReceived += OnRemoteEntryReceived;
        _sync.Start();

        _tray = new TrayIconService();
        _tray.ShowRequested += (_, _) => ShowTrayPopup();
        _tray.CaptureNowRequested += (_, _) => _viewModel.CaptureNowCommand.Execute(null);
        _tray.PauseResumeRequested += (_, _) =>
        {
            _viewModel.TogglePauseResumeCommand.Execute(null);
            _tray.UpdateMonitoringState(_viewModel.IsMonitoring);
        };
        _tray.QuitRequested += (_, _) => Shutdown();

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsMonitoring))
            {
                _tray?.UpdateMonitoringState(_viewModel.IsMonitoring);
            }
        };

        _mainWindow = new MainWindow { DataContext = _viewModel };

        _trayPopup = new TrayPopup { DataContext = _viewModel };
        _trayPopup.OpenWindowRequested += (_, _) => ShowMainWindow();
        _trayPopup.QuitRequested += (_, _) => Shutdown();
        _trayPopup.SettingsRequested += (_, _) => ShowSettingsDialog();

        _sync.StatusChanged += (_, state) =>
        {
            Dispatcher.Invoke(() => _trayPopup?.SetSyncStatus(state));
        };
        _trayPopup.SetSyncStatus(_sync.ConnectionState);

        _hotkey = new GlobalHotkeyService();
        _hotkey.Triggered += (_, _) => Dispatcher.Invoke(ShowTrayPopup);

        // Mac-style: app launches into the tray. The user opens the popup or the
        // main window explicitly. (If you'd rather have the main window auto-open
        // on launch, uncomment the line below.)
        // ShowMainWindow();
    }

    private void ShowTrayPopup()
    {
        if (_trayPopup is null) return;

        if (_trayPopup.IsVisible)
        {
            _trayPopup.Hide();
            return;
        }

        _trayPopup.ShowNearTray();
    }

    private void ShowSettingsDialog()
    {
        if (_settings is null || _sync is null) return;
        var dialog = new SettingsDialog(_settings, _sync)
        {
            Owner = _mainWindow?.IsVisible == true ? _mainWindow : null,
        };
        dialog.ShowDialog();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private void OnRemoteEntryReceived(object? sender, SyncEnvelope envelope)
    {
        if (_monitor is null) return;

        var kind = SyncProtocol.FromWireKind(envelope.Kind);
        byte[]? payload = null;
        if (!string.IsNullOrEmpty(envelope.PayloadDataBase64))
        {
            try
            {
                payload = Convert.FromBase64String(envelope.PayloadDataBase64);
            }
            catch
            {
                payload = null;
            }
        }

        // For audio/file kinds, materialize the bytes back to a temp file so the entry
        // behaves like a local one (drag-out, copy-as-file work).
        string? localFileUrl = null;
        if (payload is { Length: > 0 } && !string.IsNullOrEmpty(envelope.FileName)
            && (kind == ClipboardContentKind.Audio || kind == ClipboardContentKind.File))
        {
            try
            {
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Clipmon", "sync-cache");
                System.IO.Directory.CreateDirectory(dir);
                var safeName = string.Join("_", envelope.FileName.Split(System.IO.Path.GetInvalidFileNameChars()));
                var path = System.IO.Path.Combine(dir, $"{envelope.Fingerprint[..Math.Min(envelope.Fingerprint.Length, 12)]}-{safeName}");
                System.IO.File.WriteAllBytes(path, payload);
                localFileUrl = new Uri(path).AbsoluteUri;
            }
            catch
            {
                localFileUrl = null;
            }
        }

        var capture = new ClipboardCapturePayload(
            Kind: kind,
            TextContent: envelope.TextContent,
            FileName: envelope.FileName,
            FileUrl: localFileUrl,
            PayloadData: payload,
            UtiIdentifier: envelope.UtiIdentifier,
            SourceApplication: $"sync · {envelope.FromDeviceName}");

        Dispatcher.Invoke(() => _monitor.IngestRemote(capture));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_settings?.Current.Privacy.ClearHistoryOnQuit == true)
        {
            try { _database?.Clear(keepPinned: true); } catch { /* best effort */ }
        }

        _hotkey?.Dispose();
        _sync?.Dispose();
        _tray?.Dispose();
        _monitor?.Dispose();
        _database?.Dispose();
        base.OnExit(e);
    }
}
