import PixelBridgeKit
import SwiftUI
import UniformTypeIdentifiers

// MARK: Internet sharing

struct NetworkView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Card {
                HStack(spacing: 18) {
                    Image(systemName: "network").font(.title).frame(width: 56, height: 56)
                        .foregroundStyle(model.tetherInterface != nil ? .white : .primary)
                        .background(model.tetherInterface != nil ? AnyShapeStyle(.tint) : AnyShapeStyle(.quaternary), in: RoundedRectangle(cornerRadius: 12))
                    VStack(alignment: .leading, spacing: 4) {
                        Text("USB tethering").font(.caption).foregroundStyle(.secondary)
                        Text(model.tetherInterface.map { "On · \($0)" } ?? "Off").font(.title2.weight(.semibold))
                        Text(model.tetherInterface != nil
                             ? "macOS sees the Pixel as a network service. Change its order in System Settings › Network if you want this Mac to prefer it."
                             : "Turn on USB tethering on the phone, or set Developer options › Default USB configuration to USB tethering so it comes on whenever you plug in.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            SettingRow(icon: "gearshape", title: "Tethering and hotspot settings on the phone", subtitle: "Opens Android's Hotspot & tethering screen") {
                Button("Open") { Task { _ = await session.adb?.shell(session.serial, "am start -n com.android.settings/.Settings\\$TetherSettingsActivity") } }
            }
            SettingRow(icon: "cable.connector", title: "USB internet sharing", subtitle: model.tetherMessage.isEmpty ? "The USB connection may reconnect briefly when switching." : model.tetherMessage) {
                Toggle("USB internet sharing", isOn: Binding(get: { model.usbSharingEnabled }, set: { model.setInternetSharing($0) }))
                    .labelsHidden().toggleStyle(.switch).disabled(model.tetherBusy || session.serial.isEmpty)
            }
            Text("Wi-Fi").font(.headline).padding(.top, 8)
            SettingRow(icon: "wifi", title: (model.wifi["ssid"] as? String) ?? ((model.wifi["connected"] as? Bool) == true ? "Connected" : "—"),
                       subtitle: (model.wifi["rssi"] as? Int).map { "\($0) dBm · \(model.wifi["linkMbps"] as? Int ?? 0) Mbps link" }) {
                Button("Scan nearby") { Task { await model.scanWifi() } }.disabled(!session.isConnected)
            }
            if !model.wifiNetworks.isEmpty {
                Card {
                    VStack(alignment: .leading, spacing: 8) {
                        Text(model.radioStatus).font(.caption).foregroundStyle(.secondary)
                        ForEach(model.wifiNetworks) { n in
                            HStack {
                                Image(systemName: n.rssi > -60 ? "wifi" : n.rssi > -75 ? "wifi.exclamationmark" : "wifi.slash")
                                VStack(alignment: .leading) { Text(n.ssid); Text("\(n.rssi) dBm · \(n.frequencyMhz) MHz").font(.caption).foregroundStyle(.secondary) }
                                Spacer()
                                Text(n.band).font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }
                }
            }
            Text("USB tethering is an internet connection, not a Wi-Fi card: macOS can't scan or join networks through it. The phone's hotspot has to be turned on from the phone.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .task { if session.isConnected { await model.refreshWifi() } }
    }
}

// MARK: Bluetooth

struct BluetoothView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession
    @State private var selected: String?
    @State private var hexInput: [String: String] = [:]

    var body: some View {
        HStack(alignment: .top, spacing: 16) {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("Nearby").font(.headline)
                    Spacer()
                    if model.scanning { ProgressView().controlSize(.small) }
                    Button(model.scanning ? "Stop" : "Scan") { Task { model.scanning ? await model.stopBle() : await model.scanBle() } }
                        .buttonStyle(.borderedProminent).disabled(!session.isConnected || model.privacy)
                }
                List(selection: $selected) {
                    Section("Nearby") { ForEach(model.bleDevices) { DeviceRow(d: $0).tag($0.address) } }
                    Section("Paired with the phone") { ForEach(model.bonded) { DeviceRow(d: $0).tag($0.address) } }
                }
                .frame(minHeight: 420)
                if !model.radioStatus.isEmpty { Text(model.radioStatus).font(.caption).foregroundStyle(.secondary) }
            }
            .frame(width: 340)

            Card {
                if let d = (model.bleDevices + model.bonded).first(where: { $0.address == selected }) {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack {
                            VStack(alignment: .leading) { Text(d.title).font(.title3.weight(.semibold)); Text("\(d.address) · \(d.state)").font(.caption).foregroundStyle(.secondary) }
                            Spacer()
                            Button(d.state == "seen" || d.state == "disconnected" || d.state == "failed" ? "Connect" : "Disconnect") {
                                Task { d.state == "seen" || d.state == "disconnected" || d.state == "failed" ? await model.connect(d) : await model.disconnect(d) }
                            }.buttonStyle(.borderedProminent)
                        }
                        ForEach(d.characteristics) { c in
                            VStack(alignment: .leading, spacing: 4) {
                                HStack {
                                    Text(c.uuid).font(.caption.monospaced())
                                    Spacer()
                                    if c.properties.contains("read") { Button("Read") { Task { await model.read(d, c) } } }
                                    if c.properties.contains("notify") || c.properties.contains("indicate") { Button("Notify") { Task { await model.notify(d, c) } } }
                                }
                                Text(c.properties.joined(separator: " ")).font(.caption2).foregroundStyle(.secondary)
                                if !c.value.isEmpty { Text(c.value).font(.caption.monospaced()).foregroundStyle(.tint) }
                                if c.properties.contains("write") {
                                    HStack {
                                        TextField("Hex bytes, e.g. 01 FF", text: Binding(get: { hexInput[c.id] ?? "" }, set: { hexInput[c.id] = $0 }))
                                            .textFieldStyle(.roundedBorder).font(.caption.monospaced())
                                        Button("Write") { Task { await model.write(d, c, hex: hexInput[c.id] ?? "") } }
                                    }
                                }
                                Divider()
                            }
                        }
                    }
                } else {
                    VStack { Image(systemName: "dot.radiowaves.left.and.right").font(.largeTitle).foregroundStyle(.tertiary); Text("Select a device").foregroundStyle(.secondary) }
                        .frame(maxWidth: .infinity, minHeight: 200)
                }
            }
        }
        .task { if session.isConnected && model.bonded.isEmpty { await model.loadBonded() } }
    }
}

struct DeviceRow: View {
    let d: BleDevice
    var body: some View {
        HStack {
            Image(systemName: "dot.radiowaves.left.and.right").frame(width: 28)
            VStack(alignment: .leading) {
                Text(d.title)
                Text(d.rssi != 0 ? "\(d.connectable ? "Connectable" : "Beacon") · \(d.rssi) dBm" : "Paired on the phone").font(.caption).foregroundStyle(.secondary)
            }
        }
    }
}

// MARK: Notifications, phone, settings, diagnostics

struct NotificationsView: View {
    @EnvironmentObject var model: AppModel
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Notifications from the phone appear here once you turn on Notification access for PixelBridge on the phone. By default only the app name is shown, and nothing is saved.")
                .font(.callout).foregroundStyle(.secondary)
            if model.notifications.isEmpty { Text("No notifications yet.").foregroundStyle(.secondary) }
            ForEach(Array(model.notifications.enumerated()), id: \.offset) { _, n in
                SettingRow(icon: "bell", title: n.1, subtitle: n.2) { Text(n.0, style: .time).font(.caption).foregroundStyle(.secondary) }
            }
        }
    }
}

struct PhoneView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession
    @State private var importing = false
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SettingRow(icon: "iphone", title: "Show the phone screen", subtitle: "Open a live, interactive view of the phone on your Mac.") {
                Button("Open phone screen") { model.openPhoneScreen() }.buttonStyle(.borderedProminent).disabled(!session.isConnected)
            }
            if !model.phoneScreenStatus.isEmpty { Text(model.phoneScreenStatus).font(.callout).foregroundStyle(.secondary).textSelection(.enabled) }
            SettingRow(icon: "arrow.up.forward.app", title: "Bring the phone app to the front", subtitle: "Needed after a phone restart before the camera or microphone can start") {
                Button("Bring to front") { Task { _ = await session.adb?.shell(session.serial, "am start -n \(DeviceSession.package)/.MainActivity") } }
            }
            SettingRow(icon: "shippingbox", title: "Install or update the phone app", subtitle: "Mac expects version \(DeviceSession.expectedAppVersion)") {
                Button("Choose APK…") { importing = true }
            }
            SettingRow(icon: "key", title: "Pairing", subtitle: session.pairedAt.map { "Paired \($0.formatted())" } ?? "Not paired") {
                HStack { Button("Pair") { Task { _ = await session.pair() } }; Button("Unpair") { session.unpair() } }
            }
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [UTType(filenameExtension: "apk") ?? .data]) { r in
            if case .success(let url) = r { Task { _ = await session.install(apk: url) } }
        }
    }


}

struct SettingsView: View {
    @EnvironmentObject var session: DeviceSession
    @State private var serial = ""
    @State private var auto = true
    @AppStorage("showOnConnect") private var showOnConnect = true
    @AppStorage("autoUpdateCompanion") private var updateCompanion = true
    var body: some View {
        Form {
            TextField("Phone serial", text: $serial)
            Toggle("Connect automatically when plugged in", isOn: $auto)
            Toggle("Bring PixelBridge forward when the phone is connected", isOn: $showOnConnect)
            Toggle("Update the paired phone companion automatically", isOn: $updateCompanion)
            Text("Updates use the bundled APK and require its signing key to match the existing phone app.").font(.caption).foregroundStyle(.secondary)
            Button("Save") { session.settings.targetSerial = serial; session.settings.autoConnect = auto; session.settings.save() }
        }
        .padding(20).frame(width: 420)
        .onAppear { serial = session.settings.targetSerial; auto = session.settings.autoConnect }
    }
}

struct DiagnosticsView: View {
    @EnvironmentObject var session: DeviceSession
    @State private var lines: [String] = []
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack { Button("Refresh") { lines = Log.recent.suffix(300).map { $0 } }; Spacer() }
            Card {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Link: \(session.state.label) \(session.detail)").font(.caption.monospaced())
                    Text("Other ADB devices (ignored): \(session.otherDevices.map(\.serial).joined(separator: ", "))").font(.caption.monospaced())
                    Divider()
                    ForEach(Array(lines.enumerated()), id: \.offset) { Text($0.element).font(.caption2.monospaced()).textSelection(.enabled) }
                }
            }
        }
        .onAppear { lines = Log.recent.suffix(300).map { $0 } }
    }
}
