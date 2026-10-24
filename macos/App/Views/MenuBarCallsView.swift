import AppKit
import Combine
import PixelBridgeKit
import SwiftUI

/// A real status-item popover so incoming calls can appear at the menu bar while the Dock stays hidden.
@MainActor final class CallStatusItem: NSObject {
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let popover = NSPopover()
    private var bag = Set<AnyCancellable>()
    init(model: AppModel) {
        super.init()
        if let button = item.button { button.image = NSImage(systemSymbolName: "iphone.radiowaves.left.and.right", accessibilityDescription: "PixelBridge calls"); button.target = self; button.action = #selector(toggle) }
        popover.behavior = .transient
        let content = MenuBarCallsView(onOpen: { [weak self] in self?.popover.performClose(nil); BridgeAppDelegate.showDashboard { BridgeAppDelegate.reopen?() } })
            .environmentObject(model).environmentObject(model.session).environmentObject(model.phone)
        let hosting = NSHostingController(rootView: content)
        hosting.sizingOptions = [.preferredContentSize]
        popover.contentViewController = hosting
        model.$ringingCallID.removeDuplicates().sink { [weak self] id in
            guard id != nil else { return }; self?.show()
        }.store(in: &bag)
        model.session.$state.sink { [weak self] state in
            self?.item.button?.image = NSImage(systemSymbolName: state == .connected ? "iphone.radiowaves.left.and.right" : "iphone.slash", accessibilityDescription: "PixelBridge calls")
        }.store(in: &bag)
    }
    @objc private func toggle() { if popover.isShown { popover.performClose(nil) } else { show() } }
    func show() { guard let button = item.button else { return }; popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY); NSApplication.shared.activate(ignoringOtherApps: true); popover.contentViewController?.view.window?.makeKey() }
}

private struct MenuBarCallsView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession
    @EnvironmentObject var phone: PhoneWorkspaceModel
    @State private var number = ""
    @State private var accountID = ""
    @State private var keypad = false
    @State private var dialing = false
    let onOpen: () -> Void
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Image(systemName: "phone.fill").foregroundStyle(BridgeStyle.accent)
                Text("PixelBridge").font(.headline)
                Spacer()
                Text(model.privacy ? "Private" : session.isConnected ? "Pixel connected" : "Disconnected").font(.caption).foregroundStyle(.secondary)
            }
            Divider()
            if !phone.error.isEmpty { Text(phone.error).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
            if phone.calls.isEmpty { Text("Make a call").font(.title3.weight(.semibold)) }
            ForEach(phone.calls) { call in
                VStack(alignment: .leading, spacing: 12) {
                    Text(call.text("state") == "ringing" ? "Incoming call" : call.text("state").capitalized).font(.caption.weight(.medium)).foregroundStyle(BridgeStyle.accent)
                    Text(call.text("number")).font(.title2.weight(.semibold)).textSelection(.enabled)
                    HStack {
                        if call.text("state") == "ringing" {
                            Button("Answer", systemImage: "phone.fill") { action("phone.answer", call) }.buttonStyle(.borderedProminent)
                            Button("Silence", systemImage: "bell.slash") { model.silenceCallRingtone() }
                        } else {
                            Button(phone.muted ? "Unmute" : "Mute", systemImage: phone.muted ? "mic.slash.fill" : "mic.fill") { Task { await phone.callAction("phone.mute", args: ["enabled": !phone.muted]) } }
                            if call.flag("canHold") { Button(call.text("state") == "held" ? "Resume" : "Hold") { action(call.text("state") == "held" ? "phone.unhold" : "phone.hold", call) } }
                        }
                        Spacer(minLength: 0)
                        Button(call.text("state") == "ringing" ? "Decline" : "End", systemImage: "phone.down.fill", role: .destructive) { action("phone.end", call) }
                    }.disabled(!phone.dialer || model.privacy || !session.isConnected)
                }.padding(.vertical, 4)
            }
            if phone.calls.isEmpty {
                HStack(spacing: 10) {
                    TextField("Phone number", text: $number).textFieldStyle(.roundedBorder).onSubmit { dial() }
                    Button { dial() } label: { Image(systemName: "phone.fill").padding(.vertical, 3) }.buttonStyle(.borderedProminent).accessibilityLabel("Call number")
                        .disabled(number.trimmingCharacters(in: .whitespaces).isEmpty || dialing || !session.isConnected || model.privacy)
                }
                if phone.accounts.count > 1 { Picker("Call using", selection: $accountID) { Text("Choose SIM").tag(""); ForEach(phone.accounts) { Text($0.text("name")).tag($0.id) } } }
            }
            DisclosureGroup("Keypad", isExpanded: $keypad) {
                LazyVGrid(columns: Array(repeating: GridItem(.flexible()), count: 3), spacing: 7) {
                    ForEach(Array("123456789*0#"), id: \.self) { digit in
                        Button {
                            if let call = phone.calls.first(where: { $0.text("state") == "active" }) { Task { await phone.callAction("phone.dtmf", call: call, args: ["digit": String(digit)]) } }
                            else { number += String(digit) }
                        } label: { Text(String(digit)).font(.title3).frame(maxWidth: .infinity, minHeight: 30) }.buttonStyle(.bordered)
                    }
                }.padding(.top, 10)
            }.disabled(model.privacy)
            USBCallAudioControls(audio: model.callAudio)
            Divider()
            HStack {
                Button("Open PixelBridge", action: onOpen)
                Spacer()
                Menu {
                    Button(model.cameraOn ? "Stop camera" : "Start camera") { model.cameraOn ? model.stopCamera() : model.startCamera() }.disabled(!session.isConnected)
                    Button(model.micOn ? "Stop phone microphone" : "Start phone microphone") { model.micOn ? model.stopMic() : model.startMic() }.disabled(!session.isConnected)
                    Toggle("Save power (15 fps)", isOn: $model.savePower)
                    Divider()
                    Button("Quit PixelBridge") { NSApplication.shared.terminate(nil) }
                } label: { Image(systemName: "ellipsis.circle") }.menuStyle(.borderlessButton).fixedSize().accessibilityLabel("More PixelBridge controls")
            }
            Toggle("USB internet sharing", isOn: Binding(get: { model.usbSharingEnabled }, set: { model.setInternetSharing($0) })).disabled(model.tetherBusy || session.serial.isEmpty)
            if !model.tetherMessage.isEmpty { Text(model.tetherMessage).font(.caption).foregroundStyle(.secondary) }
            Toggle("Privacy mode", isOn: Binding(get: { model.privacy }, set: { model.setPrivacy($0) }))
        }.padding(20).frame(width: 380).fixedSize(horizontal: false, vertical: true).tint(BridgeStyle.accent)
            .task { await phone.refreshCallStatus(); accountID = phone.defaultAccountID }
            .onChange(of: session.isConnected) { _, connected in if connected { Task { await phone.refreshCallStatus(); accountID = phone.defaultAccountID } } }
    }
    private func action(_ operation: String, _ call: PhoneEntry) { Task { await phone.callAction(operation, call: call) } }
    private func dial() {
        guard !dialing, session.isConnected, !model.privacy, !number.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        dialing = true
        Task { await phone.call(number, accountID: accountID); try? await Task.sleep(nanoseconds: 1_500_000_000); dialing = false }
    }
}
