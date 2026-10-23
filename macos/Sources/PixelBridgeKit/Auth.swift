import CryptoKit
import Foundation
import Security

/// HMAC-SHA256 handshake primitives (protocol section 3). Must match AuthCrypto.kt / AuthCrypto.cs.
public enum AuthCrypto {
    public static func nonce() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes).base64EncodedString()
    }

    public static func clientMac(key: Data, channel: String, serverNonce: String, clientNonce: String) -> Data {
        hmac(key, "PB-AUTH-v1|\(channel)|\(serverNonce)|\(clientNonce)")
    }

    public static func serverMac(key: Data, channel: String, serverNonce: String, clientNonce: String) -> Data {
        hmac(key, "PB-AUTH-OK-v1|\(channel)|\(serverNonce)|\(clientNonce)")
    }

    /// Constant-time comparison.
    public static func equal(_ a: Data, _ b: Data) -> Bool {
        guard a.count == b.count else { return false }
        var diff: UInt8 = 0
        for i in 0..<a.count { diff |= a[a.startIndex + i] ^ b[b.startIndex + i] }
        return diff == 0
    }

    private static func hmac(_ key: Data, _ message: String) -> Data {
        Data(HMAC<SHA256>.authenticationCode(for: Data(message.utf8), using: SymmetricKey(data: key)))
    }
}

/// Pairing keys in the macOS login Keychain (the counterpart of DPAPI on Windows).
public struct PairingStore {
    private let service = "dev.pixelbridge.pairing"
    public init() {}

    public func load(serial: String) -> Data? {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: serial, kSecReturnData as String: true]
        var out: CFTypeRef?
        return SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess ? out as? Data : nil
    }

    public func createAndSave(serial: String) throws -> Data {
        var bytes = [UInt8](repeating: 0, count: 32)
        guard SecRandomCopyBytes(kSecRandomDefault, 32, &bytes) == errSecSuccess else { throw ProtocolError.malformed("no randomness") }
        let key = Data(bytes)
        delete(serial: serial)
        let add: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                  kSecAttrAccount as String: serial, kSecValueData as String: key,
                                  kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
                                  kSecAttrLabel as String: "PixelBridge pairing (\(serial))"]
        let status = SecItemAdd(add as CFDictionary, nil)
        guard status == errSecSuccess else { throw ProtocolError.malformed("Keychain error \(status)") }
        UserDefaults.standard.set(Date(), forKey: "pairedAt.\(serial)")
        return key
    }

    public func pairedAt(serial: String) -> Date? { UserDefaults.standard.object(forKey: "pairedAt.\(serial)") as? Date }

    public func delete(serial: String) {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: serial]
        SecItemDelete(q as CFDictionary)
        UserDefaults.standard.removeObject(forKey: "pairedAt.\(serial)")
    }
}
