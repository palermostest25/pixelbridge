import XCTest
@testable import PixelBridgeKit

final class WirelessTests: XCTestCase {
    func testEndpointValidation() {
        for good in ["192.168.1.2:5555", "10.20.0.2:37123", "172.16.0.2:65535", "172.31.255.2:1"] {
            XCTAssertTrue(WirelessAdb.validEndpoint(good), good)
        }
        for bad in ["", ":5555", "172.32.0.2:5555", "8.8.8.8:5555", "127.0.0.1:5555", "192.168.1.2:0", "192.168.1.2:65536", "192.168.1.2:5555;echo hi", "192.168.1.256:5555", "192.168.1.2"] {
            XCTAssertFalse(WirelessAdb.validEndpoint(bad), bad)
        }
        XCTAssertTrue(WirelessAdb.validCode("012345"))
        for bad in ["12345", "1234567", "１２３４５６", "12 456"] { XCTAssertFalse(WirelessAdb.validCode(bad)) }
    }

    func testOnlySelectWifiAddress() {
        XCTAssertEqual(WirelessAdb.wifiAddress("1: lo inet 127.0.0.1/8 scope host\n2: rmnet0 inet 10.12.0.1/24 scope global\n3: wlan0 inet 192.168.1.20/24 brd 192.168.1.255 scope global wlan0\n"), "192.168.1.20")
        XCTAssertNil(WirelessAdb.wifiAddress("2: wlan0 inet 8.8.8.8/24 scope global\n"))
    }

    func testSavedTransportSelection() {
        let devices = AdbClient.parseDevices("phone device\n192.168.1.20:5555 device\nother device\n")
        XCTAssertEqual(WirelessAdb.selection(configured: "phone", endpoint: "192.168.1.20:5555", identity: "phone", devices: devices), "192.168.1.20:5555")
        XCTAssertEqual(WirelessAdb.selection(configured: "other", endpoint: "192.168.1.20:5555", identity: "phone", devices: devices), "other")
        XCTAssertEqual(WirelessAdb.selection(configured: "", endpoint: "192.168.1.20:5555", identity: "phone", devices: devices.filter { !$0.serial.contains(":") }), "phone")
        XCTAssertEqual(WirelessAdb.selection(configured: "", endpoint: "192.168.1.20:5555", identity: "phone", devices: devices.filter { $0.serial == "other" }), "192.168.1.20:5555")
    }

    func testMdnsNeverGuessesBetweenPhones() {
        let rows = "adb-phone-abc _adb-tls-pairing._tcp 192.168.1.20:34567\nadb-phone-abc _adb-tls-connect._tcp 192.168.1.20:45678\nadb-other-xyz _adb-tls-connect._tcp 192.168.1.21:23456\n"
        let found = WirelessAdb.discover(rows, host: "192.168.1.20")
        XCTAssertEqual(found?.endpoint, "192.168.1.20:45678")
        XCTAssertEqual(found?.service, "adb-phone-abc._adb-tls-connect._tcp")
        XCTAssertEqual(WirelessAdb.discover(rows, identity: "phone")?.endpoint, found?.endpoint)
        XCTAssertNil(WirelessAdb.discover(rows)); XCTAssertNil(WirelessAdb.discover(rows, identity: "phon"))
    }
}
