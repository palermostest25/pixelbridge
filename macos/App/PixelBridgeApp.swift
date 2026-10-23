import PixelBridgeKit
import SwiftUI

@main
struct PixelBridgeApp: App {
    @StateObject private var model = AppModel()

    var body: some Scene {
        WindowGroup("PixelBridge") {
            ContentView()
                .environmentObject(model)
                .environmentObject(model.session)
                .frame(minWidth: 900, minHeight: 600)
        }
        .windowToolbarStyle(.unified)

        MenuBarExtra {
            MenuBarContent().environmentObject(model).environmentObject(model.session)
        } label: {
            Image(systemName: model.session.isConnected ? "iphone.radiowaves.left.and.right" : "iphone.slash")
        }

        Settings { SettingsView().environmentObject(model).environmentObject(model.session) }
    }
}

struct MenuBarContent: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession

    var body: some View {
        Text(session.isConnected ? "Connected — \(session.deviceProps["ro.product.model"] ?? "Pixel")" : session.state.label)
        Divider()
        Button(model.cameraOn ? "Stop camera" : "Start camera") { model.cameraOn ? model.stopCamera() : model.startCamera() }
            .disabled(!session.isConnected)
        Button(model.micOn ? "Stop microphone" : "Start microphone") { model.micOn ? model.stopMic() : model.startMic() }
            .disabled(!session.isConnected)
        Toggle("Privacy mode", isOn: Binding(get: { model.privacy }, set: { model.setPrivacy($0) }))
        Divider()
        Button("Quit PixelBridge") { NSApplication.shared.terminate(nil) }.keyboardShortcut("q")
    }
}
