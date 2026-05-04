using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clipmon.Services;

public sealed class ClipmonSettings
{
    public SensitiveFilterSettings SensitiveFilter { get; set; } = new();
    public SkipListSettings SkipList { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();
}

public sealed class SensitiveFilterSettings
{
    /// <summary>When true, items matching any sensitive pattern are NOT recorded.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Regex patterns that mark text as sensitive.</summary>
    public List<string> Patterns { get; set; } = new()
    {
        // Common API key prefixes
        @"\bsk-[A-Za-z0-9]{20,}\b",          // OpenAI / generic
        @"\bsk_live_[A-Za-z0-9]{20,}\b",     // Stripe live secret
        @"\bsk_test_[A-Za-z0-9]{20,}\b",     // Stripe test secret
        @"\bAKIA[0-9A-Z]{16}\b",             // AWS access key
        @"\bASIA[0-9A-Z]{16}\b",             // AWS temp access key
        @"\bgh[pousr]_[A-Za-z0-9]{20,}\b",   // GitHub PAT
        @"\bAIza[0-9A-Za-z_\-]{35}\b",       // Google API key
        @"\bxox[abprs]-[A-Za-z0-9\-]{10,}\b",// Slack token
        @"\bglpat-[A-Za-z0-9_\-]{20,}\b",    // GitLab PAT
        @"\bnpm_[A-Za-z0-9]{36}\b",          // npm token
        @"\b[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{20,}\b", // JWT
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----", // PEM private key
    };

    /// <summary>Skip clipboard text shorter than this many characters from the entropy check.</summary>
    public int MinLengthForEntropyCheck { get; set; } = 16;

    /// <summary>If text exceeds this length AND has high Shannon entropy, treat as sensitive (likely a token).</summary>
    public int LongHighEntropyThreshold { get; set; } = 32;
    public double EntropyBitsPerChar { get; set; } = 4.5;
}

public sealed class SkipListSettings
{
    /// <summary>Process names (without .exe) whose clipboard activity is ignored.</summary>
    public List<string> Apps { get; set; } = new()
    {
        "1password", "bitwarden", "keepass", "lastpass"
    };

    /// <summary>Substring keywords that, if present in clipboard text, cause the item to be skipped (case-insensitive).</summary>
    public List<string> Keywords { get; set; } = new();
}

public sealed class PrivacySettings
{
    public bool ClearHistoryOnQuit { get; set; }
    public bool AutoClearPasteboardEnabled { get; set; }

    /// <summary>How long to keep clipboard contents before wiping the OS pasteboard (0 = never).</summary>
    public int AutoClearAfterSeconds { get; set; } = 60;
}

public sealed class SyncSettings
{
    /// <summary>Master toggle for cross-computer sync.</summary>
    public bool Enabled { get; set; }

    /// <summary>WebSocket URL of the relay server. wss:// in production, ws:// for localhost testing.</summary>
    public string RelayUrl { get; set; } = "ws://localhost:8765";

    /// <summary>Shared 6+ char pairing code. Same on every device in the room. Used to derive AES key.</summary>
    public string PairingCode { get; set; } = string.Empty;

    /// <summary>Stable random ID for this device. Auto-generated on first run.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Friendly name shown to other devices ("Anthony's MacBook").</summary>
    public string DeviceName { get; set; } = Environment.MachineName;
}

public sealed class SettingsService
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ClipmonSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public SettingsService(string? overrideDirectory = null)
    {
        var directory = overrideDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipmon");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");

        Load();

        // Ensure DeviceId is always populated.
        if (string.IsNullOrWhiteSpace(Current.Sync.DeviceId))
        {
            Current.Sync.DeviceId = Guid.NewGuid().ToString("N");
            Save();
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                Current = new ClipmonSettings();
                return;
            }

            try
            {
                var json = File.ReadAllText(_path);
                Current = JsonSerializer.Deserialize<ClipmonSettings>(json, JsonOptions) ?? new ClipmonSettings();
            }
            catch
            {
                // Corrupt settings should not crash the app.
                Current = new ClipmonSettings();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(_path, json);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<ClipmonSettings> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
        }
        Save();
    }
}
