import Foundation
import Combine
import PixelBridgeKit

@main
struct ConnectionSmoke {
    @MainActor static func main() async throws {
        let rows = AdbClient.parseDevices("List of devices attached\none unauthorized usb:1\nemulator-5554 device model:Emulator\n")
        require(AdbClient.selectTarget(rows, configuredSerial: "") == .selected(rows[0]), "select one physical phone automatically")
        require(AdbClient.selectTarget(Array(rows.suffix(1)), configuredSerial: "") == .missing, "ignore emulators during auto detection")
        require(AdbClient.selectTarget([], configuredSerial: "") == .missing, "handle no USB devices")
        let two = AdbClient.parseDevices("first device model:Pixel_7\nsecond device model:Pixel_7\n")
        require(AdbClient.selectTarget(two, configuredSerial: "") == .multiple, "never choose arbitrarily between phones")
        require(AdbClient.selectTarget(two, configuredSerial: "missing") == .missing, "never fall back from a configured phone")
        require(AdbClient.selectTarget(two, configuredSerial: " second ") == .selected(two[1]), "match the trimmed configured serial")

        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        for (name, output, code, expected, serial) in [
            ("single", "List of devices attached\nphone-1 unauthorized usb:1\n", 0, LinkState.unauthorized, "phone-1"),
            ("none", "List of devices attached\n", 0, .disconnected, ""),
            ("multiple", "first device model:Pixel_7\nsecond device model:Pixel_7\n", 0, .wrongDevice, ""),
            ("failed", "USB discovery fixture failed\n", 1, .error, "")
        ] {
            let executable = directory.appendingPathComponent(name)
            let script = "#!/bin/sh\nif [ \"$1\" != devices ]; then exit 99; fi\ncat <<'FIXTURE'\n" + output + "FIXTURE\nexit \(code)\n"
            try script.write(to: executable, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
            var settings = BridgeSettings()
            settings.adbPath = executable.path
            let session = DeviceSession(settings: settings)
            session.start()
            for _ in 0..<60 {
                if !session.detail.isEmpty { break }
                try await Task.sleep(nanoseconds: 50_000_000)
            }
            require(session.state == expected, "session state for \(name): \(session.state), \(session.detail)")
            require(session.serial == serial, "resolved serial for \(name)")
            if name == "none" { require(session.detail.contains("Connect USB"), "show a useful recovery instruction") }
            await session.shutdown()
        }
        let bootFixture = directory.appendingPathComponent("boot-fixture")
        let counter = directory.appendingPathComponent("boot-count").path
        let bootScript = """
        #!/bin/sh
        if [ "$1" = devices ]; then echo 'phone-boot device model:Pixel_7'; exit 0; fi
        case "$*" in
          *'echo pb-ok'*) echo pb-ok ;;
          *'getprop sys.boot_completed'*)
            n=0; [ ! -f '\(counter)' ] || n=$(cat '\(counter)')
            n=$((n+1)); echo "$n" > '\(counter)'
            if [ "$n" -lt 2 ]; then echo 0; else echo 1; fi ;;
          *'am get-current-user'*) echo 0 ;;
          *'am get-started-user-state 0'*)
            n=$(cat '\(counter)'); if [ "$n" -lt 3 ]; then echo RUNNING_LOCKED; else echo RUNNING_UNLOCKED; fi ;;
          *'getprop ro.product.device'*) echo fixture-unapproved ;;
          *'getprop '*) echo fixture ;;
          *) exit 99 ;;
        esac
        """
        try bootScript.write(to: bootFixture, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: bootFixture.path)
        var bootSettings = BridgeSettings(); bootSettings.adbPath = bootFixture.path
        let recovery = DeviceSession(settings: bootSettings)
        var attached = 0; var details = [String]()
        let attachment = recovery.phoneAttached.sink { attached += 1 }
        let detail = recovery.$detail.sink { details.append($0) }
        recovery.start()
        for _ in 0..<200 {
            if recovery.state == .wrongDevice { break }
            try await Task.sleep(nanoseconds: 50_000_000)
        }
        require(details.contains { $0.contains("Android is starting") }, "wait for Android boot without manual retry")
        require(details.contains { $0.contains("Unlock Android once") }, "wait for first unlock without manual retry")
        require(recovery.state == .wrongDevice, "continue device validation automatically after unlock")
        require(attached == 1, "bring dashboard forward once per physical attachment, not every boot retry")
        await recovery.shutdown()
        withExtendedLifetime((attachment, detail)) {}
        let wirelessFixture = directory.appendingPathComponent("wireless-fixture")
        let wirelessScript = """
        #!/bin/sh
        if [ "$1" = devices ]; then echo '192.168.1.20:5555 device'; exit 0; fi
        case "$*" in
          *'getprop ro.serialno'*) echo another-phone ;;
          *) exit 99 ;;
        esac
        """
        try wirelessScript.write(to: wirelessFixture, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: wirelessFixture.path)
        var wirelessSettings = BridgeSettings(); wirelessSettings.adbPath = wirelessFixture.path
        wirelessSettings.targetSerial = "phone"; wirelessSettings.wirelessSerial = "phone"; wirelessSettings.wirelessEndpoint = "192.168.1.20:5555"
        let wireless = DeviceSession(settings: wirelessSettings)
        wireless.start()
        for _ in 0..<80 {
            if wireless.state == .wrongDevice { break }
            try await Task.sleep(nanoseconds: 50_000_000)
        }
        require(wireless.state == .wrongDevice && wireless.detail.contains("does not identify your phone"), "reject reused wireless address before app launch or pairing")
        await wireless.shutdown()
        require(WirelessAdb.validEndpoint("10.0.0.2:37123"), "accept a private TLS endpoint")
        require(!WirelessAdb.validEndpoint(":5555") && !WirelessAdb.validEndpoint("8.8.8.8:5555"), "reject invalid/public endpoints")
        print("PASS: device-selection, discovery, boot recovery and wireless identity checks; no real phone commands sent.")
    }

    static func require(_ condition: @autoclosure () -> Bool, _ message: String) {
        guard condition() else { fatalError("FAIL: " + message) }
    }
}
