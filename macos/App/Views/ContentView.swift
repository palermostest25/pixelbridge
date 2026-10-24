import AppKit
import PixelBridgeKit
import SwiftUI

enum Page: String, CaseIterable, Identifiable {
    case home, messages, calls, contacts, camera, mic, sensors, compass, location, network, bluetooth, notifications, phone, tools, diagnostics
    var id: String { rawValue }
    var title: String {
        switch self {
        case .messages: "Messages"; case .calls: "Calls"; case .contacts: "Contacts"; case .compass: "Compass"; case .tools: "Phone Tools"
        case .home: "Home"; case .camera: "Camera"; case .mic: "Microphone"; case .sensors: "Motion Sensors"; case .location: "Location"
        case .network: "Internet Sharing"; case .bluetooth: "Bluetooth"; case .notifications: "Notifications"; case .phone: "Phone Screen"
        case .diagnostics: "Diagnostics"
        }
    }
    var subtitle: String {
        switch self {
        case .messages: "Your conversations, close at hand."
        case .calls: "Make room for a proper conversation."
        case .contacts: "The people you want to reach."
        case .compass: "Find your bearings."
        case .tools: "Useful little things, one cable away."
        case .home: "Your phone. A whole new set of possibilities."
        case .camera: "A better view, powered by your phone."
        case .mic: "Bring your phone’s microphone to your Mac."
        case .sensors: "Every movement, in real time."
        case .location: "A clear view of your phone’s position."
        case .network: "Stay connected through one USB cable."
        case .bluetooth: "Explore nearby devices through your phone."
        case .notifications: "A quiet place for updates from your phone."
        case .phone: "Your phone, within reach."
        case .diagnostics: "A closer look at your connection."
        }
    }
    var icon: String {
        switch self {
        case .messages: "message"; case .calls: "phone"; case .contacts: "person.crop.circle"; case .compass: "location.north.circle"; case .tools: "switch.2"
        case .home: "house"; case .camera: "camera"; case .mic: "mic"; case .sensors: "gyroscope"; case .location: "location"
        case .network: "network"; case .bluetooth: "dot.radiowaves.left.and.right"; case .notifications: "bell"; case .phone: "iphone"
        case .diagnostics: "stethoscope"
        }
    }
}

struct ContentView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession
    @Environment(\.openWindow) private var openWindow
    @State private var page: Page? = .home

    var body: some View {
        NavigationSplitView {
            List(selection: $page) {
                Section {
                    Label(Page.home.title, systemImage: Page.home.icon).tag(Page.home)
                }
                Section("Conversations") {
                    ForEach([Page.messages, .calls, .contacts, .notifications]) { p in Label(p.title, systemImage: p.icon).tag(p) }
                }
                Section("Capture") {
                    ForEach([Page.camera, .mic, .sensors, .compass, .location]) { p in Label(p.title, systemImage: p.icon).tag(p) }
                }
                Section("Connect") {
                    ForEach([Page.network, .bluetooth, .phone, .tools]) { p in Label(p.title, systemImage: p.icon).tag(p) }
                }
                Section("Support") { Label(Page.diagnostics.title, systemImage: Page.diagnostics.icon).tag(Page.diagnostics) }
            }
            .listStyle(.sidebar)
            .navigationSplitViewColumnWidth(min: 210, ideal: 232, max: 270)
            .safeAreaInset(edge: .top) {
                VStack(alignment: .leading, spacing: 16) {
                    HStack(spacing: 10) {
                        Image(systemName: "cable.connector.horizontal").font(.title2.weight(.medium)).foregroundStyle(BridgeStyle.accent)
                        Text("PixelBridge").font(.title3.weight(.semibold))
                    }
                    StatusPill()
                }.padding(18)
            }
            .safeAreaInset(edge: .bottom) {
                Label("One cable. More possibilities.", systemImage: "cable.connector")
                    .font(.caption).foregroundStyle(.secondary).padding(18)
            }
        } detail: {
            ScrollView {
                VStack(alignment: .leading, spacing: 28) {
                    VStack(alignment: .leading, spacing: 7) {
                        Text((page ?? .home).title).font(.system(size: 30, weight: .bold, design: .rounded))
                        Text((page ?? .home).subtitle).font(.callout).foregroundStyle(.secondary)
                    }
                    Group {
                        switch page ?? .home {
                        case .messages: MessagesView()
                        case .calls: CallsView()
                        case .contacts: ContactsView()
                        case .compass: CompassView(compass: model.compass)
                        case .tools: PhoneToolsView()
                        case .home: HomeView()
                        case .camera: CameraView()
                        case .mic: MicView()
                        case .sensors: SensorsView()
                        case .location: LocationView()
                        case .network: NetworkView()
                        case .bluetooth: BluetoothView()
                        case .notifications: PhoneNotificationsView()
                        case .phone: PhoneView()
                        case .diagnostics: DiagnosticsView()
                        }
                    }
                }
                .padding(32)
                .frame(maxWidth: 1000, alignment: .leading)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .background(BridgeStyle.canvas)
            .navigationTitle("PixelBridge")
            .toolbar {
                ToolbarItem {
                    Toggle(isOn: Binding(get: { model.privacy }, set: { model.setPrivacy($0) })) { Label("Privacy", systemImage: "eye.slash") }
                        .help("Pause camera, microphone, location and sensors")
                }
            }
        }
        .tint(BridgeStyle.accent)
        .onAppear { BridgeAppDelegate.reopen = { openWindow(id: "dashboard") } }
        .onChange(of: page, initial: true) { _, p in model.sensorsVisible = p == .sensors; model.compassVisible = p == .compass; model.cameraVisible = p == .camera }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didChangeOcclusionStateNotification)) { _ in model.updateSensorDemand() }
        .onDisappear { model.sensorsVisible = false; model.compassVisible = false; model.cameraVisible = false }
    }
}

enum BridgeStyle {
    static let accent = Color(nsColor: NSColor(name: "BridgeAccent") { appearance in
        appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            ? NSColor(srgbRed: 0.45, green: 0.77, blue: 0.64, alpha: 1)
            : NSColor(srgbRed: 0.16, green: 0.43, blue: 0.35, alpha: 1)
    })
    static let canvas = Color(nsColor: .windowBackgroundColor)
    static let surface = Color(nsColor: .controlBackgroundColor)
}

struct StatusPill: View {
    @EnvironmentObject var session: DeviceSession
    @EnvironmentObject var model: AppModel
    var body: some View {
        HStack(spacing: 6) {
            Circle().fill(session.isConnected ? .green : session.state == .connecting ? .orange : .secondary).frame(width: 7, height: 7)
            Text(model.privacy ? "Privacy mode" : session.isConnected ? "\(session.deviceProps["ro.product.model"] ?? "Pixel") connected" : session.state.label)
                .font(.caption.weight(.medium))
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.horizontal, 11).padding(.vertical, 7)
        .background(.quaternary, in: Capsule())
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// Rounded "Settings-style" row: icon, title, subtitle, trailing control.
struct SettingRow<Trailing: View>: View {
    let icon: String, title: String
    var subtitle: String? = nil
    @ViewBuilder var trailing: Trailing
    var body: some View {
        HStack(spacing: 16) {
            Image(systemName: icon).font(.system(size: 18, weight: .medium)).frame(width: 36, height: 36).foregroundStyle(BridgeStyle.accent)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.body.weight(.medium))
                if let subtitle { Text(subtitle).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
            }
            Spacer()
            trailing
        }
        .padding(18)
        .background(BridgeStyle.surface, in: RoundedRectangle(cornerRadius: 14))
    }
}

struct Card<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        content.padding(22).frame(maxWidth: .infinity, alignment: .leading)
            .background(BridgeStyle.surface, in: RoundedRectangle(cornerRadius: 14))
    }
}
