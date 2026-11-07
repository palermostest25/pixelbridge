import Foundation
import XCTest
@testable import PixelBridgeKit

final class NativeCaptureTests: XCTestCase {
    func testOnlySafeNonemptyCameraOriginals() {
        let rows = """
        Row: 0 _id=17, _display_name=PXL_20261107_120000.jpg, _size=123
        Row: 1 _id=18, _display_name=PXL_20261107_120001.mp4, _size=456
        Row: 2 _id=19, _display_name=PXL_../../other.jpg, _size=789
        Row: 3 _id=20, _display_name=PXL_empty.jpg, _size=0
        Row: 4 _id=21, _display_name=PXL_hidden.exe, _size=1
        """
        let files = NativeCapture.files(rows)
        XCTAssertEqual(files.map(\.id), [17, 18])
        XCTAssertEqual(files.map(\.bytes), [123, 456])
    }

    func testSavingOriginalDoesNotReplaceExistingFile() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: folder) }
        let existing = folder.appendingPathComponent("PXL_test.jpg")
        let temp = folder.appendingPathComponent("pending.part")
        try Data([0]).write(to: existing)
        try Data([1, 2, 3]).write(to: temp)
        let saved = try NativeCapture.save(temp, named: "PXL_test.jpg", in: folder)
        XCTAssertEqual(saved.lastPathComponent, "PXL_test (1).jpg")
        XCTAssertEqual(try Data(contentsOf: existing), Data([0]))
        XCTAssertEqual(try Data(contentsOf: saved), Data([1, 2, 3]))
        XCTAssertFalse(FileManager.default.fileExists(atPath: temp.path))
    }
}
