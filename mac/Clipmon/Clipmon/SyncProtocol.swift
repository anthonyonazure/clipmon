import CryptoKit
import Foundation

/// Wire-format constants and helpers shared with the Windows client.
/// IMPORTANT: keep aligned with `SyncProtocol.cs`.
enum SyncProtocol {
    static let keySalt = "clipmon-key-v1"
    static let roomSalt = "clipmon-room-v1"
    static let pbkdf2Iterations = 200_000
    static let keySizeBytes = 32 // AES-256
    static let nonceBytes = 12
    static let tagBytes = 16

    /// 32-byte AES-256 key derived from the pairing code.
    /// Uses HKDF over SHA-256(pairingCode || salt) instead of PBKDF2 because CommonCrypto's
    /// PBKDF2 is verbose; HKDF is the same cost-class for a single-use key derivation here.
    /// To stay compatible with the Windows side we instead implement PBKDF2-HMAC-SHA256 manually.
    static func deriveKey(pairingCode: String) throws -> SymmetricKey {
        guard !pairingCode.isEmpty else { throw SyncError.missingPairingCode }
        let password = Data(pairingCode.utf8)
        let salt = Data(keySalt.utf8)
        let bytes = pbkdf2HmacSha256(password: password, salt: salt, iterations: pbkdf2Iterations, keyLength: keySizeBytes)
        return SymmetricKey(data: bytes)
    }

    static func deriveRoomId(pairingCode: String) throws -> String {
        guard !pairingCode.isEmpty else { throw SyncError.missingPairingCode }
        let combined = Data((pairingCode + ":" + roomSalt).utf8)
        let digest = SHA256.hash(data: combined)
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    static func encryptEnvelope(key: SymmetricKey, plaintextJson: String) throws -> String {
        let plaintext = Data(plaintextJson.utf8)
        let sealed = try AES.GCM.seal(plaintext, using: key)
        // Wire format: [12 nonce] [ciphertext] [16 tag]  (matches AesGcm in .NET)
        let nonceData: Data = sealed.nonce.withUnsafeBytes { Data($0) }
        var envelope = Data()
        envelope.append(nonceData)
        envelope.append(sealed.ciphertext)
        envelope.append(sealed.tag)
        return envelope.base64EncodedString()
    }

    static func decryptEnvelope(key: SymmetricKey, envelopeBase64: String) throws -> String {
        guard let envelope = Data(base64Encoded: envelopeBase64) else {
            throw SyncError.cryptoFailure
        }
        guard envelope.count >= nonceBytes + tagBytes else {
            throw SyncError.cryptoFailure
        }
        let nonceData = envelope.prefix(nonceBytes)
        let tag = envelope.suffix(tagBytes)
        let ciphertext = envelope.dropFirst(nonceBytes).dropLast(tagBytes)

        let nonce = try AES.GCM.Nonce(data: nonceData)
        let sealed = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ciphertext, tag: tag)
        let plaintext = try AES.GCM.open(sealed, using: key)
        return String(data: plaintext, encoding: .utf8) ?? ""
    }

    enum SyncError: Error {
        case missingPairingCode
        case cryptoFailure
    }
}

// MARK: - PBKDF2 (CommonCrypto)

import CommonCrypto

private func pbkdf2HmacSha256(password: Data, salt: Data, iterations: Int, keyLength: Int) -> Data {
    var derived = Data(count: keyLength)
    let result = derived.withUnsafeMutableBytes { derivedBytes -> Int32 in
        password.withUnsafeBytes { passwordBytes -> Int32 in
            salt.withUnsafeBytes { saltBytes -> Int32 in
                CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2),
                    passwordBytes.bindMemory(to: Int8.self).baseAddress,
                    password.count,
                    saltBytes.bindMemory(to: UInt8.self).baseAddress,
                    salt.count,
                    CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256),
                    UInt32(iterations),
                    derivedBytes.bindMemory(to: UInt8.self).baseAddress,
                    keyLength
                )
            }
        }
    }
    if result != kCCSuccess {
        return Data(count: keyLength) // zero key on failure; caller will fail to decrypt
    }
    return derived
}

// MARK: - Wire envelope

/// Wire-compatible with `SyncEnvelope` on Windows. Field names use camelCase in JSON.
struct SyncEnvelopeWire: Codable {
    var fingerprint: String
    var kind: String
    var textContent: String?
    var fileName: String?
    var fileUrl: String?
    var payloadDataBase64: String?
    var utiIdentifier: String?
    var createdAt: Date
    var updatedAt: Date
    var isPinned: Bool
    var sourceApplication: String?
    var fromDeviceId: String
    var fromDeviceName: String
}
