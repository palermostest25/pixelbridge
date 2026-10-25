import Foundation
import Security
import CryptoKit

/// Key contents stay in memory until the user confirms the import; never logged.
public struct AdbKeyImport {
    private let secret: Data, pub: Data
    public let fingerprint: String
    private static func failure(_ text: String) -> NSError { NSError(domain: "PixelBridge.Keys", code: 1, userInfo: [NSLocalizedDescriptionKey: text]) }
    public static func prepare(_ selected: URL) throws -> AdbKeyImport {
        let key = selected.pathExtension == "pub" ? selected.deletingPathExtension() : selected
        func read(_ url: URL) throws -> Data {
            guard let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= 65536 else { throw failure("Choose adbkey with its matching adbkey.pub beside it.") }
            return try Data(contentsOf: url)
        }
        let secret = try read(key), pub = try read(URL(fileURLWithPath: key.path + ".pub"))
        guard let pem = String(data: secret, encoding: .utf8) else { throw failure("Choose an unencrypted ADB private key.") }
        let rsaPEM = pem.contains("-----BEGIN RSA PRIVATE KEY-----")
        guard rsaPEM || pem.contains("-----BEGIN PRIVATE KEY-----"), let encoded = Data(base64Encoded: pem.split(separator: "\n").filter { !$0.hasPrefix("-----") }.joined(), options: .ignoreUnknownCharacters) else { throw failure("Choose an unencrypted RSA ADB private key.") }
        var der = encoded
        if !rsaPEM {
            var outer = DERReader(encoded); var fields = DERReader(try outer.next(0x30))
            _ = try fields.next(0x02); _ = try fields.next(0x30); der = try fields.next(0x04)
        }
        guard let privateKey = SecKeyCreateWithData(der as CFData, [kSecAttrKeyType: kSecAttrKeyTypeRSA, kSecAttrKeyClass: kSecAttrKeyClassPrivate, kSecAttrKeySizeInBits: 2048] as CFDictionary, nil),
              let publicKey = SecKeyCopyPublicKey(privateKey), let actual = SecKeyCopyExternalRepresentation(publicKey, nil) as Data?,
              let token = String(data: pub, encoding: .utf8)?.split(whereSeparator: \.isWhitespace).first,
              let blob = Data(base64Encoded: String(token)), blob.count == 524, Array(blob.prefix(4)) == [64, 0, 0, 0] else { throw failure("These files are not a matching RSA-2048 ADB key pair.") }
        func tlv(_ tag: UInt8, _ data: Data) -> Data { Data([tag] + (data.count < 128 ? [UInt8(data.count)] : data.count < 256 ? [0x81, UInt8(data.count)] : [0x82, UInt8(data.count >> 8), UInt8(data.count & 255)])) + data }
        func integer(_ bytes: [UInt8]) -> Data { var value = Array(bytes.drop(while: { $0 == 0 })); if value.isEmpty { value = [0] }; if value[0] & 128 != 0 { value.insert(0, at: 0) }; return tlv(0x02, Data(value)) }
        let expected = tlv(0x30, integer(Array(blob[8..<264].reversed())) + integer(Array(blob[520..<524].reversed())))
        guard actual == expected else { throw failure("adbkey and adbkey.pub do not match. Copy both from the same computer.") }
        return AdbKeyImport(secret: secret, pub: pub, fingerprint: SHA256.hash(data: actual).map { String(format: "%02X", $0) }.joined())
    }
    public func install(directory: URL? = nil) throws -> URL {
        let fm = FileManager.default
        let directory = directory ?? fm.homeDirectoryForCurrentUser.appendingPathComponent(".android")
        if !fm.fileExists(atPath: directory.path) { try fm.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700]) }
        let names = ["adbkey", "adbkey.pub"]
        for name in names { if (try? fm.attributesOfItem(atPath: directory.appendingPathComponent(name).path)[.type] as? FileAttributeType) == .typeSymbolicLink { throw Self.failure("Existing ADB keys are symbolic links. Manage those keys at their original location.") } }
        let root = directory.appendingPathComponent("pixelbridge-key-backups")
        try fm.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: root.path)
        let backup = root.appendingPathComponent(UUID().uuidString)
        try fm.createDirectory(at: backup, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        let originals = try names.map { name -> Data? in let url = directory.appendingPathComponent(name); return fm.fileExists(atPath: url.path) ? try Data(contentsOf: url) : nil }
        for index in 0..<2 {
            if let old = originals[index] { try old.write(to: backup.appendingPathComponent(names[index])); try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: backup.appendingPathComponent(names[index]).path) }
            let staged = backup.appendingPathComponent("new-" + names[index]); try [secret, pub][index].write(to: staged)
            try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: staged.path)
        }
        var installed = 0
        do {
            for name in names { let source = backup.appendingPathComponent("new-" + name), target = directory.appendingPathComponent(name); guard rename(source.path, target.path) == 0 else { throw Self.failure("Could not save the imported keys. Existing keys were restored.") }; installed += 1 }
        } catch {
            for index in 0..<installed { let target = directory.appendingPathComponent(names[index]); if let old = originals[index] { try old.write(to: target, options: .atomic); try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: target.path) } else { try fm.removeItem(at: target) } }
            throw error
        }
        return backup
    }
}

private struct DERReader {
    private let bytes: [UInt8]; private var index = 0
    init(_ data: Data) { bytes = Array(data) }
    mutating func next(_ tag: UInt8) throws -> Data {
        func invalid() -> NSError { NSError(domain: "PixelBridge.Keys", code: 1, userInfo: [NSLocalizedDescriptionKey: "The private key format is invalid."]) }
        guard index + 2 <= bytes.count, bytes[index] == tag else { throw invalid() }; index += 1
        var count = Int(bytes[index]); index += 1
        if count & 128 != 0 { let length = count & 127; guard length > 0, length <= 3, index + length <= bytes.count else { throw invalid() }; count = 0; for _ in 0..<length { count = (count << 8) | Int(bytes[index]); index += 1 } }
        guard index + count <= bytes.count else { throw invalid() }; defer { index += count }; return Data(bytes[index..<(index + count)])
    }
}
