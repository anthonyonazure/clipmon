using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Clipmon.Models;

namespace Clipmon.Services;

public sealed record SyncPeer(string DeviceId, string DeviceName);

/// <summary>
/// E2E-encrypted sync client.
///
/// Lifecycle:
///   - Started lazily once the user enables sync + provides a pairing code.
///   - Owns one ClientWebSocket; reconnects with backoff on drop.
///   - Subscribes to ClipboardMonitor.EntryCaptured and broadcasts encrypted envelopes.
///   - Decrypts incoming envelopes and raises <see cref="EntryReceived"/> for the host to persist.
///
/// What it never does:
///   - Send the pairing code to the server. The server sees only SHA-256(pairingCode + salt).
///   - Trust server-side echo suppression. Each device tracks its own deviceId in the envelope and discards self-originated messages.
/// </summary>
public sealed class SyncClient : IDisposable
{
    private const int BackfillCount = 20;
    private const int MaxPayloadBytes = 2 * 1024 * 1024; // 2 MB raw (~ 2.7 MB base64, well under relay's 4 MB cap)

    private readonly SettingsService _settings;
    private readonly ClipboardMonitor _monitor;
    private readonly Func<int, IReadOnlyList<ClipboardEntry>>? _backfillSource;

    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private byte[]? _key;
    private string? _roomId;
    private string _connectionState = "Disconnected";

    /// <summary>Suppress relaying entries that we just received from the network.</summary>
    private readonly ConcurrentDictionary<string, byte> _recentlyReceivedFingerprints = new();

    public event EventHandler<SyncEnvelope>? EntryReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<IReadOnlyList<SyncPeer>>? PeersChanged;

    public string ConnectionState => _connectionState;
    public IReadOnlyList<SyncPeer> Peers => _peers.ToArray();
    private readonly List<SyncPeer> _peers = new();

    public SyncClient(SettingsService settings, ClipboardMonitor monitor, Func<int, IReadOnlyList<ClipboardEntry>>? backfillSource = null)
    {
        _settings = settings;
        _monitor = monitor;
        _backfillSource = backfillSource;
        _monitor.EntryCaptured += OnLocalEntry;
        _settings.Changed += OnSettingsChanged;
    }

    public void Start()
    {
        var sync = _settings.Current.Sync;
        if (!sync.Enabled || string.IsNullOrWhiteSpace(sync.PairingCode) || string.IsNullOrWhiteSpace(sync.RelayUrl))
        {
            UpdateState("Disabled");
            return;
        }

        if (_runLoop is not null && !_runLoop.IsCompleted)
        {
            return; // already running
        }

        try
        {
            _key = SyncProtocol.DeriveKey(sync.PairingCode);
            _roomId = SyncProtocol.DeriveRoomId(sync.PairingCode);
        }
        catch (Exception ex)
        {
            UpdateState($"Bad pairing code: {ex.Message}");
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _runLoop = Task.Run(() => RunAsync(token), token);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch { /* ignore */ }

        _runLoop = null;
        _key = null;
        _roomId = null;
        UpdateState("Disconnected");
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // If sync was toggled off, stop. If toggled on or pairing changed, restart.
        Stop();
        Start();
    }

    private async Task RunAsync(CancellationToken token)
    {
        var attempt = 0;

        while (!token.IsCancellationRequested)
        {
            attempt++;
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                var url = new Uri(_settings.Current.Sync.RelayUrl);
                UpdateState($"Connecting to {url.Host}...");
                await ws.ConnectAsync(url, token).ConfigureAwait(false);
                UpdateState("Connected");
                attempt = 0;

                await SendJoinAsync(ws, token).ConfigureAwait(false);
                await ReceiveLoopAsync(ws, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                UpdateState($"Disconnected ({ex.GetType().Name})");
            }

            if (token.IsCancellationRequested) break;

            var delaySec = Math.Min(60, (int)Math.Pow(2, Math.Min(6, attempt)));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySec), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        UpdateState("Stopped");
    }

    private ClientWebSocket? _activeSocket;

    private async Task SendJoinAsync(ClientWebSocket ws, CancellationToken token)
    {
        _activeSocket = ws;
        var sync = _settings.Current.Sync;
        var join = JsonSerializer.Serialize(new
        {
            type = "join",
            room = _roomId,
            deviceId = sync.DeviceId,
            deviceName = string.IsNullOrWhiteSpace(sync.DeviceName) ? Environment.MachineName : sync.DeviceName,
        }, SyncProtocol.JsonOptions);

        await ws.SendAsync(Encoding.UTF8.GetBytes(join), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();

        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (ms.Length == 0) continue;

            var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            HandleServerMessage(json);
        }
    }

    private void HandleServerMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            switch (type)
            {
                case "joined":
                    UpdateState("Synced");
                    _peers.Clear();
                    if (doc.RootElement.TryGetProperty("peers", out var peersProp) && peersProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in peersProp.EnumerateArray())
                        {
                            var id = p.TryGetProperty("deviceId", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                            var name = p.TryGetProperty("deviceName", out var nameProp) ? nameProp.GetString() ?? "Device" : "Device";
                            if (!string.IsNullOrEmpty(id))
                            {
                                _peers.Add(new SyncPeer(id, name));
                            }
                        }
                    }
                    PeersChanged?.Invoke(this, Peers);
                    break;
                case "peer-joined":
                    {
                        var name = doc.RootElement.TryGetProperty("deviceName", out var n) ? n.GetString() : "Device";
                        var id = doc.RootElement.TryGetProperty("deviceId", out var i) ? i.GetString() : null;
                        UpdateState($"Peer joined: {name}");
                        if (!string.IsNullOrEmpty(id))
                        {
                            if (!_peers.Any(p => p.DeviceId == id))
                            {
                                _peers.Add(new SyncPeer(id, name ?? "Device"));
                            }
                            PeersChanged?.Invoke(this, Peers);
                            SendBackfill(id);
                        }
                    }
                    break;
                case "peer-left":
                    {
                        var id = doc.RootElement.TryGetProperty("deviceId", out var i) ? i.GetString() : null;
                        if (!string.IsNullOrEmpty(id))
                        {
                            _peers.RemoveAll(p => p.DeviceId == id);
                            PeersChanged?.Invoke(this, Peers);
                        }
                        UpdateState("Peer left");
                    }
                    break;
                case "clip":
                    HandleClip(doc.RootElement);
                    break;
                case "error":
                    UpdateState($"Server error: {doc.RootElement.GetProperty("code").GetString()}");
                    break;
            }
        }
        catch
        {
            // Ignore malformed messages.
        }
    }

    private void HandleClip(JsonElement root)
    {
        if (_key is null) return;
        if (!root.TryGetProperty("envelope", out var envProp)) return;

        try
        {
            var json = SyncProtocol.DecryptEnvelope(_key, envProp.GetString() ?? string.Empty);
            var envelope = JsonSerializer.Deserialize<SyncEnvelope>(json, SyncProtocol.JsonOptions);
            if (envelope is null) return;

            // Self-echo guard: ignore items we ourselves broadcast.
            if (envelope.FromDeviceId == _settings.Current.Sync.DeviceId) return;

            // Mark for outbound suppression so the resulting clipboard write doesn't loop back.
            _recentlyReceivedFingerprints.TryAdd(envelope.Fingerprint, 1);
            EntryReceived?.Invoke(this, envelope);
        }
        catch (Exception ex)
        {
            UpdateState($"Decrypt failed: {ex.Message}");
        }
    }

    private void SendBackfill(string targetDeviceId)
    {
        if (string.IsNullOrEmpty(targetDeviceId)) return;
        if (_backfillSource is null) return;
        if (_activeSocket is not { State: WebSocketState.Open } ws) return;
        if (_key is null || _roomId is null) return;

        IReadOnlyList<ClipboardEntry> entries;
        try
        {
            entries = _backfillSource(BackfillCount);
        }
        catch
        {
            return;
        }

        var sync = _settings.Current.Sync;
        foreach (var entry in entries)
        {
            try
            {
                var envelope = BuildEnvelope(entry, sync);
                var plaintext = JsonSerializer.Serialize(envelope, SyncProtocol.JsonOptions);
                var encrypted = SyncProtocol.EncryptEnvelope(_key, plaintext);
                var msg = JsonSerializer.Serialize(new
                {
                    type = "clip",
                    room = _roomId,
                    envelope = encrypted,
                    targetDeviceId,
                    backfill = true,
                }, SyncProtocol.JsonOptions);
                _ = ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                // Skip individual entries that fail to serialize.
            }
        }
    }

    private SyncEnvelope BuildEnvelope(ClipboardEntry entry, SyncSettings sync)
    {
        // Lazy hydrate: if the entry references a local file we haven't yet loaded, try to read it.
        var bytes = entry.PayloadData;
        if (bytes is null && !string.IsNullOrEmpty(entry.FileUrl))
        {
            bytes = TryReadCappedFile(entry.FileUrl);
        }

        if (bytes is { Length: > MaxPayloadBytes })
        {
            UpdateState($"Skipping {entry.FileName ?? entry.Kind.ToString()} ({bytes.Length / 1024 / 1024} MB) — too large to sync");
            bytes = null;
        }

        return new SyncEnvelope
        {
            Fingerprint = entry.Fingerprint,
            Kind = SyncProtocol.ToWireKind(entry.Kind),
            TextContent = entry.TextContent,
            FileName = entry.FileName,
            FileUrl = null, // file:// URLs are device-local; the receiver gets the bytes instead
            PayloadDataBase64 = bytes is null ? null : Convert.ToBase64String(bytes),
            UtiIdentifier = entry.UtiIdentifier,
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.UpdatedAt,
            IsPinned = entry.IsPinned,
            SourceApplication = entry.SourceApplication,
            FromDeviceId = sync.DeviceId,
            FromDeviceName = string.IsNullOrWhiteSpace(sync.DeviceName) ? Environment.MachineName : sync.DeviceName,
        };
    }

    private static byte[]? TryReadCappedFile(string fileUrl)
    {
        try
        {
            var uri = new Uri(fileUrl);
            if (!uri.IsFile) return null;
            var info = new System.IO.FileInfo(uri.LocalPath);
            if (!info.Exists) return null;
            if (info.Length > MaxPayloadBytes) return null;
            return System.IO.File.ReadAllBytes(uri.LocalPath);
        }
        catch
        {
            return null;
        }
    }

    private void OnLocalEntry(object? sender, ClipboardEntry entry)
    {
        if (_activeSocket is not { State: WebSocketState.Open } ws) return;
        if (_key is null || _roomId is null) return;

        // Don't ping-pong remote-originated items back across the wire.
        // Use the source-application marker as the primary signal — fingerprints differ
        // across platforms (PascalCase vs lowercase kind seeds), so the
        // `_recentlyReceivedFingerprints` set is only a best-effort secondary guard.
        if (entry.SourceApplication?.StartsWith("sync · ", StringComparison.Ordinal) == true) return;
        if (_recentlyReceivedFingerprints.TryRemove(entry.Fingerprint, out _)) return;

        var sync = _settings.Current.Sync;
        var envelope = BuildEnvelope(entry, sync);

        try
        {
            var plaintext = JsonSerializer.Serialize(envelope, SyncProtocol.JsonOptions);
            var encrypted = SyncProtocol.EncryptEnvelope(_key, plaintext);
            var msg = JsonSerializer.Serialize(new { type = "clip", room = _roomId, envelope = encrypted }, SyncProtocol.JsonOptions);

            // Fire and forget: WebSocket sends are inherently async but losing one clip is not fatal.
            _ = ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            UpdateState($"Send failed: {ex.Message}");
        }
    }

    private void UpdateState(string state)
    {
        _connectionState = state;
        StatusChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        Stop();
        _monitor.EntryCaptured -= OnLocalEntry;
        _settings.Changed -= OnSettingsChanged;
    }
}
