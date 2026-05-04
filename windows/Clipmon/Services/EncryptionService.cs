using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Clipmon.Services;

/// <summary>
/// AES-256-GCM encryption with the master key DPAPI-protected on disk.
///
/// Wire format:  [12 nonce bytes] [ciphertext bytes] [16 tag bytes]
/// Key file:     %LocalAppData%/Clipmon/dataprotect.bin
///               (32 random bytes wrapped with Windows DPAPI, CurrentUser scope)
/// </summary>
public sealed class EncryptionService
{
    private const int KeySizeBytes = 32; // AES-256
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _key;

    public EncryptionService(string? overrideDirectory = null)
    {
        var directory = overrideDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipmon");
        Directory.CreateDirectory(directory);

        var keyFile = Path.Combine(directory, "dataprotect.bin");
        _key = LoadOrCreateKey(keyFile);
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var protectedBytes = File.ReadAllBytes(path);
                var key = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                if (key.Length == KeySizeBytes) return key;
            }
            catch
            {
                // Corrupt or wrong-user key — regenerate.
            }
        }

        var fresh = RandomNumberGenerator.GetBytes(KeySizeBytes);
        var wrapped = ProtectedData.Protect(fresh, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, wrapped);
        return fresh;
    }

    public byte[] EncryptBytes(ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[NonceBytes + ciphertext.Length + TagBytes];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceBytes);
        Buffer.BlockCopy(ciphertext, 0, output, NonceBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, output, NonceBytes + ciphertext.Length, TagBytes);
        return output;
    }

    public byte[] DecryptBytes(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("Encrypted payload is malformed.");
        }

        var nonce = envelope[..NonceBytes];
        var tag = envelope[(envelope.Length - TagBytes)..];
        var ciphertext = envelope[NonceBytes..(envelope.Length - TagBytes)];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public string EncryptStringToBase64(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        return Convert.ToBase64String(EncryptBytes(bytes));
    }

    public string? DecryptStringFromBase64(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(DecryptBytes(bytes));
        }
        catch
        {
            return null;
        }
    }
}
