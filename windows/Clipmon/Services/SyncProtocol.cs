using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clipmon.Models;

namespace Clipmon.Services;

/// <summary>
/// Constants and helpers shared by the relay protocol on both ends.
/// IMPORTANT: keep in sync with the macOS Swift implementation.
/// </summary>
public static class SyncProtocol
{
    public const string KeySalt = "clipmon-key-v1";
    public const string RoomSalt = "clipmon-room-v1";
    public const int Pbkdf2Iterations = 200_000;
    public const int KeySizeBytes = 32; // AES-256
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] DeriveKey(string pairingCode)
    {
        if (string.IsNullOrEmpty(pairingCode)) throw new ArgumentException("Pairing code is required");
        var salt = Encoding.UTF8.GetBytes(KeySalt);
        using var pbk = new Rfc2898DeriveBytes(pairingCode, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
        return pbk.GetBytes(KeySizeBytes);
    }

    public static string DeriveRoomId(string pairingCode)
    {
        if (string.IsNullOrEmpty(pairingCode)) throw new ArgumentException("Pairing code is required");
        var bytes = Encoding.UTF8.GetBytes(pairingCode + ":" + RoomSalt);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string EncryptEnvelope(byte[] key, string plaintextJson)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plaintext = Encoding.UTF8.GetBytes(plaintextJson);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[NonceBytes + ciphertext.Length + TagBytes];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceBytes);
        Buffer.BlockCopy(ciphertext, 0, output, NonceBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, output, NonceBytes + ciphertext.Length, TagBytes);
        return Convert.ToBase64String(output);
    }

    /// <summary>
    /// Wire format for Kind matches the Swift raw values exactly: lowercase-first-letter.
    /// Mac uses "text"/"richText"/"spreadsheet"/etc., so Windows MUST send the same strings.
    /// </summary>
    public static string ToWireKind(ClipboardContentKind kind) => kind switch
    {
        ClipboardContentKind.Text => "text",
        ClipboardContentKind.Markdown => "markdown",
        ClipboardContentKind.RichText => "richText",
        ClipboardContentKind.Spreadsheet => "spreadsheet",
        ClipboardContentKind.Image => "image",
        ClipboardContentKind.Audio => "audio",
        ClipboardContentKind.File => "file",
        _ => "text"
    };

    public static ClipboardContentKind FromWireKind(string? wire) => (wire ?? "text").ToLowerInvariant() switch
    {
        "text" => ClipboardContentKind.Text,
        "markdown" => ClipboardContentKind.Markdown,
        "richtext" => ClipboardContentKind.RichText,
        "spreadsheet" => ClipboardContentKind.Spreadsheet,
        "image" => ClipboardContentKind.Image,
        "audio" => ClipboardContentKind.Audio,
        "file" => ClipboardContentKind.File,
        _ => ClipboardContentKind.Text
    };

    public static string DecryptEnvelope(byte[] key, string envelopeBase64)
    {
        var envelope = Convert.FromBase64String(envelopeBase64);
        if (envelope.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("Envelope is too short");
        }

        var nonce = envelope.AsSpan(0, NonceBytes);
        var tag = envelope.AsSpan(envelope.Length - TagBytes, TagBytes);
        var ciphertext = envelope.AsSpan(NonceBytes, envelope.Length - NonceBytes - TagBytes);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}

/// <summary>
/// JSON shape carried inside the encrypted envelope. Wire-compatible with the
/// macOS client. Field names are camelCase in JSON.
/// </summary>
public sealed class SyncEnvelope
{
    public string Fingerprint { get; set; } = string.Empty;
    public string Kind { get; set; } = "Text";
    public string? TextContent { get; set; }
    public string? FileName { get; set; }
    public string? FileUrl { get; set; }
    public string? PayloadDataBase64 { get; set; }
    public string? UtiIdentifier { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsPinned { get; set; }
    public string? SourceApplication { get; set; }
    public string FromDeviceId { get; set; } = string.Empty;
    public string FromDeviceName { get; set; } = string.Empty;
}
