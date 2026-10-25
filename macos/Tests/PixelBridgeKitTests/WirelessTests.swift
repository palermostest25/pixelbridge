import XCTest
@testable import PixelBridgeKit

final class WirelessTests: XCTestCase {
    func testEndpointValidation() {
        XCTAssertEqual(ProtocolError.malformed("Reconnect USB to turn off wireless.").localizedDescription, "Reconnect USB to turn off wireless.")
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

    @MainActor func testUsbSetupAndListenerShutdown() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let executable = directory.appendingPathComponent("adb-fixture")
        let marker = directory.appendingPathComponent("enabled").path
        let commands = directory.appendingPathComponent("commands").path
        let discovery = directory.appendingPathComponent("discovery").path
        let script = """
        #!/bin/sh
        printf '%s\\n' "$*" >> '\(commands)'
        if [ "$1" = devices ]; then
          echo 'phone device'
          if [ ! -f '\(discovery)' ]; then touch '\(discovery)'; sleep 0.6; exit 0; fi
          if [ -f '\(marker)' ]; then echo '192.168.1.20:5555 device'; fi
          exit 0
        fi
        case "$*" in
          *'getprop ro.serialno'*) echo phone ;;
          *'ip -4 -o addr show'*) echo '3: wlan0 inet 192.168.1.20/24 scope global wlan0' ;;
          *'tcpip 5555'*) touch '\(marker)'; echo restarting ;;
          *' usb') rm -f '\(marker)'; echo restarting ;;
          connect*) echo connected ;;
          disconnect*) echo disconnected ;;
          *) exit 99 ;;
        esac
        """
        try script.write(to: executable, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        var settings = BridgeSettings(); settings.adbPath = executable.path; settings.targetSerial = "phone"; settings.autoConnect = false
        var saved: [BridgeSettings] = []
        let session = DeviceSession(settings: settings, saveSettings: { saved.append($0) })
        session.start()
        for _ in 0..<80 { if FileManager.default.fileExists(atPath: discovery) { break }; try await Task.sleep(nanoseconds: 20_000_000) }
        let enabled = try await session.enableWireless()
        try await Task.sleep(nanoseconds: 800_000_000) // A stale discovery cannot reset the Wi-Fi transport.
        XCTAssertTrue(enabled.contains("verified")); XCTAssertEqual(session.serial, "192.168.1.20:5555")
        XCTAssertEqual(session.settings.wirelessSerial, "phone"); XCTAssertEqual(session.transportLabel, "Wi-Fi")
        let disabled = try await session.disableWireless()
        XCTAssertTrue(disabled.contains("off")); XCTAssertTrue(session.wirelessEndpoint.isEmpty)
        XCTAssertEqual(saved.count, 2); XCTAssertTrue(saved.allSatisfy { !$0.autoConnect })
        let log = try String(contentsOfFile: commands, encoding: .utf8)
        XCTAssertTrue(log.contains("-s phone tcpip 5555")); XCTAssertTrue(log.contains("-s phone usb"))
        XCTAssertFalse(log.contains("kill-server")); XCTAssertFalse(log.contains("am start"))
        await session.shutdown()
    }

    @MainActor func testTlsPairingAndDisconnect() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let executable = directory.appendingPathComponent("adb-fixture")
        let script = """
        #!/bin/sh
        case "$*" in
          pair*) echo 'Successfully paired to 192.168.1.20:34567' ;;
          'mdns services') echo 'adb-phone-abc _adb-tls-connect._tcp 192.168.1.20:45678' ;;
          *'getprop ro.serialno'*) echo phone ;;
          *'getprop service.adb.tls.port'*) echo 45678 ;;
          connect*) echo connected ;;
          disconnect*) echo disconnected ;;
          *) exit 99 ;;
        esac
        """
        try script.write(to: executable, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        var settings = BridgeSettings(); settings.adbPath = executable.path; settings.autoConnect = false
        var saved: [BridgeSettings] = []
        let session = DeviceSession(settings: settings, saveSettings: { saved.append($0) })
        let paired = try await session.pairWireless(address: "192.168.1.20:34567", code: "012345", connectAddress: "")
        XCTAssertTrue(paired.contains("Encrypted")); XCTAssertTrue(session.wirelessSecure)
        XCTAssertEqual(session.wirelessEndpoint, "192.168.1.20:45678"); XCTAssertEqual(session.settings.targetSerial, "phone")
        let encoded = try JSONEncoder().encode(session.settings)
        XCTAssertFalse(String(decoding: encoded, as: UTF8.self).contains("012345"))
        let disconnected = try await session.disableWireless()
        XCTAssertTrue(disconnected.contains("removed")); XCTAssertTrue(session.wirelessEndpoint.isEmpty)
        XCTAssertEqual(saved.count, 2); XCTAssertTrue(saved.allSatisfy { !$0.autoConnect })
        await session.shutdown()
    }

}
