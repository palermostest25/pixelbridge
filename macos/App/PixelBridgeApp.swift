import AppKit
import PixelBridgeKit
import SwiftUI

@main
struct PixelBridgeApp: App {
    @NSApplicationDelegateAdaptor(BridgeAppDelegate.self) private var delegate
    @StateObject private var model = AppModel()

    var body: some Scene {
        WindowGroup("PixelBridge", id: "dashboard") {
            ContentView()
                .environmentObject(model)
                .environmentObject(model.session)
                .environmentObject(model.phone)
                .frame(minWidth: 900, minHeight: 600)
                .onAppear { delegate.installCallMenu(model) }
        }
        .defaultSize(width: 1120, height: 780)
        .windowToolbarStyle(.unified)
        .commands { CommandGroup(after: .newItem) { Button("Show call controls") { delegate.showCallMenu() }.keyboardShortcut("c", modifiers: [.command, .shift]) } }

        Settings { SettingsView().environmentObject(model).environmentObject(model.session) }
    }
}

struct MenuBarContent: View {
    @Environment(\.openWindow) private var openWindow
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession

    var body: some View {
        Text(session.isConnected ? "Connected — \(session.deviceProps["ro.product.model"] ?? "Pixel")" : session.state.label)
        Button("Open PixelBridge") { BridgeAppDelegate.showDashboard { openWindow(id: "dashboard") } }
        Divider()
        Button(model.cameraOn ? "Stop camera" : "Start camera") { model.cameraOn ? model.stopCamera() : model.startCamera() }
            .disabled(!session.isConnected)
        Button(model.micOn ? "Stop microphone" : "Start microphone") { model.micOn ? model.stopMic() : model.startMic() }
            .disabled(!session.isConnected)
        Divider()
        Toggle("USB internet sharing", isOn: Binding(get: { model.usbSharingEnabled }, set: { model.setInternetSharing($0) }))
            .disabled(model.tetherBusy || session.serial.isEmpty || session.adb == nil)
        if !model.tetherMessage.isEmpty { Text(model.tetherMessage) }
        Toggle("Save power (15 fps)", isOn: $model.savePower)
        Toggle("Privacy mode", isOn: Binding(get: { model.privacy }, set: { model.setPrivacy($0) }))
        Divider()
        Button("Quit PixelBridge") { NSApplication.shared.terminate(nil) }.keyboardShortcut("q")
    }
}

/// Closing the dashboard leaves the status item and active bridge alive, without a Dock icon.
@MainActor final class BridgeAppDelegate: NSObject, NSApplicationDelegate {
    static var reopen: (() -> Void)?
    private var callMenu: CallStatusItem?
    private weak var model: AppModel?
    func installCallMenu(_ model: AppModel) { guard callMenu == nil else { return }; self.model = model; callMenu = CallStatusItem(model: model) }
    func showCallMenu() { callMenu?.show() }
    func applicationWillTerminate(_ notification: Notification) { model?.callAudio.stop(); model?.silenceCallRingtone(); model?.closePhoneScreen() }
    private var windowObservers: [NSObjectProtocol] = []
    func applicationDidFinishLaunching(_ notification: Notification) {
        let center = NotificationCenter.default
        windowObservers.append(center.addObserver(forName: NSWindow.willCloseNotification, object: nil, queue: .main) { note in
            guard let closing = note.object as? NSWindow else { return }
            DispatchQueue.main.async {
                guard Self.isDashboard(closing) else { return }
                let remaining = NSApplication.shared.windows.contains { $0 !== closing && Self.isDashboard($0) && $0.isVisible }
                if !remaining { NSApplication.shared.setActivationPolicy(.accessory); Log.info("dashboard closed; running in menu bar") }
            }
        })
        windowObservers.append(center.addObserver(forName: .pixelBridgeShowDashboard, object: nil, queue: .main) { _ in
            Task { @MainActor in Self.showDashboard { Self.reopen?() } }
        })
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        Self.showDashboard { Self.reopen?() }; return true
    }
    private static func isDashboard(_ window: NSWindow) -> Bool {
        window.title == "PixelBridge" || window.identifier?.rawValue.contains("dashboard") == true
    }
    static func showDashboard(_ open: () -> Void) {
        NSApplication.shared.setActivationPolicy(.regular)
        if let existing = NSApplication.shared.windows.first(where: { isDashboard($0) && ($0.isVisible || $0.isMiniaturized) }) {
            if existing.isMiniaturized { existing.deminiaturize(nil) }
            existing.makeKeyAndOrderFront(nil)
        } else { open() }
        NSApplication.shared.activate(ignoringOtherApps: true)
    }
}
extension Notification.Name { static let pixelBridgeShowDashboard = Notification.Name("PixelBridge.showDashboard") }
