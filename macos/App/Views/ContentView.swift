import PixelBridgeKit
import SwiftUI

enum Page: String, CaseIterable, Identifiable {
    case home, camera, mic, sensors, location, network, bluetooth, notifications, phone, diagnostics
    var id: String { rawValue }
    var title: String {
        switch self {
        case .home: "Home"; case .camera: "Camera"; case .mic: "Microphone"; case .sensors: "Motion Sensors"; case .location: "Location"
        case .network: "Internet Sharing"; case .bluetooth: "Bluetooth"; case .notifications: "Notifications"; case .phone: "Phone Screen"
        case .diagnostics: "Diagnostics"
        }
    }
    var icon: String {
        switch self {
        case .home: "house"; case .camera: "camera"; case .mic: "mic"; case .sensors: "gyroscope"; case .location: "location"
        case .network: "network"; case .bluetooth: "dot.radiowaves.left.and.right"; case .notifications: "bell"; case .phone: "iphone"
        case .diagnostics: "stethoscope"
        }
    }
}

struct ContentView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession
    @State private var page: Page? = .home

    var body: some View {
        NavigationSplitView {
            List(selection: $page) {
                Section {
                    ForEach(Page.allCases.filter { $0 != .diagnostics }) { p in Label(p.title, systemImage: p.icon).tag(p) }
                }
                Section("Support") { Label(Page.diagnostics.title, systemImage: Page.diagnostics.icon).tag(Page.diagnostics) }
            }
            .navigationSplitViewColumnWidth(min: 200, ideal: 220)
            .safeAreaInset(edge: .top) { StatusPill().padding(.horizontal, 12).padding(.bottom, 6) }
        } detail: {
            ScrollView {
                Group {
                    switch page ?? .home {
                    case .home: HomeView()
                    case .camera: CameraView()
                    case .mic: MicView()
                    case .sensors: SensorsView()
                    case .location: LocationView()
                    case .network: NetworkView()
                    case .bluetooth: BluetoothView()
                    case .notifications: NotificationsView()
                    case .phone: PhoneView()
                    case .diagnostics: DiagnosticsView()
                    }
                }
                .padding(28)
                .frame(maxWidth: 1000, alignment: .leading)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .navigationTitle((page ?? .home).title)
            .toolbar {
                ToolbarItem {
                    Toggle(isOn: Binding(get: { model.privacy }, set: { model.setPrivacy($0) })) { Label("Privacy", systemImage: "eye.slash") }
                        .help("Pause camera, microphone, location and sensors")
                }
            }
        }
    }
}

struct StatusPill: View {
    @EnvironmentObject var session: DeviceSession
    @EnvironmentObject var model: AppModel
    var body: some View {
        HStack(spacing: 6) {
            Circle().fill(session.isConnected ? .green : session.state == .connecting ? .orange : .secondary).frame(width: 7, height: 7)
            Text(model.privacy ? "Privacy mode" : session.isConnected ? "\(session.deviceProps["ro.product.model"] ?? "Pixel") connected" : session.state.label)
                .font(.caption)
        }
        .padding(.horizontal, 10).padding(.vertical, 4)
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
        HStack(spacing: 14) {
            Image(systemName: icon).font(.title3).frame(width: 26).foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                if let subtitle { Text(subtitle).font(.caption).foregroundStyle(.secondary) }
            }
            Spacer()
            trailing
        }
        .padding(14)
        .background(.background.secondary, in: RoundedRectangle(cornerRadius: 10))
    }
}

struct Card<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        content.padding(18).frame(maxWidth: .infinity, alignment: .leading)
            .background(.background.secondary, in: RoundedRectangle(cornerRadius: 12))
    }
}
