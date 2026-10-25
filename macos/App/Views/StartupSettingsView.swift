import AppKit
import ServiceManagement
import SwiftUI

struct StartupSettingsView: View {
    @AppStorage("startInMenuBar") private var startInMenuBar = true
    @State private var status = SMAppService.mainApp.status
    @State private var busy = false
    @State private var error = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Toggle("Launch at login", isOn: Binding(
                get: { status == .enabled || status == .requiresApproval },
                set: { enabled in Task { await setEnabled(enabled) } }
            )).disabled(busy)
            Toggle("Start in the menu bar", isOn: $startInMenuBar)
            Text("Connect in the background after you sign in to your Mac. Open the main window from the menu-bar icon.")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            if status == .requiresApproval {
                Text("macOS needs approval in Login Items before PixelBridge can launch automatically.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Button("Open Login Items…") { SMAppService.openSystemSettingsLoginItems() }
            }
            if !error.isEmpty { Text(error).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
        }
        .onAppear { status = SMAppService.mainApp.status }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in status = SMAppService.mainApp.status }
    }

    @MainActor private func setEnabled(_ enabled: Bool) async {
        guard !busy else { return }
        busy = true; error = ""
        defer { busy = false; status = SMAppService.mainApp.status }
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try await SMAppService.mainApp.unregister() }
        } catch { self.error = "Could not change launch at login: \(error.localizedDescription)" }
    }
}
