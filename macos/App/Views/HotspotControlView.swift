import PixelBridgeKit
import SwiftUI

struct HotspotControlView: View {
    @EnvironmentObject var session: DeviceSession
    @EnvironmentObject var model: AppModel
    @State private var status: HotspotState?
    @State private var busy = false
    @State private var confirming = false
    @State private var message = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SettingRow(icon: "personalhotspot", title: "Wi-Fi hotspot", subtitle: subtitle) {
                HStack {
                    Button(busy ? "Checking…" : status?.enabled == true ? "Turn off hotspot" : "Turn on hotspot…") {
                        if status?.enabled == true { Task { await run("off") } } else { confirming = true }
                    }.disabled(busy || !session.isConnected || status?.supported != true)
                    Button { Task { await run("status") } } label: { Image(systemName: "arrow.clockwise") }
                        .accessibilityLabel("Refresh hotspot status").disabled(busy || !session.isConnected)
                    Button("Phone settings") {
                        Task {
                            _ = await session.adb?.shell(session.serial, "am start -n com.android.settings/.Settings\\$TetherSettingsActivity")
                            model.openPhoneScreen()
                        }
                    }.disabled(busy || !session.isConnected)
                }
            }
            if !message.isEmpty { Text(message).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
        }
        .alert("Turn on Wi-Fi hotspot?", isPresented: $confirming) {
            Button("Cancel", role: .cancel) { }
            Button("Turn on hotspot") { Task { await run("on") } }
        } message: { Text(HotspotControl.enableNotice) }
        .task(id: session.isConnected) { if session.isConnected { await run("status") } else { status = nil } }
    }

    private var subtitle: String {
        guard session.isConnected else { return "Connect the phone to control its hotspot." }
        guard let status else { return "Checking Android hotspot controls…" }
        guard status.supported else { return "Use Phone settings on this Android version. Direct controls require Android 16 or newer and authorized debugging." }
        return status.enabled ? "On · Uses the phone’s existing hotspot name and password." : "Off · Share the phone’s internet over Wi-Fi using its saved hotspot settings."
    }

    private func run(_ action: String) async {
        guard !busy else { return }
        busy = true; message = ""
        defer { busy = false }
        do {
            status = try await HotspotControl.run(session, action: action)
            if action != "status" { message = status?.enabled == true ? "Android confirmed: hotspot is on." : "Android confirmed: hotspot is off." }
        } catch { status = nil; message = error.localizedDescription }
    }
}
