using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Clipmon.Models;

namespace Clipmon.Services;

public sealed class ClipboardMonitor : IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "png", "jpg", "jpeg", "heic", "gif", "tiff", "tif", "bmp", "webp" };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "m4a", "aac", "wav", "flac", "aiff", "ogg" };
    private static readonly HashSet<string> SpreadsheetExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "xls", "xlsx", "csv", "tsv", "numbers", "ods" };
    private static readonly HashSet<string> RichTextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "rtf", "rtfd" };
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "md", "markdown", "txt" };

    private readonly ClipboardDatabase _database;
    private readonly ClipboardWindowMessageSink _sink;
    private readonly SensitiveContentFilter? _filter;
    private readonly SettingsService? _settingsService;
    private System.Windows.Threading.DispatcherTimer? _autoClearTimer;

    private bool _isMonitoring;
    private string? _suppressFingerprint;

    public event EventHandler<ClipboardEntry>? EntryCaptured;
    public event EventHandler<string>? StatusMessage;

    public bool IsMonitoring => _isMonitoring;

    public ClipboardMonitor(ClipboardDatabase database, SensitiveContentFilter? filter = null, SettingsService? settings = null)
    {
        _database = database;
        _filter = filter;
        _settingsService = settings;
        _sink = new ClipboardWindowMessageSink();
        _sink.ClipboardChanged += OnClipboardChanged;
    }

    private void RestartAutoClearTimer()
    {
        var s = _settingsService?.Current.Privacy;
        if (s is null) return;

        _autoClearTimer?.Stop();
        _autoClearTimer = null;

        if (!s.AutoClearPasteboardEnabled || s.AutoClearAfterSeconds <= 0) return;

        _autoClearTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(s.AutoClearAfterSeconds)
        };
        _autoClearTimer.Tick += (_, _) =>
        {
            _autoClearTimer?.Stop();
            try { Clipboard.Clear(); } catch { /* COMException possible if another process has clipboard locked */ }
            StatusMessage?.Invoke(this, "Auto-cleared OS clipboard");
        };
        _autoClearTimer.Start();
    }

    public void Start()
    {
        if (_isMonitoring) return;
        _isMonitoring = true;
        StatusMessage?.Invoke(this, "Watching clipboard changes");
    }

    public void Stop()
    {
        if (!_isMonitoring) return;
        _isMonitoring = false;
        StatusMessage?.Invoke(this, "Clipboard monitoring paused");
    }

    /// <summary>Force-capture the current clipboard contents regardless of monitoring state.</summary>
    public void CaptureNow() => CaptureCurrent(force: true);

    public void ImportFiles(IEnumerable<string> paths)
    {
        var imported = 0;
        var source = SourceApplicationResolver.Resolve();

        foreach (var path in paths)
        {
            try
            {
                var payload = BuildPayloadFromFile(path, source);
                Persist(payload);
                imported++;
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke(this, $"Skipped {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (imported > 0)
        {
            StatusMessage?.Invoke(this, $"Imported {imported} file(s) into history");
        }
    }

    /// <summary>
    /// Suppress the next captured fingerprint so that when the app writes
    /// content back to the clipboard, it doesn't get re-saved.
    /// </summary>
    public void SuppressNextFingerprint(string fingerprint) => _suppressFingerprint = fingerprint;

    /// <summary>
    /// Persist a clipboard payload that originated on another device (received via sync).
    /// Bypasses the local clipboard read but still flows through Persist so the UI updates.
    /// </summary>
    public void IngestRemote(ClipboardCapturePayload payload)
    {
        try
        {
            Persist(payload);
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"Failed to ingest remote clip: {ex.Message}");
        }
    }

    public void CopyToClipboard(ClipboardEntry entry)
    {
        SuppressNextFingerprint(entry.Fingerprint);

        try
        {
            switch (entry.Kind)
            {
                case ClipboardContentKind.Text:
                case ClipboardContentKind.Markdown:
                case ClipboardContentKind.Spreadsheet:
                    Clipboard.SetText(entry.TextContent ?? string.Empty);
                    break;

                case ClipboardContentKind.RichText:
                    var data = new DataObject();
                    if (entry.PayloadData is { Length: > 0 })
                    {
                        data.SetData(DataFormats.Rtf, System.Text.Encoding.UTF8.GetString(entry.PayloadData));
                    }
                    if (!string.IsNullOrEmpty(entry.TextContent))
                    {
                        data.SetText(entry.TextContent);
                    }
                    Clipboard.SetDataObject(data, copy: true);
                    break;

                case ClipboardContentKind.Image:
                    if (entry.PayloadData is { Length: > 0 })
                    {
                        var image = DecodePng(entry.PayloadData);
                        if (image is not null)
                        {
                            Clipboard.SetImage(image);
                        }
                    }
                    else if (!string.IsNullOrEmpty(entry.FileUrl))
                    {
                        SetFileDropFromUrl(entry.FileUrl);
                    }
                    break;

                case ClipboardContentKind.Audio:
                case ClipboardContentKind.File:
                    if (!string.IsNullOrEmpty(entry.FileUrl))
                    {
                        SetFileDropFromUrl(entry.FileUrl);
                    }
                    break;
            }

            entry.UpdatedAt = DateTime.UtcNow;
            _database.Upsert(entry);
            StatusMessage?.Invoke(this, $"Copied {entry.Kind.DisplayName().ToLowerInvariant()} back to clipboard");
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"Could not copy: {ex.Message}");
        }
    }

    private static BitmapSource? DecodePng(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            return decoder.Frames.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static void SetFileDropFromUrl(string fileUrl)
    {
        try
        {
            var uri = new Uri(fileUrl);
            if (!uri.IsFile) return;
            var collection = new System.Collections.Specialized.StringCollection { uri.LocalPath };
            Clipboard.SetFileDropList(collection);
        }
        catch
        {
            // ignore
        }
    }

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        if (!_isMonitoring) return;
        CaptureCurrent(force: false);
    }

    private void CaptureCurrent(bool force)
    {
        if (!force && !_isMonitoring) return;

        try
        {
            var payload = ReadCurrentClipboard();
            if (payload is null)
            {
                StatusMessage?.Invoke(this, "Clipboard changed, but nothing supported was found");
                return;
            }

            if (_suppressFingerprint is not null && _suppressFingerprint == payload.Fingerprint)
            {
                _suppressFingerprint = null;
                return;
            }

            if (_filter is not null && _filter.ShouldSkip(payload.TextContent, payload.SourceApplication, out var reason))
            {
                StatusMessage?.Invoke(this, reason ?? "Skipped sensitive item");
                return;
            }

            Persist(payload);
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"Capture failed: {ex.Message}");
        }
    }

    private void Persist(ClipboardCapturePayload payload)
    {
        var existing = _database.FindByFingerprint(payload.Fingerprint);
        ClipboardEntry entry;

        if (existing is not null)
        {
            existing.Kind = payload.Kind;
            existing.TextContent = payload.TextContent;
            existing.FileName = payload.FileName;
            existing.FileUrl = payload.FileUrl;
            existing.PayloadData = payload.PayloadData;
            existing.UtiIdentifier = payload.UtiIdentifier;
            existing.SourceApplication = payload.SourceApplication;
            existing.UpdatedAt = DateTime.UtcNow;
            entry = existing;
        }
        else
        {
            entry = new ClipboardEntry
            {
                Fingerprint = payload.Fingerprint,
                Kind = payload.Kind,
                TextContent = payload.TextContent,
                FileName = payload.FileName,
                FileUrl = payload.FileUrl,
                PayloadData = payload.PayloadData,
                UtiIdentifier = payload.UtiIdentifier,
                SourceApplication = payload.SourceApplication,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        _database.Upsert(entry);
        StatusMessage?.Invoke(this, $"Captured {entry.Kind.DisplayName().ToLowerInvariant()} item");
        EntryCaptured?.Invoke(this, entry);
        RestartAutoClearTimer();
    }

    private static ClipboardCapturePayload? ReadCurrentClipboard()
    {
        // Clipboard reads can fail with COMException if another process is
        // holding the clipboard open. Retry briefly before giving up.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return ReadCurrentClipboardOnce();
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 2)
            {
                Thread.Sleep(40);
            }
        }
        return null;
    }

    private static ClipboardCapturePayload? ReadCurrentClipboardOnce()
    {
        var source = SourceApplicationResolver.Resolve();

        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            if (files.Count > 0)
            {
                var path = files[0];
                if (!string.IsNullOrEmpty(path))
                {
                    return BuildPayloadFromFile(path, source);
                }
            }
        }

        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image is not null)
            {
                var bytes = EncodeAsPng(image);
                return new ClipboardCapturePayload(
                    ClipboardContentKind.Image,
                    TextContent: null,
                    FileName: null,
                    FileUrl: null,
                    PayloadData: bytes,
                    UtiIdentifier: "Bitmap",
                    SourceApplication: source);
            }
        }

        var rtf = TryGetData(DataFormats.Rtf) as string;
        if (!string.IsNullOrEmpty(rtf))
        {
            var plain = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            return new ClipboardCapturePayload(
                ClipboardContentKind.RichText,
                TextContent: plain,
                FileName: null,
                FileUrl: null,
                PayloadData: System.Text.Encoding.UTF8.GetBytes(rtf),
                UtiIdentifier: DataFormats.Rtf,
                SourceApplication: source);
        }

        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text)) return null;

            var kind = LooksLikeColor(text)
                ? ClipboardContentKind.Color
                : LooksLikeSpreadsheet(text)
                    ? ClipboardContentKind.Spreadsheet
                    : LooksLikeMarkdown(text)
                        ? ClipboardContentKind.Markdown
                        : ClipboardContentKind.Text;

            return new ClipboardCapturePayload(
                kind,
                TextContent: text,
                FileName: null,
                FileUrl: null,
                PayloadData: null,
                UtiIdentifier: DataFormats.UnicodeText,
                SourceApplication: source);
        }

        return null;
    }

    private static ClipboardCapturePayload BuildPayloadFromFile(string path, string? source)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        var kind = ClassifyFile(extension);
        byte[]? payload = null;

        if (kind == ClipboardContentKind.Image && File.Exists(path))
        {
            try
            {
                payload = File.ReadAllBytes(path);
            }
            catch
            {
                payload = null;
            }
        }

        string? textContent = null;
        if (kind == ClipboardContentKind.Spreadsheet && File.Exists(path))
        {
            try
            {
                textContent = File.ReadAllText(path);
            }
            catch
            {
                textContent = null;
            }
        }

        var uri = new Uri(path);
        return new ClipboardCapturePayload(
            kind,
            TextContent: textContent,
            FileName: Path.GetFileName(path),
            FileUrl: uri.AbsoluteUri,
            PayloadData: payload,
            UtiIdentifier: extension,
            SourceApplication: source);
    }

    private static ClipboardContentKind ClassifyFile(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return ClipboardContentKind.File;
        if (ImageExtensions.Contains(extension)) return ClipboardContentKind.Image;
        if (AudioExtensions.Contains(extension)) return ClipboardContentKind.Audio;
        if (SpreadsheetExtensions.Contains(extension)) return ClipboardContentKind.Spreadsheet;
        if (RichTextExtensions.Contains(extension)) return ClipboardContentKind.RichText;
        if (MarkdownExtensions.Contains(extension)) return ClipboardContentKind.Markdown;
        return ClipboardContentKind.File;
    }

    private static bool LooksLikeMarkdown(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("\n#")
            || lower.Contains("```")
            || lower.Contains("](")
            || lower.Contains("\n- ")
            || lower.Contains("\n* ");
    }

    private static bool LooksLikeSpreadsheet(string text)
    {
        return text.Contains('\t') && text.Contains('\n');
    }

    private static readonly System.Text.RegularExpressions.Regex ColorRegex =
        new(@"^\s*(#?[0-9A-Fa-f]{6}|#?[0-9A-Fa-f]{8}|#?[0-9A-Fa-f]{3}|rgb\s*\(.+\)|rgba\s*\(.+\)|hsl\s*\(.+\)|hsla\s*\(.+\))\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool LooksLikeColor(string text)
    {
        if (text.Length > 32) return false;
        return ColorRegex.IsMatch(text);
    }

    private static byte[] EncodeAsPng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static object? TryGetData(string format)
    {
        try
        {
            return Clipboard.ContainsData(format) ? Clipboard.GetData(format) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _sink.ClipboardChanged -= OnClipboardChanged;
        _sink.Dispose();
    }
}
