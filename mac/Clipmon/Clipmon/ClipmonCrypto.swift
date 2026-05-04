import CryptoKit
import Foundation
import Security

/// AES-256-GCM at-rest crypto for the local SwiftData store.
/// The 32-byte master key lives in the macOS Keychain (kSecClassGenericPassword,
/// kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly) and is auto-generated on first use.
///
/// Wire format identical to the Windows EncryptionService: [12 nonce] [ciphertext] [16 tag].
final class ClipmonCrypto {
    static let shared = ClipmonCrypto()

    private static let service = "com.clipmon.atrest"
    private static let account = "store-key-v1"

    private let key: SymmetricKey

    private init() {
        if let existing = ClipmonCrypto.loadKey() {
            self.key = existing
        } else {
            let fresh = SymmetricKey(size: .bits256)
            ClipmonCrypto.storeKey(fresh)
            self.key = fresh
        }
    }

    func encrypt(_ data: Data) -> Data? {
        do {
            let sealed = try AES.GCM.seal(data, using: key)
            let nonceData: Data = sealed.nonce.withUnsafeBytes { Data($0) }
            var out = Data()
            out.append(nonceData)
            out.append(sealed.ciphertext)
            out.append(sealed.tag)
            return out
        } catch {
            return nil
        }
    }

    func decrypt(_ envelope: Data) -> Data? {
        guard envelope.count >= 12 + 16 else { return nil }
        do {
            let nonce = try AES.GCM.Nonce(data: envelope.prefix(12))
            let tag = envelope.suffix(16)
            let ciphertext = envelope.dropFirst(12).dropLast(16)
            let sealed = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ciphertext, tag: tag)
            return try AES.GCM.open(sealed, using: key)
        } catch {
            return nil
        }
    }

    func encryptString(_ s: String) -> Data? { encrypt(Data(s.utf8)) }

    func decryptString(_ envelope: Data) -> String? {
        guard let data = decrypt(envelope) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    // MARK: Keychain

    private static func loadKey() -> SymmetricKey? {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        guard status == errSecSuccess, let data = item as? Data else { return nil }
        return SymmetricKey(data: data)
    }

    private static func storeKey(_ key: SymmetricKey) {
        let data = key.withUnsafeBytes { Data($0) }
        let attributes: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
            kSecValueData as String: data,
        ]
        // Best-effort: ignore errors (user can be offline / Keychain locked at first run).
        _ = SecItemAdd(attributes as CFDictionary, nil)
    }
}
