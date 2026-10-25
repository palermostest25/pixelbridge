import AppKit
import Combine
import PixelBridgeKit
import SwiftUI

@main
struct PixelBridgeApp: App {
    @NSApplicationDelegateAdaptor(BridgeAppDelegate.self) private var delegate

    var body: some Scene {
        Settings {
            SettingsView()
                .environmentObject(delegate.model)
                .environmentObject(delegate.model.session)
        }
        .commands {
            CommandGroup(after: .newItem) {
                Button("Open PixelBridge") { BridgeAppDelegate.showDashboard { BridgeAppDelegate.reopen?() } }
                    .keyboardShortcut("1", modifiers: .command)
                Button("Show call controls") { delegate.showCallMenu() }
                    .keyboardShortcut("c", modifiers: [.command, .shift])
            }
        }
    }
}

/// The bridge and status item start independently of the dashboard. The dashboard
/// is created only when requested, so login never restores a window or Dock icon.
@MainActor final class BridgeAppDelegate: NSObject, NSApplicationDelegate, NSToolbarDelegate {
    static var reopen: (() -> Void)?
    let model = AppModel()
    private var callMenu: CallStatusItem?
    private var dashboard: NSWindow?
    private var privacySubscription: AnyCancellable?
    private static let privacyItem = NSToolbarItem.Identifier("PixelBridge.privacy")
    private var windowObservers: [NSObjectProtocol] = []

    func showCallMenu() { callMenu?.show() }
    func applicationWillTerminate(_ notification: Notification) {
        model.callAudio.stop(); model.silenceCallRingtone(); model.closePhoneScreen()
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApplication.shared.setActivationPolicy(.accessory)
        Self.reopen = { [weak self] in self?.openDashboard() }
        callMenu = CallStatusItem(model: model)
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
        if UserDefaults.standard.object(forKey: "startInMenuBar") as? Bool == false {
            Self.showDashboard { self.openDashboard() }
        }
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        Self.showDashboard { Self.reopen?() }; return true
    }
    private func openDashboard() {
        if dashboard == nil {
            let content = ContentView()
                .environmentObject(model)
                .environmentObject(model.session)
                .environmentObject(model.phone)
                .frame(minWidth: 900, minHeight: 600)
            let window = NSWindow(contentViewController: NSHostingController(rootView: content))
            window.title = "PixelBridge"
            window.identifier = NSUserInterfaceItemIdentifier("dashboard")
            window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
            window.toolbarStyle = .unified
            let toolbar = NSToolbar(identifier: "PixelBridge.dashboard")
            toolbar.delegate = self
            toolbar.displayMode = .iconOnly
            window.toolbar = toolbar
            window.isReleasedWhenClosed = false
            window.setContentSize(NSSize(width: 1120, height: 780))
            window.minSize = NSSize(width: 900, height: 600)
            if !window.setFrameUsingName("PixelBridge.dashboard") { window.center() }
            window.setFrameAutosaveName("PixelBridge.dashboard")
            dashboard = window
        }
        dashboard?.makeKeyAndOrderFront(nil)
    }
    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [.toggleSidebar, .flexibleSpace, Self.privacyItem]
    }
    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [.toggleSidebar, .flexibleSpace, Self.privacyItem]
    }
    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier identifier: NSToolbarItem.Identifier, willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        guard identifier == Self.privacyItem else { return nil }
        let item = NSToolbarItem(itemIdentifier: identifier)
        item.label = "Privacy"; item.toolTip = "Pause capture and access to phone data"
        let button = NSButton(image: NSImage(systemSymbolName: "eye.slash", accessibilityDescription: "Privacy")!, target: self, action: #selector(togglePrivacy))
        button.setButtonType(.pushOnPushOff)
        button.bezelStyle = .texturedRounded
        button.setAccessibilityLabel("Privacy")
        item.view = button
        privacySubscription = model.$privacy.sink { [weak button] enabled in button?.state = enabled ? .on : .off }
        return item
    }
    @objc private func togglePrivacy() { model.setPrivacy(!model.privacy) }
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
