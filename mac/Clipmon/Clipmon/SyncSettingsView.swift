import SwiftUI

@available(macOS 14.0, *)
struct SyncSettingsView: View {
    @EnvironmentObject private var store: SyncSettingsStore
    @EnvironmentObject private var sync: SyncClient
    @Environment(\.dismiss) private var dismiss

    @State private var draft = SyncSettings()
    @State private var skipAppsText = ""
    @State private var skipKeywordsText = ""

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Clipmon settings")
                    .font(.title2.weight(.semibold))
                Text("Sync, privacy, and filtering controls.")
                    .foregroundStyle(.secondary)
                    .font(.caption)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 20)
            .padding(.top, 20)

            TabView {
                syncTab
                    .tabItem { Label("Sync", systemImage: "arrow.triangle.2.circlepath") }
                privacyTab
                    .tabItem { Label("Privacy", systemImage: "lock.shield") }
                filtersTab
                    .tabItem { Label("Filters", systemImage: "line.3.horizontal.decrease.circle") }
                aboutTab
                    .tabItem { Label("About", systemImage: "info.circle") }
            }
            .padding(.horizontal, 20)
            .padding(.top, 14)

            HStack {
                Text(sync.connectionState)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save") { save() }
                    .buttonStyle(.borderedProminent)
                    .disabled(draft.enabled && draft.pairingCode.isEmpty)
            }
            .padding(20)
        }
        .frame(width: 620, height: 620)
        .onAppear {
            draft = store.current
            skipAppsText = draft.skipApps.joined(separator: "\n")
            skipKeywordsText = draft.skipKeywords.joined(separator: "\n")
        }
    }

    // MARK: - Sync tab

    @ViewBuilder
    private var syncTab: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                if draft.enabled && draft.pairingCode.isEmpty {
                    GroupBox {
                        VStack(alignment: .leading, spacing: 6) {
                            Text("Pair this device")
                                .font(.headline)
                            Text("Generate a pairing code below, then enter the same code on every other device. Treat the code like a password — anyone with it can read your synced clipboard.")
                                .foregroundStyle(.secondary)
                                .font(.caption)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(8)
                    }
                }

                GroupBox {
                    Toggle("Enable sync", isOn: $draft.enabled)
                        .toggleStyle(.switch)
                        .padding(8)
                }

                GroupBox(label: Text("Pairing code").font(.headline)) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Same code on every device. Click Generate, then copy the result to other devices.")
                            .foregroundStyle(.secondary)
                            .font(.caption2)
                        HStack {
                            TextField("pairing code", text: $draft.pairingCode)
                                .textFieldStyle(.roundedBorder)
                                .font(.system(.body, design: .monospaced))
                            Button("Generate") {
                                draft.pairingCode = SyncSettings.generatePairingCode()
                            }
                            Button("Copy") {
                                NSPasteboard.general.clearContents()
                                NSPasteboard.general.setString(draft.pairingCode, forType: .string)
                            }
                            .disabled(draft.pairingCode.isEmpty)
                        }
                    }
                    .padding(8)
                }

                GroupBox(label: Text("Relay server").font(.headline)) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("WebSocket URL of a relay you control.")
                            .foregroundStyle(.secondary)
                            .font(.caption2)
                        TextField("ws://...", text: $draft.relayUrl)
                            .textFieldStyle(.roundedBorder)
                            .font(.system(.body, design: .monospaced))
                    }
                    .padding(8)
                }

                GroupBox(label: Text("Device name").font(.headline)) {
                    TextField("This Mac", text: $draft.deviceName)
                        .textFieldStyle(.roundedBorder)
                        .padding(8)
                }

                GroupBox(label: Text("Connected peers").font(.headline)) {
                    if sync.connectedPeers.isEmpty {
                        Text("No other devices connected.")
                            .foregroundStyle(.secondary)
                            .font(.caption)
                            .padding(8)
                    } else {
                        VStack(alignment: .leading, spacing: 4) {
                            ForEach(sync.connectedPeers) { peer in
                                HStack {
                                    Image(systemName: "circle.fill")
                                        .foregroundStyle(.green)
                                        .imageScale(.small)
                                    Text(peer.deviceName)
                                        .font(.callout)
                                }
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(8)
                    }
                }
            }
            .padding(.vertical, 8)
        }
    }

    // MARK: - Privacy tab

    @ViewBuilder
    private var privacyTab: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                GroupBox {
                    VStack(alignment: .leading, spacing: 6) {
                        Toggle("Clear unpinned history when Clipmon quits", isOn: $draft.clearHistoryOnQuit)
                        Text("Pinned items survive across launches; everything else is wiped on quit.")
                            .foregroundStyle(.secondary)
                            .font(.caption2)
                    }
                    .padding(8)
                }

                GroupBox(label: Text("Auto-clear OS clipboard").font(.headline)) {
                    VStack(alignment: .leading, spacing: 8) {
                        Toggle("Wipe the system pasteboard automatically", isOn: $draft.autoClearPasteboardEnabled)
                        Text("After this many seconds with no clipboard activity, the system pasteboard is wiped. Your Clipmon history is unaffected.")
                            .foregroundStyle(.secondary)
                            .font(.caption2)
                        HStack {
                            Text("Clear after")
                                .foregroundStyle(.secondary)
                                .font(.caption)
                            Stepper(value: $draft.autoClearAfterSeconds, in: 5...86400, step: 30) {
                                TextField("", value: $draft.autoClearAfterSeconds, format: .number)
                                    .textFieldStyle(.roundedBorder)
                                    .frame(width: 80)
                            }
                            .labelsHidden()
                            Text("seconds")
                                .foregroundStyle(.secondary)
                                .font(.caption)
                        }
                    }
                    .padding(8)
                }
            }
            .padding(.vertical, 8)
        }
    }

    // MARK: - Filters tab

    @ViewBuilder
    private var filtersTab: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                GroupBox {
                    VStack(alignment: .leading, spacing: 6) {
                        Toggle("Skip likely credentials (recommended)", isOn: $draft.sensitiveFilterEnabled)
                        Text("Detects API keys (sk-, AKIA, ghp_…), JWTs, PEM blocks, and high-entropy tokens. Items matched are not recorded.")
                            .foregroundStyle(.secondary)
                            .font(.caption2)
                    }
                    .padding(8)
                }

                GroupBox(label: Text("Skip these apps").font(.headline)) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("App names whose clipboard is never recorded. One per line, partial match (case-insensitive).")
                            .foregroundStyle(.secondary)
                            .font(.caption2)
                        TextEditor(text: $skipAppsText)
                            .font(.system(.body, design: .monospaced))
                            .frame(minHeight: 100)
                            .border(.quaternary)
                    }
                    .padding(8)
                }

                GroupBox(label: Text("Skip keywords").font(.headline)) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Substrings that, if present in clipboard text, cause it to be ignored. One per line.")
                            .foregroundStyle(.secondary)
                            .font(.caption2)
                        TextEditor(text: $skipKeywordsText)
                            .font(.system(.body, design: .monospaced))
                            .frame(minHeight: 80)
                            .border(.quaternary)
                    }
                    .padding(8)
                }
            }
            .padding(.vertical, 8)
        }
    }

    // MARK: - About tab

    @ViewBuilder
    private var aboutTab: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                GroupBox {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Clipmon").font(.title3.weight(.bold))
                        Text("Local-first clipboard manager with optional E2E-encrypted sync.")
                            .foregroundStyle(.secondary)
                            .font(.callout)
                        Text("• Sync envelopes are AES-256-GCM encrypted with a key derived from your pairing code (PBKDF2-HMAC-SHA256, 200k iterations).")
                            .foregroundStyle(.secondary)
                            .font(.caption)
                        Text("• The relay server never sees your pairing code or clipboard contents.")
                            .foregroundStyle(.secondary)
                            .font(.caption)
                        Text("• Local at-rest encryption is enabled on the Windows app via DPAPI; on macOS the Keychain key infrastructure is in place but the SwiftData schema migration is still pending.")
                            .foregroundStyle(.secondary)
                            .font(.caption)
                    }
                    .padding(8)
                }
            }
            .padding(.vertical, 8)
        }
    }

    private func save() {
        var updated = draft
        updated.skipApps = splitLines(skipAppsText)
        updated.skipKeywords = splitLines(skipKeywordsText)
        store.current = updated
        store.save()
        dismiss()
    }

    private func splitLines(_ text: String) -> [String] {
        text.components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
    }
}
