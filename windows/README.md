# Clipmon for Windows

Native Windows port of Clipmon. Same idea as the macOS app — a fast, searchable
clipboard history with rich previews — built on .NET 8 + WPF instead of SwiftUI.

## Requirements

- Windows 10 1809 or newer (Windows 11 recommended)
- .NET 8 SDK to build, or .NET 8 Desktop Runtime to run a published build

## Build & run

```powershell
cd windows
dotnet build Clipmon.sln -c Debug
dotnet run --project Clipmon
```

Or open `Clipmon.sln` in Visual Studio 2022+ and press F5.

## Publish a single-file executable

```powershell
dotnet publish Clipmon -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\publish
```

Drop the resulting `Clipmon.exe` (plus `runtimes\` if self-contained) anywhere
on disk. The first launch will create `%LOCALAPPDATA%\Clipmon\clipmon.db`.

## Architecture

| Layer | Implementation |
|------|------|
| UI | WPF + MVVM via `CommunityToolkit.Mvvm` |
| System tray | `System.Windows.Forms.NotifyIcon` (programmatically generated icon) |
| Clipboard hook | Native `AddClipboardFormatListener` via a hidden `HwndSource` (no polling) |
| Persistence | SQLite via `Microsoft.Data.Sqlite`, schema in [`ClipboardDatabase.cs`](Clipmon/Services/ClipboardDatabase.cs) |
| Source app | `GetForegroundWindow` + `GetWindowThreadProcessId` |

The fingerprint scheme (`SHA256` over kind + text + filename + payload-hash)
matches the macOS version, so an entry copied on either platform would
deduplicate identically if the database were ever shared.

## Feature parity with macOS

- [x] Auto-capture clipboard changes (text, RTF, image, files)
- [x] Pin / unpin
- [x] Delete / clear-with-keep-pinned / clear-everything
- [x] Search
- [x] All / Pinned scope filter
- [x] File drop import
- [x] Copy back to clipboard
- [x] Pause / resume monitoring
- [x] Source application name
- [x] Image preview in the detail pane
- [x] Type detection: text, markdown, rich text, spreadsheet, image, audio, file
- [x] Tray icon with context menu (Open / Capture now / Pause / Quit)

## File layout

```
windows/
  Clipmon.sln
  Clipmon/
    Clipmon.csproj
    GlobalUsings.cs        # WPF vs WinForms type aliases
    App.xaml(.cs)
    Models/
      ClipboardContentKind.cs
      ClipboardEntry.cs
      ClipboardCapturePayload.cs
    Services/
      ClipboardDatabase.cs
      ClipboardMonitor.cs
      ClipboardWindowMessageSink.cs
      SourceApplicationResolver.cs
      TrayIconService.cs
    ViewModels/
      MainViewModel.cs
    Views/
      MainWindow.xaml(.cs)
    Converters/
      Converters.cs
```
