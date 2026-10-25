import AppKit
import PixelBridgeKit
import SwiftUI

struct AdbKeyImportControl: View {
    @EnvironmentObject var session: DeviceSession
    @EnvironmentObject var model: AppModel
    @State private var plan: AdbKeyImport?
    @State private var confirming = false
    @State private var busy = false
    @State private var message = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SettingRow(icon: "key.horizontal", title: "ADB keys from another computer", subtitle: "Choose adbkey with adbkey.pub beside it. Keep copies of both files from the computer your phone already trusts.") {
                Button(busy ? "Importing…" : "Import ADB keys…", action: choose).disabled(busy || !model.phone.calls.isEmpty)
            }
            if !message.isEmpty { Text(message).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
        }
        .alert("Import & reconnect?", isPresented: $confirming) {
            Button("Cancel", role: .cancel) { plan = nil }
            Button("Import & reconnect") { Task { await apply() } }
        } message: {
            Text("Fingerprint: \(plan?.fingerprint ?? "")\n\nCurrent keys will be backed up. Restarting ADB briefly disconnects all ADB apps, including screen mirroring. The phone must already trust these keys, or you must approve its USB-debugging prompt.")
        }
    }
    private func choose() {
        let panel = NSOpenPanel(); panel.title = "Choose adbkey (adbkey.pub must be beside it)"
        panel.canChooseDirectories = false; panel.canChooseFiles = true; panel.allowsMultipleSelection = false; panel.showsHiddenFiles = true
        panel.begin { result in
            guard result == .OK, let url = panel.url else { return }
            do { plan = try AdbKeyImport.prepare(url); confirming = true; message = "" }
            catch { message = error.localizedDescription }
        }
    }
    private func apply() async {
        guard let plan, let adb = session.adb else { message = "Install or locate ADB first."; return }
        busy = true; defer { busy = false; self.plan = nil }
        let auto = session.settings.autoConnect
        await session.disconnect()
        defer { session.settings.autoConnect = auto; if auto { session.retryNow() } }
        do {
            _ = try plan.install()
            let stopped = await adb.run(["kill-server"])
            guard stopped.ok else { message = "Keys were saved, but ADB could not restart. Restart ADB manually."; return }
            let started = await adb.run(["start-server"])
            message = started.ok ? "ADB keys imported. Previous keys are backed up in .android/pixelbridge-key-backups." : "Keys imported. Restart ADB manually to reconnect."
        } catch { message = error.localizedDescription }
    }
}
