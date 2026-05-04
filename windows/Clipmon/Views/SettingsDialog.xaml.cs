using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using Clipmon.Services;

namespace Clipmon.Views;

public partial class SettingsDialog : Window
{
    private const string PairingCharset = "abcdefghjkmnpqrstuvwxyz23456789";
    private const int PairingLength = 10;

    private readonly SettingsService _settings;
    private readonly SyncClient _sync;

    public SettingsDialog(SettingsService settings, SyncClient sync)
    {
        InitializeComponent();
        _settings = settings;
        _sync = sync;

        LoadFromSettings();
        UpdatePeers(_sync.Peers);

        _sync.StatusChanged += OnSyncStatusChanged;
        _sync.PeersChanged += OnPeersChanged;
        StatusText.Text = _sync.ConnectionState;

        Closed += (_, _) =>
        {
            _sync.StatusChanged -= OnSyncStatusChanged;
            _sync.PeersChanged -= OnPeersChanged;
        };
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;

        // Sync
        EnabledCheckbox.IsChecked = s.Sync.Enabled;
        PairingCodeBox.Text = s.Sync.PairingCode;
        RelayUrlBox.Text = s.Sync.RelayUrl;
        DeviceNameBox.Text = s.Sync.DeviceName;

        // Privacy
        ClearOnQuitCheckbox.IsChecked = s.Privacy.ClearHistoryOnQuit;
        AutoClearCheckbox.IsChecked = s.Privacy.AutoClearPasteboardEnabled;
        AutoClearSecondsBox.Text = s.Privacy.AutoClearAfterSeconds.ToString(CultureInfo.InvariantCulture);

        // Filters
        SensitiveFilterCheckbox.IsChecked = s.SensitiveFilter.Enabled;
        SkipAppsBox.Text = string.Join(Environment.NewLine, s.SkipList.Apps);
        SkipKeywordsBox.Text = string.Join(Environment.NewLine, s.SkipList.Keywords);
        SensitivePatternsBox.Text = string.Join(Environment.NewLine, s.SensitiveFilter.Patterns);

        UpdateFirstRunBanner();
    }

    private void OnSyncStatusChanged(object? sender, string state)
    {
        Dispatcher.Invoke(() => StatusText.Text = state);
    }

    private void OnPeersChanged(object? sender, IReadOnlyList<SyncPeer> peers)
    {
        Dispatcher.Invoke(() => UpdatePeers(peers));
    }

    private void UpdatePeers(IReadOnlyList<SyncPeer> peers)
    {
        PeersList.ItemsSource = peers;
        NoPeersText.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEnabledChanged(object sender, RoutedEventArgs e) => UpdateFirstRunBanner();

    private void UpdateFirstRunBanner()
    {
        var enabled = EnabledCheckbox.IsChecked == true;
        var hasCode = !string.IsNullOrWhiteSpace(PairingCodeBox.Text);
        FirstRunBanner.Visibility = (enabled && !hasCode) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnGenerateCodeClicked(object sender, RoutedEventArgs e)
    {
        PairingCodeBox.Text = GeneratePairingCode();
        UpdateFirstRunBanner();
    }

    private void OnCopyCodeClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PairingCodeBox.Text)) return;
        try
        {
            Clipboard.SetText(PairingCodeBox.Text.Trim());
            StatusText.Text = "Pairing code copied";
        }
        catch
        {
            // ignore — clipboard can be locked by another process
        }
    }

    private static string GeneratePairingCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(PairingLength);
        var chars = new char[PairingLength];
        for (var i = 0; i < PairingLength; i++)
        {
            chars[i] = PairingCharset[bytes[i] % PairingCharset.Length];
        }
        return new string(chars);
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var seconds = 60;
        int.TryParse(AutoClearSecondsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
        if (seconds < 5) seconds = 5;
        if (seconds > 86400) seconds = 86400;

        _settings.Update(s =>
        {
            // Sync
            s.Sync.Enabled = EnabledCheckbox.IsChecked == true;
            s.Sync.PairingCode = PairingCodeBox.Text.Trim();
            s.Sync.RelayUrl = RelayUrlBox.Text.Trim();
            s.Sync.DeviceName = string.IsNullOrWhiteSpace(DeviceNameBox.Text)
                ? Environment.MachineName
                : DeviceNameBox.Text.Trim();

            // Privacy
            s.Privacy.ClearHistoryOnQuit = ClearOnQuitCheckbox.IsChecked == true;
            s.Privacy.AutoClearPasteboardEnabled = AutoClearCheckbox.IsChecked == true;
            s.Privacy.AutoClearAfterSeconds = seconds;

            // Filters
            s.SensitiveFilter.Enabled = SensitiveFilterCheckbox.IsChecked == true;
            s.SkipList.Apps = SplitLines(SkipAppsBox.Text);
            s.SkipList.Keywords = SplitLines(SkipKeywordsBox.Text);
            s.SensitiveFilter.Patterns = SplitLines(SensitivePatternsBox.Text);
        });

        if (_settings.Current.Sync.Enabled && string.IsNullOrEmpty(_settings.Current.Sync.PairingCode))
        {
            MessageBox.Show(
                this,
                "Sync is enabled but no pairing code is set. Generate one and use the same code on every device you want to share with.",
                "Pairing code required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static List<string> SplitLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }
}
