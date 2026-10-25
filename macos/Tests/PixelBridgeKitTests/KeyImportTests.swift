import XCTest
import Security
@testable import PixelBridgeKit

final class AdbKeyImportTests: XCTestCase {
    private func fixture(_ root: URL, name: String) throws -> URL {
        let key = try XCTUnwrap(SecKeyCreateRandomKey([kSecAttrKeyType: kSecAttrKeyTypeRSA, kSecAttrKeySizeInBits: 2048] as CFDictionary, nil))
        let secret = try XCTUnwrap(SecKeyCopyExternalRepresentation(key, nil) as Data?)
        let pub = try XCTUnwrap(SecKeyCopyExternalRepresentation(try XCTUnwrap(SecKeyCopyPublicKey(key)), nil) as Data?)
        XCTAssertEqual(pub.count, 270)
        var blob = Data(repeating: 0, count: 524); blob[0] = 64
        blob.replaceSubrange(8..<264, with: pub[9..<265].reversed()); blob[520] = 1; blob[522] = 1
        let url = root.appendingPathComponent(name)
        try ("-----BEGIN RSA PRIVATE KEY-----\n" + secret.base64EncodedString(options: .lineLength64Characters) + "\n-----END RSA PRIVATE KEY-----\n").write(to: url, atomically: true, encoding: .utf8)
        try (blob.base64EncodedString() + " test@fixture\n").write(toFile: url.path + ".pub", atomically: true, encoding: .utf8)
        return url
    }
    func testMatchingPairCanBePreparedFromEitherFile() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700]); defer { try? FileManager.default.removeItem(at: root) }
        let source = try fixture(root, name: "source")
        let plan = try AdbKeyImport.prepare(source)
        XCTAssertEqual(plan.fingerprint, try AdbKeyImport.prepare(URL(fileURLWithPath: source.path + ".pub")).fingerprint)
        let destination = root.appendingPathComponent("destination"); try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
        try Data("old-private".utf8).write(to: destination.appendingPathComponent("adbkey")); try Data("old-public".utf8).write(to: destination.appendingPathComponent("adbkey.pub"))
        let backup = try plan.install(directory: destination)
        XCTAssertEqual(try String(contentsOf: backup.appendingPathComponent("adbkey"), encoding: .utf8), "old-private")
        XCTAssertEqual(try String(contentsOf: backup.appendingPathComponent("adbkey.pub"), encoding: .utf8), "old-public")
        XCTAssertEqual(try AdbKeyImport.prepare(destination.appendingPathComponent("adbkey")).fingerprint, plan.fingerprint)
        XCTAssertEqual((try FileManager.default.attributesOfItem(atPath: destination.appendingPathComponent("adbkey").path)[.posixPermissions] as? NSNumber)?.intValue, 0o600)
    }
    func testMismatchedPairIsRejectedBeforeAnyImport() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700]); defer { try? FileManager.default.removeItem(at: root) }
        let first = try fixture(root, name: "first"), second = try fixture(root, name: "second")
        try Data(contentsOf: URL(fileURLWithPath: second.path + ".pub")).write(to: URL(fileURLWithPath: first.path + ".pub"))
        XCTAssertThrowsError(try AdbKeyImport.prepare(first))
        XCTAssertFalse(FileManager.default.fileExists(atPath: root.appendingPathComponent("pixelbridge-key-backups").path))
    }
}
