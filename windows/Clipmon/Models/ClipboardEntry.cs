using System.Security.Cryptography;
using System.Text;

namespace Clipmon.Models;

public sealed class ClipboardEntry
{
    public required string Fingerprint { get; set; }
    public ClipboardContentKind Kind { get; set; } = ClipboardContentKind.Text;
    public string? TextContent { get; set; }
    public string? FileName { get; set; }
    public string? FileUrl { get; set; }
    public byte[]? PayloadData { get; set; }
    public string? UtiIdentifier { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsPinned { get; set; }
    public string? SourceApplication { get; set; }

    public string DisplayTitle => Kind switch
    {
        ClipboardContentKind.Image => FileName ?? "Image",
        ClipboardContentKind.Audio => FileName ?? "Audio",
        ClipboardContentKind.File => FileName ?? "File",
        _ => Preview
    };

    public string Preview
    {
        get
        {
            if (Kind is ClipboardContentKind.Image or ClipboardContentKind.Audio or ClipboardContentKind.File)
            {
                return FileName ?? Kind.DisplayName() + " clipboard item";
            }

            var content = (TextContent ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            if (string.IsNullOrEmpty(content))
            {
                return "Empty clipboard item";
            }

            return content.Length <= 120 ? content : content[..117] + "...";
        }
    }

    public string SearchableText
    {
        get
        {
            var parts = new[]
            {
                Kind.DisplayName(),
                TextContent,
                FileName,
                FileUrl,
                UtiIdentifier,
                SourceApplication
            };

            return string.Join(' ', parts.Where(p => !string.IsNullOrEmpty(p))!).ToLowerInvariant();
        }
    }

    public static string ComputeFingerprint(
        ClipboardContentKind kind,
        string? textContent,
        string? fileName,
        string? fileUrl,
        byte[]? payloadData,
        string? utiIdentifier)
    {
        var seed = new StringBuilder();
        seed.Append(kind.Serialize()).Append("::");
        seed.Append(textContent ?? string.Empty);
        seed.Append("::").Append(fileName ?? string.Empty);
        seed.Append("::").Append(fileUrl ?? string.Empty);
        seed.Append("::").Append(utiIdentifier ?? string.Empty);

        if (payloadData is { Length: > 0 })
        {
            seed.Append("::").Append(Sha256Hex(payloadData));
        }

        return Sha256Hex(Encoding.UTF8.GetBytes(seed.ToString()));
    }

    private static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}
