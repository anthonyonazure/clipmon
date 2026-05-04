import AppKit
import Combine
import CryptoKit
import Foundation
import SwiftData
import UniformTypeIdentifiers

struct PeerInfo: Identifiable, Equatable {
    let deviceId: String
    let deviceName: String
    var id: String { deviceId }

    static func from(dict: [String: Any]) -> PeerInfo? {
        guard let id = dict["deviceId"] as? String, !id.isEmpty else { return nil }
        let name = (dict["deviceName"] as? String) ?? "Device"
        return PeerInfo(deviceId: id, deviceName: name)
    }
}

@available(macOS 14.0, *)
@MainActor
final class SyncClient: ObservableObject {
    static let shared = SyncClient()

    private let backfillCount = 20
    private let maxPayloadBytes = 2 * 1024 * 1024 // 2 MB raw cap

    @Published private(set) var connectionState: String = "Disabled"
    @Published private(set) var connectedPeers: [PeerInfo] = []

    private let store = SyncSettingsStore.shared
    private weak var modelContext: ModelContext?
    private weak var controller: ClipboardHistoryController?

    private var task: URLSessionWebSocketTask?
    private var key: SymmetricKey?
    private var roomId: String?
    private var attempt: Int = 0
    private var reconnectWorkItem: DispatchWorkItem?
    private var settingsCancellable: AnyCancellable?
    private var entryCancellable: AnyCancellable?
    private var recentlyReceivedFingerprints: Set<String> = []
    private var lastBroadcastFingerprint: String?

    private let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        return e
    }()

    private let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()

    func attach(controller: ClipboardHistoryController, modelContext: ModelContext) {
        self.controller = controller
        self.modelContext = modelContext

        // Forward newly-saved entries to the relay.
        controller.onEntrySaved = { [weak self] entry in
            Task { @MainActor in
                self?.broadcast(entry: entry)
            }
        }

        // Restart whenever settings change.
        settingsCancellable?.cancel()
        settingsCancellable = store.$current
            .removeDuplicates()
            .sink { [weak self] _ in
                Task { @MainActor in
                    self?.restart()
                }
            }

        start()
    }

    /// Call this every time a local clipboard entry is persisted, so we can broadcast it.
    func broadcast(entry: ClipboardEntry) {
        guard let task, let key, let roomId, store.current.enabled else { return }

        // Don't echo a clip we just received from the network.
        if recentlyReceivedFingerprints.remove(entry.fingerprint) != nil { return }

        let settings = store.current

        // Lazy hydrate file bytes when we have a local URL but no in-memory payload.
        var payload = entry.payloadData
        if payload == nil, let urlString = entry.fileURLString, let url = URL(string: urlString), url.isFileURL {
            payload = readCapped(url: url)
        }
        if let p = payload, p.count > maxPayloadBytes {
            connectionState = "Skipping \(entry.fileName ?? entry.kind.displayName) (\(p.count / 1024 / 1024) MB) — too large to sync"
            payload = nil
        }

        let envelope = SyncEnvelopeWire(
            fingerprint: entry.fingerprint,
            kind: entry.kind.rawValue,
            textContent: entry.textContent,
            fileName: entry.fileName,
            fileUrl: nil,
            payloadDataBase64: payload?.base64EncodedString(),
            utiIdentifier: entry.utiIdentifier,
            createdAt: entry.createdAt,
            updatedAt: entry.updatedAt,
            isPinned: entry.isPinned,
            sourceApplication: entry.sourceApplication,
            fromDeviceId: settings.deviceId,
            fromDeviceName: settings.deviceName
        )

        do {
            let plaintext = try encoder.encode(envelope)
            guard let plaintextString = String(data: plaintext, encoding: .utf8) else { return }
            let envelopeBase64 = try SyncProtocol.encryptEnvelope(key: key, plaintextJson: plaintextString)
            let payload: [String: Any] = ["type": "clip", "room": roomId, "envelope": envelopeBase64]
            let data = try JSONSerialization.data(withJSONObject: payload)
            guard let str = String(data: data, encoding: .utf8) else { return }

            lastBroadcastFingerprint = entry.fingerprint
            task.send(.string(str)) { [weak self] error in
                if let error {
                    Task { @MainActor in
                        self?.connectionState = "Send failed: \(error.localizedDescription)"
                    }
                }
            }
        } catch {
            connectionState = "Encrypt failed: \(error.localizedDescription)"
        }
    }

    // MARK: Lifecycle

    private func restart() {
        stop()
        start()
    }

    private func start() {
        let settings = store.current
        guard settings.enabled, !settings.pairingCode.isEmpty, !settings.relayUrl.isEmpty else {
            connectionState = "Disabled"
            return
        }

        do {
            self.key = try SyncProtocol.deriveKey(pairingCode: settings.pairingCode)
            self.roomId = try SyncProtocol.deriveRoomId(pairingCode: settings.pairingCode)
        } catch {
            connectionState = "Bad pairing code"
            return
        }

        connect()
    }

    private func stop() {
        reconnectWorkItem?.cancel()
        reconnectWorkItem = nil
        task?.cancel(with: .normalClosure, reason: nil)
        task = nil
        key = nil
        roomId = nil
        connectionState = "Disconnected"
    }

    private func connect() {
        guard let url = URL(string: store.current.relayUrl) else {
            connectionState = "Invalid relay URL"
            return
        }
        connectionState = "Connecting to \(url.host ?? "relay")…"

        let session = URLSession(configuration: .ephemeral)
        let task = session.webSocketTask(with: url)
        self.task = task
        task.resume()

        sendJoin()
        receiveLoop(task: task)
    }

    private func sendJoin() {
        guard let task, let roomId else { return }
        let settings = store.current
        let payload: [String: Any] = [
            "type": "join",
            "room": roomId,
            "deviceId": settings.deviceId,
            "deviceName": settings.deviceName
        ]
        guard let data = try? JSONSerialization.data(withJSONObject: payload),
              let str = String(data: data, encoding: .utf8) else { return }
        task.send(.string(str)) { [weak self] error in
            if let error {
                Task { @MainActor in
                    self?.connectionState = "Join failed: \(error.localizedDescription)"
                    self?.scheduleReconnect()
                }
            } else {
                Task { @MainActor in
                    self?.connectionState = "Connected"
                }
            }
        }
    }

    private func receiveLoop(task: URLSessionWebSocketTask) {
        task.receive { [weak self] result in
            Task { @MainActor in
                guard let self else { return }
                guard self.task === task else { return } // stale loop after restart

                switch result {
                case .success(.string(let text)):
                    self.handleServerMessage(text)
                    self.receiveLoop(task: task)
                case .success(.data(let data)):
                    if let text = String(data: data, encoding: .utf8) {
                        self.handleServerMessage(text)
                    }
                    self.receiveLoop(task: task)
                case .success:
                    self.receiveLoop(task: task)
                case .failure(let error):
                    self.connectionState = "Disconnected (\(error.localizedDescription))"
                    self.scheduleReconnect()
                }
            }
        }
    }

    private func scheduleReconnect() {
        guard store.current.enabled else { return }
        reconnectWorkItem?.cancel()

        attempt += 1
        let delay = min(60, pow(2.0, Double(min(attempt, 6))))
        let work = DispatchWorkItem { [weak self] in
            Task { @MainActor in
                self?.connect()
            }
        }
        reconnectWorkItem = work
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
    }

    private func handleServerMessage(_ text: String) {
        guard let data = text.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = obj["type"] as? String else { return }

        switch type {
        case "joined":
            connectionState = "Synced"
            attempt = 0
            if let peers = obj["peers"] as? [[String: Any]] {
                connectedPeers = peers.compactMap(PeerInfo.from(dict:))
            }
        case "peer-joined":
            if let info = PeerInfo.from(dict: obj) {
                if !connectedPeers.contains(where: { $0.deviceId == info.deviceId }) {
                    connectedPeers.append(info)
                }
                connectionState = "Peer joined: \(info.deviceName)"
                sendBackfill(targetDeviceId: info.deviceId)
            }
        case "peer-left":
            if let id = obj["deviceId"] as? String {
                connectedPeers.removeAll { $0.deviceId == id }
            }
            connectionState = "Peer left"
        case "clip":
            handleClip(obj: obj)
        case "error":
            let code = (obj["code"] as? String) ?? "unknown"
            connectionState = "Server error: \(code)"
        default:
            break
        }
    }

    private func sendBackfill(targetDeviceId: String) {
        guard !targetDeviceId.isEmpty,
              let modelContext,
              let task,
              let key,
              let roomId else { return }

        var descriptor = FetchDescriptor<ClipboardEntry>(
            sortBy: [SortDescriptor(\.updatedAt, order: .reverse)]
        )
        descriptor.fetchLimit = backfillCount

        let entries: [ClipboardEntry]
        do {
            entries = try modelContext.fetch(descriptor)
        } catch {
            return
        }

        let settings = store.current
        for entry in entries {
            let envelope = SyncEnvelopeWire(
                fingerprint: entry.fingerprint,
                kind: entry.kind.rawValue,
                textContent: entry.textContent,
                fileName: entry.fileName,
                fileUrl: nil,
                payloadDataBase64: entry.payloadData?.base64EncodedString(),
                utiIdentifier: entry.utiIdentifier,
                createdAt: entry.createdAt,
                updatedAt: entry.updatedAt,
                isPinned: entry.isPinned,
                sourceApplication: entry.sourceApplication,
                fromDeviceId: settings.deviceId,
                fromDeviceName: settings.deviceName
            )

            do {
                let plaintext = try encoder.encode(envelope)
                guard let plaintextString = String(data: plaintext, encoding: .utf8) else { continue }
                let envelopeBase64 = try SyncProtocol.encryptEnvelope(key: key, plaintextJson: plaintextString)
                let payload: [String: Any] = [
                    "type": "clip",
                    "room": roomId,
                    "envelope": envelopeBase64,
                    "targetDeviceId": targetDeviceId,
                    "backfill": true,
                ]
                if let data = try? JSONSerialization.data(withJSONObject: payload),
                   let str = String(data: data, encoding: .utf8) {
                    task.send(.string(str)) { _ in }
                }
            } catch {
                continue
            }
        }
    }

    private func handleClip(obj: [String: Any]) {
        guard let key, let envelopeBase64 = obj["envelope"] as? String else { return }
        do {
            let plaintext = try SyncProtocol.decryptEnvelope(key: key, envelopeBase64: envelopeBase64)
            guard let data = plaintext.data(using: .utf8) else { return }
            let envelope = try decoder.decode(SyncEnvelopeWire.self, from: data)

            // Self-echo guard.
            if envelope.fromDeviceId == store.current.deviceId { return }

            recentlyReceivedFingerprints.insert(envelope.fingerprint)
            ingest(envelope)
        } catch {
            connectionState = "Decrypt failed: \(error.localizedDescription)"
        }
    }

    private func ingest(_ envelope: SyncEnvelopeWire) {
        guard let modelContext else { return }
        guard let kind = ClipboardContentKind(rawValue: envelope.kind) else { return }

        let payloadData: Data?
        if let b64 = envelope.payloadDataBase64 {
            payloadData = Data(base64Encoded: b64)
        } else {
            payloadData = nil
        }

        // Materialize audio/file bytes to a temp file so the entry behaves like a local file.
        var localFileURLString: String? = nil
        if let payloadData, !payloadData.isEmpty,
           let fileName = envelope.fileName,
           kind == .audio || kind == .file {
            let dir = NSTemporaryDirectory() + "Clipmon/sync-cache/"
            try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
            let safe = fileName.replacingOccurrences(of: "/", with: "_")
            let prefix = String(envelope.fingerprint.prefix(12))
            let path = dir + "\(prefix)-\(safe)"
            do {
                try payloadData.write(to: URL(fileURLWithPath: path))
                localFileURLString = URL(fileURLWithPath: path).absoluteString
            } catch {
                localFileURLString = nil
            }
        }

        let fingerprint = envelope.fingerprint
        let descriptor = FetchDescriptor<ClipboardEntry>(
            predicate: #Predicate { $0.fingerprint == fingerprint }
        )
        do {
            if let existing = try modelContext.fetch(descriptor).first {
                existing.refresh(
                    kind: kind,
                    textContent: envelope.textContent,
                    fileName: envelope.fileName,
                    fileURLString: localFileURLString,
                    payloadData: payloadData,
                    utiIdentifier: envelope.utiIdentifier,
                    sourceApplication: "sync · \(envelope.fromDeviceName)"
                )
            } else {
                let entry = ClipboardEntry(
                    kind: kind,
                    textContent: envelope.textContent,
                    fileName: envelope.fileName,
                    fileURLString: localFileURLString,
                    payloadData: payloadData,
                    utiIdentifier: envelope.utiIdentifier,
                    sourceApplication: "sync · \(envelope.fromDeviceName)"
                )
                modelContext.insert(entry)
            }
            try modelContext.save()
        } catch {
            connectionState = "Ingest failed: \(error.localizedDescription)"
        }
    }

    private func readCapped(url: URL) -> Data? {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attrs[.size] as? Int,
              size <= maxPayloadBytes else {
            return nil
        }
        return try? Data(contentsOf: url)
    }
}
