namespace Clipmon.Models;

public sealed record ClipboardCapturePayload(
    ClipboardContentKind Kind,
    string? TextContent,
    string? FileName,
    string? FileUrl,
    byte[]? PayloadData,
    string? UtiIdentifier,
    string? SourceApplication)
{
    public string Fingerprint => ClipboardEntry.ComputeFingerprint(
        Kind, TextContent, FileName, FileUrl, PayloadData, UtiIdentifier);
}
