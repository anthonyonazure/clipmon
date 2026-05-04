namespace Clipmon.Models;

public enum ClipboardContentKind
{
    Text,
    Markdown,
    RichText,
    Spreadsheet,
    Image,
    Audio,
    File,
    Color
}

public static class ClipboardContentKindExtensions
{
    public static string DisplayName(this ClipboardContentKind kind) => kind switch
    {
        ClipboardContentKind.Text => "Text",
        ClipboardContentKind.Markdown => "Markdown",
        ClipboardContentKind.RichText => "Rich Text",
        ClipboardContentKind.Spreadsheet => "Spreadsheet",
        ClipboardContentKind.Image => "Image",
        ClipboardContentKind.Audio => "Audio",
        ClipboardContentKind.File => "File",
        ClipboardContentKind.Color => "Color",
        _ => "Item"
    };

    public static string Glyph(this ClipboardContentKind kind) => kind switch
    {
        ClipboardContentKind.Text => "",
        ClipboardContentKind.Markdown => "",
        ClipboardContentKind.RichText => "",
        ClipboardContentKind.Spreadsheet => "",
        ClipboardContentKind.Image => "",
        ClipboardContentKind.Audio => "",
        ClipboardContentKind.File => "",
        ClipboardContentKind.Color => "",
        _ => ""
    };

    public static string Serialize(this ClipboardContentKind kind) => kind.ToString();

    public static ClipboardContentKind Parse(string raw) =>
        Enum.TryParse<ClipboardContentKind>(raw, ignoreCase: true, out var value)
            ? value
            : ClipboardContentKind.Text;
}
