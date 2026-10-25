import Foundation

/// Explicit USB-authorized Wi-Fi setup; no LAN scanning or Wi-Fi changes.
public enum WirelessAdb {
    public static let consent = "Connect both devices to the same trusted Wi-Fi network. This enables Android's network debugging on port 5555. Computers holding an ADB key your phone trusts can control it. This mode is not encrypted; use it only on a private network. Setup briefly interrupts calls and screen mirroring. Use ‘Turn off wireless’ to stop the phone's network listener. After a phone restart or address change, connect USB and set up again."

    public static func isUsbSerial(_ serial: String) -> Bool {
        !serial.isEmpty && !serial.contains(":") && !serial.hasPrefix("adb-") && !serial.hasPrefix("emulator-")
    }

    public static func isPrivateAddress(_ value: String) -> Bool {
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4, parts.allSatisfy({ !$0.isEmpty && $0.allSatisfy(\.isASCII) && $0.allSatisfy(\.isNumber) }),
              parts.allSatisfy({ $0.count <= 3 && Int($0).map { $0 <= 255 } == true }) else { return false }
        let b = parts.map { Int($0)! }
        return b[0] == 10 || b[0] == 172 && (16...31).contains(b[1]) || b[0] == 192 && b[1] == 168
    }

    public static func validEndpoint(_ value: String) -> Bool {
        let parts = value.split(separator: ":", omittingEmptySubsequences: false)
        guard parts.count == 2, isPrivateAddress(String(parts[0])), let port = Int(parts[1]), parts[1].utf8.allSatisfy({ (48...57).contains($0) }) else { return false }
        return (1...65535).contains(port)
    }

    public static func wifiAddress(_ output: String) -> String? {
        for line in output.split(separator: "\n") {
            guard let r = line.range(of: #"^\d+:\s+wlan\d+\s+inet\s+(\d+\.\d+\.\d+\.\d+)/\d+\b"#, options: .regularExpression) else { continue }
            let words = line[r].split(whereSeparator: \.isWhitespace)
            guard words.count >= 4 else { continue }
            let ip = String(words[3].split(separator: "/")[0])
            if isPrivateAddress(ip) { return ip }
        }
        return nil
    }

    public static func tlsPort(_ output: String) -> Int? {
        let text = output.trimmingCharacters(in: .whitespacesAndNewlines)
        if let port = Int(text), (1...65535).contains(port) { return port }
        guard let match = text.range(of: #"(?m)^\s*tls_port=(\d+)\s*$"#, options: .regularExpression),
              let port = Int(text[match].split(separator: "=").last!.trimmingCharacters(in: .whitespacesAndNewlines)), (1...65535).contains(port) else { return nil }
        return port
    }

    public static func readTlsPort(_ adb: AdbClient, serial: String) async -> Int? {
        if let port = tlsPort(await adb.getprop(serial, "service.adb.tls.port")) { return port }
        // New Android releases report the current port through the ADB service dump.
        return tlsPort(await adb.shell(serial, "dumpsys adb | grep -E '^ *tls_port='", timeout: 5).stdout)
    }

    public static func validCode(_ code: String) -> Bool { code.count == 6 && code.utf8.allSatisfy { (48...57).contains($0) } }

    public static func discover(_ output: String, host: String? = nil, identity: String? = nil) -> (endpoint: String, service: String)? {
        var found: [(String, String)] = []
        for line in output.split(separator: "\n") {
            let parts = line.split(whereSeparator: \.isWhitespace).map(String.init)
            guard parts.count == 3, parts[1].trimmingCharacters(in: CharacterSet(charactersIn: ".")) == "_adb-tls-connect._tcp", validEndpoint(parts[2]) else { continue }
            if let host, parts[2].split(separator: ":").first.map(String.init) != host { continue }
            if let identity, !parts[0].hasPrefix("adb-" + identity + "-") { continue }
            let service = parts[0].contains("._adb-tls-connect._tcp") ? parts[0] : parts[0] + "._adb-tls-connect._tcp"
            found.append((parts[2], service))
        }
        return found.count == 1 ? found[0] : nil
    }

    public static func selection(configured: String, endpoint: String, identity: String, devices: [AdbDevice], service: String = "") -> String {
        guard validEndpoint(endpoint), !identity.isEmpty, configured.isEmpty || configured == identity || configured == endpoint || configured == service else { return configured }
        if devices.contains(where: { $0.serial == endpoint && $0.state == "device" }) { return endpoint }
        if !service.isEmpty, devices.contains(where: { $0.serial == service && $0.state == "device" }) { return service }
        if devices.contains(where: { $0.serial == identity }) { return identity }
        return endpoint
    }
}
