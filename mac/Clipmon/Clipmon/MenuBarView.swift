import AppKit
import SwiftData
import SwiftUI

@available(macOS 14.0, *)
struct MenuBarView: View {
    @Environment(\.modelContext) private var modelContext
    @EnvironmentObject private var controller: ClipboardHistoryController
    @EnvironmentObject private var syncStore: SyncSettingsStore
    @EnvironmentObject private var sync: SyncClient
    @Environment(\.openWindow) private var openWindow
    @State private var showingSettings = false

    @Query(sort: [SortDescriptor(\ClipboardEntry.updatedAt, order: .reverse)])
    private var entries: [ClipboardEntry]

    private var recentEntries: [ClipboardEntry] {
        controller.filteredEntries(from: entries).prefix(5).map { $0 }
    }

    private var searchResults: [ClipboardEntry] {
        controller.filteredEntries(from: entries)
    }

    private var hasSearchText: Bool {
        !controller.searchText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    init() {}

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header

            TextField("Search clipboard", text: $controller.searchText)
                .textFieldStyle(.roundedBorder)

            HStack(spacing: 8) {
                Button {
                    openMainWindow()
                } label: {
                    Label("Open Window", systemImage: "window.zoomedin")
                }

                Button {
                    controller.captureCurrentClipboard(force: true)
                } label: {
                    Label("Capture", systemImage: "arrow.down.doc")
                }
            }
            .buttonStyle(.bordered)

            HStack(spacing: 8) {
                Button {
                    if controller.isMonitoring {
                        controller.stop()
                    } else {
                        controller.startIfNeeded(modelContext: modelContext)
                    }
                } label: {
                    Label(controller.isMonitoring ? "Pause" : "Resume", systemImage: controller.isMonitoring ? "pause.fill" : "play.fill")
                }

                Button(role: .destructive) {
                    controller.clearHistory(keepingPinned: true)
                } label: {
                    Label("Clear Unpinned", systemImage: "trash")
                }
            }
            .buttonStyle(.bordered)

            Divider()

            if hasSearchText {
                Text("Search Results")
                    .font(.headline)

                if searchResults.isEmpty {
                    Text("No results for “\(controller.searchText)”")
                        .foregroundStyle(.secondary)
                        .padding(.vertical, 8)
                } else {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(searchResults, id: \.fingerprint) { entry in
                            MenuBarEntryRow(entry: entry) {
                                controller.copyToClipboard(entry)
                            } onPinToggle: {
                                controller.togglePin(entry)
                            }
                        }
                    }
                }
            } else {
                if recentEntries.isEmpty {
                    Text("No clipboard items yet")
                        .foregroundStyle(.secondary)
                        .padding(.vertical, 8)
                } else {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Recent Clips")
                            .font(.headline)

                        ForEach(recentEntries, id: \.fingerprint) { entry in
                            MenuBarEntryRow(entry: entry) {
                                controller.copyToClipboard(entry)
                            } onPinToggle: {
                                controller.togglePin(entry)
                            }
                        }
                    }
                }
            }

            Divider()

            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text(controller.statusMessage)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button {
                        showingSettings = true
                    } label: {
                        Image(systemName: "gearshape")
                    }
                    .buttonStyle(.bordered)
                    Button("Quit") {
                        NSApp.terminate(nil)
                    }
                    .buttonStyle(.bordered)
                }

                if syncStore.current.enabled {
                    Text("sync · \(sync.connectionState)")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding(14)
        .frame(width: 360)
        .onAppear {
            controller.startIfNeeded(modelContext: modelContext)
        }
        .sheet(isPresented: $showingSettings) {
            SyncSettingsView()
                .environmentObject(syncStore)
                .environmentObject(sync)
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Label("Clipmon", systemImage: "doc.on.clipboard")
                    .font(.headline)

                Spacer()

                Image(systemName: controller.isMonitoring ? "dot.radiowaves.left.and.right" : "pause.circle")
                    .foregroundStyle(controller.isMonitoring ? .green : .secondary)
            }

            Text("Clipboard history in the menu bar")
                .font(.caption)
                .foregroundStyle(.secondary)

            Text(controller.statusMessage)
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
    }

    private func openMainWindow() {
        openWindow(id: "main")
        NSApp.activate(ignoringOtherApps: true)
    }
}

extension Color {
    init?(hex: String) {
        let s = hex.trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "#", with: "")
        guard let value = UInt64(s, radix: 16) else { return nil }
        let r: Double, g: Double, b: Double, a: Double
        switch s.count {
        case 3:
            r = Double((value >> 8) & 0xF) / 15
            g = Double((value >> 4) & 0xF) / 15
            b = Double(value & 0xF) / 15
            a = 1
        case 6:
            r = Double((value >> 16) & 0xFF) / 255
            g = Double((value >> 8) & 0xFF) / 255
            b = Double(value & 0xFF) / 255
            a = 1
        case 8:
            r = Double((value >> 24) & 0xFF) / 255
            g = Double((value >> 16) & 0xFF) / 255
            b = Double((value >> 8) & 0xFF) / 255
            a = Double(value & 0xFF) / 255
        default: return nil
        }
        self.init(.sRGB, red: r, green: g, blue: b, opacity: a)
    }
}

@available(macOS 14.0, *)
private struct MenuBarEntryRow: View {
    let entry: ClipboardEntry
    let onCopy: () -> Void
    let onPinToggle: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Button(action: onCopy) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    if entry.kind == .color, let color = Color(hex: entry.textContent ?? "") {
                        RoundedRectangle(cornerRadius: 3, style: .continuous)
                            .fill(color)
                            .overlay(RoundedRectangle(cornerRadius: 3).stroke(.secondary.opacity(0.3), lineWidth: 0.5))
                            .frame(width: 14, height: 14)
                    } else {
                        Label(entry.kind.displayName, systemImage: entry.kind.sfSymbol)
                            .font(.caption2.weight(.semibold))
                            .foregroundStyle(.secondary)
                    }

                    Text(entry.displayTitle)
                        .lineLimit(2)
                        .multilineTextAlignment(.leading)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    if entry.isPinned {
                        Image(systemName: "pin.fill")
                            .foregroundStyle(.orange)
                    }
                }
            }
            .buttonStyle(.plain)

            HStack {
                Text(entry.createdAt.formatted(date: .omitted, time: .shortened))
                    .foregroundStyle(.secondary)
                if let fileName = entry.fileName {
                    Text("•")
                    Text(fileName)
                        .lineLimit(1)
                }
                Spacer()
                Button(entry.isPinned ? "Unpin" : "Pin", action: onPinToggle)
                    .buttonStyle(.plain)
            }
            .font(.caption)
        }
        .padding(10)
        .background(.quaternary.opacity(0.6), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .onDrag { dragProvider() }
    }

    private func dragProvider() -> NSItemProvider {
        // Files: drag the existing file URL.
        if let urlString = entry.fileURLString, let url = URL(string: urlString), url.isFileURL {
            return NSItemProvider(contentsOf: url) ?? NSItemProvider()
        }

        // Images: write payload to a temp .png and drag that.
        if entry.kind == .image, let data = entry.payloadData {
            let dir = NSTemporaryDirectory() + "Clipmon/drag-cache/"
            try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
            let path = dir + "\(String(entry.fingerprint.prefix(12))).png"
            if let nsImage = NSImage(data: data),
               let tiff = nsImage.tiffRepresentation,
               let rep = NSBitmapImageRep(data: tiff),
               let png = rep.representation(using: .png, properties: [:]) {
                try? png.write(to: URL(fileURLWithPath: path))
            } else {
                try? data.write(to: URL(fileURLWithPath: path))
            }
            return NSItemProvider(contentsOf: URL(fileURLWithPath: path)) ?? NSItemProvider()
        }

        // Everything else: drag the plain text.
        let text = entry.textContent ?? entry.displayTitle
        return NSItemProvider(object: text as NSString)
    }
}
