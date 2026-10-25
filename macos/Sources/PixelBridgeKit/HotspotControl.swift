import Foundation

public struct HotspotState: Sendable {
    public let supported: Bool
    public let enabled: Bool
}

public enum HotspotControl {
    public static let enableNotice = "Turn on the phone's Wi-Fi hotspot using its existing password and name? This shares its internet connection, may use mobile data, uses extra battery, and can interrupt the current Wi-Fi connection. Android or your carrier may require approval on the phone."

    @MainActor public static func run(_ session: DeviceSession, action: String) async throws -> HotspotState {
        guard ["status", "on", "off"].contains(action) else { throw ProtocolError.malformed("Unknown hotspot action.") }
        guard let adb = session.adb, !session.serial.isEmpty else { throw ProtocolError.malformed("Connect the phone first.") }
        let serial = session.serial
        let result = await adb.shell(serial, "pm path \(DeviceSession.package)", timeout: 5)
        let path = result.stdout.split(separator: "\n").first { $0.hasPrefix("package:") }?.dropFirst(8).trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard result.ok, path.range(of: #"^/[A-Za-z0-9_/.=+~\-]+$"#, options: .regularExpression) != nil else { throw ProtocolError.malformed("Update the phone companion to use hotspot controls.") }
        let command = "CLASSPATH='\(path)' app_process / dev.pixelbridge.app.phone.ShellHotspot \(action)"
        let response = await adb.shell(serial, command, timeout: 35)
        let state = try parse(response.stdout, success: response.ok)
        guard action == "status" || state.supported else { throw ProtocolError.malformed("Direct hotspot controls are unavailable on this Android version. Open Phone settings.") }
        return state
    }

    public static func parse(_ output: String, success: Bool) throws -> HotspotState {
        guard let line = output.split(separator: "\n").last(where: { $0.trimmingCharacters(in: .whitespaces).hasPrefix("{") }),
              let data = line.data(using: .utf8), let json = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw ProtocolError.malformed("Hotspot controls are unavailable. Update the phone app, or open Android hotspot settings.")
        }
        guard success, json["ok"] as? Bool == true else { throw ProtocolError.malformed(json["error"] as? String ?? "Android could not change the hotspot.") }
        let supported = json["supported"] as? Bool ?? false
        guard !supported || json["enabled"] is Bool else { throw ProtocolError.malformed("Android did not report hotspot status. Refresh or open Phone settings.") }
        return HotspotState(supported: supported, enabled: json["enabled"] as? Bool ?? false)
    }
}
