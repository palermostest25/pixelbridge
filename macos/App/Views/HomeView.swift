import PixelBridgeKit
import SwiftUI

struct HomeView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            Card {
                HStack(spacing: 24) {
                    RoundedRectangle(cornerRadius: 14).strokeBorder(.secondary.opacity(0.5), lineWidth: 2)
                        .background(RoundedRectangle(cornerRadius: 14).fill(BridgeStyle.canvas))
                        .frame(width: 66, height: 118)
                        .overlay(Image(systemName: session.isConnected ? "checkmark" : "cable.connector").foregroundStyle(session.isConnected ? .green : .secondary))
                    VStack(alignment: .leading, spacing: 6) {
                        Text(session.deviceProps["ro.product.model"] ?? "Your Pixel").font(.system(size: 28, weight: .semibold, design: .rounded))
                        Text(session.isConnected ? "Connected over USB" : "\(session.state.label) · \(session.detail)").foregroundStyle(.secondary)
                        HStack {
                            if let r = session.deviceProps["ro.build.version.release"] { Chip("Android \(r)") }
                            if let b = model.battery, session.isConnected { Chip("\(b.percent)% battery") }
                            if model.tetherInterface != nil { Chip("Sharing internet") }
                        }
                    }
                    Spacer()
                    VStack(alignment: .trailing, spacing: 12) {
                        Button(session.isConnected ? "Disconnect" : "Connect") {
                            Task { session.isConnected ? await session.disconnect() : session.retryNow() }
                        }.buttonStyle(.borderedProminent).controlSize(.large)
                        .disabled(session.state == .connecting)
                        Toggle("Privacy mode", isOn: Binding(get: { model.privacy }, set: { model.setPrivacy($0) })).toggleStyle(.switch)
                    }
                }
            }

            if session.state == .notPaired || session.state == .apkMissing || session.state == .apkOutdated {
                Card {
                    HStack {
                        Image(systemName: "exclamationmark.circle.fill").foregroundStyle(.blue)
                        VStack(alignment: .leading) {
                            Text(session.state == .notPaired ? "Pair this Mac" : "Install the phone app").bold()
                            Text(session.detail).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if session.state == .notPaired { Button("Pair") { Task { _ = await session.pair() } }.buttonStyle(.borderedProminent) }
                    }
                }
            }

            HStack {
                Text("Your workspace").font(.title3.weight(.semibold))
                Spacer()
                Text(model.privacy ? "Paused for privacy" : session.isConnected ? "Ready when you are" : "Connect to get started").font(.callout).foregroundStyle(.secondary)
            }
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 265), spacing: 12)], spacing: 12) {
                Tile(icon: "camera", title: "Camera", state: model.cameraOn ? (model.preview != nil ? "Live · \(Int(model.fps)) fps" : "Starting…") : "Off",
                     active: model.cameraOn, toggle: Binding(get: { model.cameraOn }, set: { $0 ? model.startCamera() : model.stopCamera() }))
                Tile(icon: "mic", title: "Microphone", state: model.micOn ? "Live" : "Off", active: model.micOn,
                     toggle: Binding(get: { model.micOn }, set: { $0 ? model.startMic() : model.stopMic() }))
                Tile(icon: "network", title: "Internet sharing", state: model.tetherInterface.map { "On · \($0)" } ?? "Off", active: model.tetherInterface != nil)
                Tile(icon: "location", title: "Location", state: model.location.map { "±\(Int($0.accM ?? 0)) m" } ?? (model.locationOn ? "Waiting…" : "Off"),
                     active: model.location != nil, toggle: Binding(get: { model.locationOn }, set: { $0 ? model.startLocation() : model.stopLocation() }))
                Tile(icon: "dot.radiowaves.left.and.right", title: "Bluetooth", state: model.scanning ? "Scanning…" : "\(model.bleDevices.count) nearby", active: model.scanning)
                Tile(icon: "gyroscope", title: "Motion sensors", state: model.latest.isEmpty ? "Off" : "Streaming", active: !model.latest.isEmpty)
            }
            .disabled(!session.isConnected || model.privacy)

            HStack(alignment: .top, spacing: 12) {
                Card {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Battery").font(.headline)
                        if let b = model.battery, session.isConnected {
                            HStack(alignment: .lastTextBaseline) {
                                Text("\(b.percent)%").font(.system(size: 34, weight: .semibold))
                                if let t = b.tempC { Text(String(format: "%.1f °C", t)).foregroundStyle(.secondary) }
                            }
                            ProgressView(value: Double(b.percent), total: 100)
                            Text("\(b.charging ? "Charging" : "Not charging") · \(String(format: "%.2f", Double(b.voltageMv) / 1000)) V · health \(b.health)")
                                .font(.caption).foregroundStyle(.secondary)
                        } else { Text("Connect the phone to see battery status.").foregroundStyle(.secondary) }
                        Text("This phone has a history of battery repairs. Keep camera sessions supervised. If it gets hot, swells or smells, unplug it.")
                            .font(.caption).foregroundStyle(.orange)
                    }
                }
                Card {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Connection").font(.headline)
                        Text(session.isConnected ? String(format: "USB · round trip %.1f ms", session.rttMs) : "No active link")
                        ForEach(session.portMap.sorted(by: { $0.key < $1.key }), id: \.key) { k, v in
                            Text("\(k)  127.0.0.1:\(v)").font(.caption.monospaced()).foregroundStyle(.secondary)
                        }
                    }
                }
            }
        }
    }
}

struct Chip: View {
    let text: String
    init(_ t: String) { text = t }
    var body: some View { Text(text).font(.caption).padding(.horizontal, 9).padding(.vertical, 3).background(.quaternary, in: Capsule()) }
}

struct Tile: View {
    let icon: String, title: String, state: String, active: Bool
    var toggle: Binding<Bool>? = nil
    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: icon).font(.title3).frame(width: 40, height: 40)
                .foregroundStyle(active ? .white : .primary)
                .background(active ? AnyShapeStyle(BridgeStyle.accent) : AnyShapeStyle(.quaternary), in: RoundedRectangle(cornerRadius: 9))
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.body.weight(.semibold))
                Text(state).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
            if let toggle { Toggle("", isOn: toggle).toggleStyle(.switch).labelsHidden().accessibilityLabel(title) }
        }
        .padding(18)
        .background(BridgeStyle.surface, in: RoundedRectangle(cornerRadius: 14))
    }
}
