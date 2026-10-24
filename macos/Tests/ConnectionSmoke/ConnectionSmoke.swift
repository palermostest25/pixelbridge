import Foundation
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
            if name == "none" { require(session.detail.contains("USB data cable"), "show a useful recovery instruction") }
            await session.shutdown()
        }
        print("PASS: 15 device-selection and discovery checks; no real phone commands sent.")
    }

    static func require(_ condition: @autoclosure () -> Bool, _ message: String) {
        guard condition() else { fatalError("FAIL: " + message) }
    }
}
