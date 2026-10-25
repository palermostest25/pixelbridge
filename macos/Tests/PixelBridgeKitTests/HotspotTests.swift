import XCTest
@testable import PixelBridgeKit

final class HotspotTests: XCTestCase {
    func testConfirmedState() throws {
        for enabled in [true, false] {
            let state = try HotspotControl.parse("{\"ok\":true,\"supported\":true,\"enabled\":\(enabled)}", success: true)
            XCTAssertTrue(state.supported); XCTAssertEqual(state.enabled, enabled)
        }
    }
    func testFailureIsVisible() {
        XCTAssertThrowsError(try HotspotControl.parse("{\"ok\":false,\"error\":\"Check carrier support\"}", success: false)) { error in
            XCTAssertEqual(error.localizedDescription, "Check carrier support")
        }
    }
    func testMissingStateIsNotOff() {
        XCTAssertThrowsError(try HotspotControl.parse("{\"ok\":true,\"supported\":true}", success: true))
    }
    func testUnsupportedOrMissingHelper() throws {
        XCTAssertFalse(try HotspotControl.parse("{\"ok\":true,\"supported\":false}", success: true).supported)
        XCTAssertThrowsError(try HotspotControl.parse("Class not found", success: false)) { error in
            XCTAssertTrue(error.localizedDescription.contains("Update"))
        }
    }
}
