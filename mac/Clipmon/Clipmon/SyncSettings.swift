import Foundation

/// Persisted sync configuration. Stored as JSON in `~/Library/Application Support/Clipmon/sync.json`.
struct SyncSettings: Codable, Equatable {
    var enabled: Bool = false
    var relayUrl: String = "ws://localhost:8765"
    var pairingCode: String = ""
    var deviceId: String = UUID().uuidString.lowercased().replacingOccurrences(of: "-", with: "")
    var deviceName: String = Host.current().localizedName ?? "Mac"

    // Privacy
    var clearHistoryOnQuit: Bool = false
    var autoClearPasteboardEnabled: Bool = false
    var autoClearAfterSeconds: Int = 60

    // Skip lists
    var skipApps: [String] = ["1Password", "Bitwarden", "Keeper", "LastPass"]
    var skipKeywords: [String] = []
    var sensitiveFilterEnabled: Bool = true

    static let pairingCharset = "abcdefghjkmnpqrstuvwxyz23456789"
    static let pairingLength = 10

    static func generatePairingCode() -> String {
        var rng = SystemRandomNumberGenerator()
        return String((0..<pairingLength).map { _ in
            pairingCharset.randomElement(using: &rng)!
        })
    }

    enum CodingKeys: String, CodingKey {
        case enabled, relayUrl, pairingCode, deviceId, deviceName
        case clearHistoryOnQuit, autoClearPasteboardEnabled, autoClearAfterSeconds
        case skipApps, skipKeywords, sensitiveFilterEnabled
    }

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.enabled = (try? c.decode(Bool.self, forKey: .enabled)) ?? false
        self.relayUrl = (try? c.decode(String.self, forKey: .relayUrl)) ?? "ws://localhost:8765"
        self.pairingCode = (try? c.decode(String.self, forKey: .pairingCode)) ?? ""
        self.deviceId = (try? c.decode(String.self, forKey: .deviceId)) ?? UUID().uuidString.lowercased().replacingOccurrences(of: "-", with: "")
        self.deviceName = (try? c.decode(String.self, forKey: .deviceName)) ?? (Host.current().localizedName ?? "Mac")
        self.clearHistoryOnQuit = (try? c.decode(Bool.self, forKey: .clearHistoryOnQuit)) ?? false
        self.autoClearPasteboardEnabled = (try? c.decode(Bool.self, forKey: .autoClearPasteboardEnabled)) ?? false
        self.autoClearAfterSeconds = (try? c.decode(Int.self, forKey: .autoClearAfterSeconds)) ?? 60
        self.skipApps = (try? c.decode([String].self, forKey: .skipApps)) ?? ["1Password", "Bitwarden", "Keeper", "LastPass"]
        self.skipKeywords = (try? c.decode([String].self, forKey: .skipKeywords)) ?? []
        self.sensitiveFilterEnabled = (try? c.decode(Bool.self, forKey: .sensitiveFilterEnabled)) ?? true
    }
}

@MainActor
final class SyncSettingsStore: ObservableObject {
    static let shared = SyncSettingsStore()

    @Published var current: SyncSettings

    private let url: URL

    init() {
        let dir = SyncSettingsStore.directoryURL()
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        self.url = dir.appendingPathComponent("sync.json")

        if let data = try? Data(contentsOf: url),
           let decoded = try? JSONDecoder().decode(SyncSettings.self, from: data) {
            var settings = decoded
            // Make sure deviceId is always populated.
            if settings.deviceId.isEmpty {
                settings.deviceId = UUID().uuidString.lowercased().replacingOccurrences(of: "-", with: "")
            }
            self.current = settings
        } else {
            self.current = SyncSettings()
        }
    }

    func save() {
        do {
            let data = try JSONEncoder().encode(current)
            try data.write(to: url, options: [.atomic])
        } catch {
            // Non-fatal: best-effort persistence.
        }
    }

    private static func directoryURL() -> URL {
        let base = (try? FileManager.default.url(
            for: .applicationSupportDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: true
        )) ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("Clipmon")
    }
}
