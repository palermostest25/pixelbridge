import PixelBridgeKit
import SwiftUI

struct PhoneToolsView: View {
    @EnvironmentObject var phone: PhoneWorkspaceModel
    @EnvironmentObject var session: DeviceSession
    @EnvironmentObject var model: AppModel
    @State private var selectedApp: String?
    @State private var selectedDownload: String?
    @State private var restarting = false
    @State private var volume = 0.0
    @State private var editingVolume = false
    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            PhoneFeatureNotice()
            VStack(spacing: 0) {
                SettingRow(icon: "flashlight.on.fill", title: "Flashlight", subtitle: "Uses the rear LED. Stop the camera first if Android reports it is busy.") {
                    Toggle("Flashlight", isOn: Binding(get: { phone.tools["flashlight"] as? Bool ?? false }, set: { on in Task { await phone.tool("tools.flashlight", ["enabled": on]) } })).labelsHidden().toggleStyle(.switch)
                }
                SettingRow(icon: "speaker.wave.2", title: "Media volume", subtitle: "Controls the phone’s media output.") {
                    Slider(value: $volume, in: 0...max(1, (phone.tools["maximumVolume"] as? NSNumber)?.doubleValue ?? 15), step: 1, onEditingChanged: { editing in editingVolume = editing; if !editing { Task { await phone.tool("tools.volume", ["value": Int(volume)]) } } }).frame(width: 190).accessibilityLabel("Phone media volume")
                }
                SettingRow(icon: "bell.and.waves.left.and.right", title: "Find phone", subtitle: "Plays the phone’s alarm sound for up to 15 seconds, at its current alarm volume.") {
                    HStack { Button("Ring") { Task { await phone.tool("tools.find", ["enabled": true]) } }; Button("Stop") { Task { await phone.tool("tools.find", ["enabled": false]) } } }
                }
                SettingRow(icon: "iphone.radiowaves.left.and.right", title: "Vibrate", subtitle: "A short vibration to help locate the phone.") { Button("Vibrate") { Task { await phone.tool("tools.vibrate") } } }
            }.disabled(!session.isConnected)
            HStack(alignment: .top, spacing: 24) {
                VStack(alignment: .leading, spacing: 12) {
                    Label("Open an app", systemImage: "square.grid.2x2").font(.headline)
                    Picker("App", selection: $selectedApp) { Text("Choose an app").tag(nil as String?); ForEach(phone.apps) { app in Text(app.text("name")).tag(Optional(app.id)) } }.labelsHidden()
                    HStack { Button("Open on phone") { if let app = phone.apps.first(where: { $0.id == selectedApp }) { Task { await phone.launch(app) } } }.disabled(!session.isConnected || selectedApp == nil); Button("Show screen") { model.openPhoneScreen() }.disabled(!session.isConnected) }
                    Text("App launching and screen controls use this computer’s existing debugging authorization.").font(.caption).foregroundStyle(.secondary)
                }.frame(maxWidth: .infinity, alignment: .leading)
                VStack(alignment: .leading, spacing: 12) {
                    Label("Display & restart", systemImage: "power").font(.headline)
                    HStack { Button("Display off") { Task { await phone.display(false) } }; Button("Wake display") { Task { await phone.display(true) } } }.disabled(!session.isConnected)
                    Button("Restart phone…") { restarting = true }.disabled(!session.isConnected)
                    Text("PixelBridge reconnects after Android starts. Android still requires its first unlock after a reboot.").font(.caption).foregroundStyle(.secondary)
                }.frame(maxWidth: .infinity, alignment: .leading)
            }
            Divider()
            HStack { Label("Phone downloads", systemImage: "folder").font(.headline); Spacer(); Button("Refresh") { Task { await phone.refreshDownloads() } }; Button("Send file…", systemImage: "arrow.up.doc") { Task { await phone.uploadFile() } }.buttonStyle(.borderedProminent) }.disabled(!session.isConnected)
            if phone.downloads.isEmpty { Text("Files in the phone’s Downloads folder appear here. Choose “Send file” to copy a file there over USB.").foregroundStyle(.secondary) }
            else {
                List(phone.downloads, id: \.self, selection: $selectedDownload) { name in HStack { Image(systemName: "doc").foregroundStyle(.secondary); Text(name).lineLimit(1); Spacer(); Button("Save to Mac…") { Task { await phone.downloadFile(name) } }.buttonStyle(.borderless).disabled(!session.isConnected) } }.frame(height: 220)
            }
            if !phone.transferStatus.isEmpty { Text(phone.transferStatus).font(.callout).foregroundStyle(.secondary) }
        }.disabled(model.privacy)
            .onChange(of: (phone.tools["volume"] as? NSNumber)?.doubleValue) { _, value in if !editingVolume, let value { volume = value } }
            .task { await phone.enter("tools"); volume = (phone.tools["volume"] as? NSNumber)?.doubleValue ?? 0 }.onDisappear { phone.leave("tools") }
            .confirmationDialog("Restart the connected phone?", isPresented: $restarting, titleVisibility: .visible) { Button("Restart phone") { Task { await phone.restartPhone() } }; Button("Cancel", role: .cancel) {} } message: { Text("Active calls and transfers will stop. PixelBridge will reconnect when the phone is ready; Android may need its first unlock.") }
    }
}
