import AppKit
import Charts
import CoreImage
import MapKit
import PixelBridgeKit
import SceneKit
import SwiftUI

// MARK: Camera

struct PixelBufferView: NSViewRepresentable {
    let buffer: CVPixelBuffer?
    func makeNSView(context: Context) -> NSView {
        let v = NSView(); v.wantsLayer = true
        v.layer?.contentsGravity = .resizeAspect; v.layer?.backgroundColor = NSColor.black.cgColor
        return v
    }
    func updateNSView(_ v: NSView, context: Context) { v.layer?.contents = buffer.map { CVPixelBufferGetIOSurface($0)?.takeUnretainedValue() } ?? nil }
}

struct CameraView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession
    @State private var camera = "0"

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Picker("", selection: $camera) { Text("Rear").tag("0"); Text("Front").tag("1") }
                    .pickerStyle(.segmented).frame(width: 180).labelsHidden()
                    .onChange(of: camera) { _, v in model.switchCamera(v) }
                Spacer()
                Button { model.cameraOn ? model.stopCamera() : model.startCamera() } label: {
                    Label(model.cameraOn ? "Stop camera" : "Start camera", systemImage: model.cameraOn ? "stop.fill" : "play.fill")
                }.buttonStyle(.borderedProminent).disabled(!session.isConnected || model.privacy)
            }
            ZStack(alignment: .topLeading) {
                PixelBufferView(buffer: model.preview).aspectRatio(16 / 9, contentMode: .fit)
                    .clipShape(RoundedRectangle(cornerRadius: 12))
                if model.preview == nil {
                    VStack(spacing: 10) {
                        Image(systemName: "camera").font(.system(size: 36))
                        Text(model.privacy ? "Privacy mode is on" : model.cameraError ?? (model.cameraOn ? "Starting camera…" : "Camera is off"))
                    }.foregroundStyle(.white.opacity(0.7)).frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    Label("LIVE · \(Int(model.fps)) fps", systemImage: "circle.fill").font(.caption.bold()).foregroundStyle(.white)
                        .padding(.horizontal, 10).padding(.vertical, 4).background(.black.opacity(0.6), in: Capsule()).padding(14)
                }
            }
            SettingRow(icon: "video", title: "macOS webcam", subtitle: "Appears as \u{201C}PixelBridge Camera\u{201D} in FaceTime, Zoom, Teams, OBS and browsers. Status: \(model.extensionState)") {
                Button("Install camera") { model.installCameraExtension() }
            }
        }
    }
}

// MARK: Microphone

struct MicView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession
    @State private var devices = AudioPlayer.outputDevices()
    @State private var output: AudioDeviceID?
    @State private var gain: Double = 1
    @State private var muted = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Card {
                VStack(alignment: .leading, spacing: 12) {
                    HStack {
                        Text(model.micOn ? (muted ? "Live, muted" : "Live") : "Microphone is off").font(.title3.weight(.semibold))
                        Spacer()
                        Button(model.micOn ? "Stop microphone" : "Start microphone") { model.micOn ? model.stopMic() : model.startMic() }
                            .buttonStyle(.borderedProminent).disabled(!session.isConnected || model.privacy)
                    }
                    Gauge(value: max(0, min(1, (model.micLevel + 60) / 60))) { EmptyView() }.gaugeStyle(.linearCapacity).tint(.green)
                    Text("48 kHz mono from the Pixel's microphone over USB").font(.caption).foregroundStyle(.secondary)
                }
            }
            SettingRow(icon: "speaker.slash", title: "Mute", subtitle: "Nothing is sent while muted") {
                Toggle("", isOn: $muted).toggleStyle(.switch).labelsHidden().onChange(of: muted) { _, v in model.audio.muted = v }
            }
            SettingRow(icon: "dial.medium", title: "Gain") {
                Slider(value: $gain, in: 0...8).frame(width: 200).onChange(of: gain) { _, v in model.audio.gain = Float(v) }
            }
            if !devices.contains(where: \.isVirtualCable) {
                Label("To use the phone as a microphone in other apps, install BlackHole 2ch (free, existential.audio), then choose it below and pick \u{201C}BlackHole 2ch\u{201D} as the microphone in your app.",
                      systemImage: "exclamationmark.triangle").font(.callout).padding(12).background(.yellow.opacity(0.15), in: RoundedRectangle(cornerRadius: 10))
            }
            SettingRow(icon: "hifispeaker", title: "Send to", subtitle: "BlackHole for apps, or speakers to listen") {
                Picker("", selection: $output) {
                    Text("Don't send").tag(AudioDeviceID?.none)
                    ForEach(devices) { Text($0.name).tag(AudioDeviceID?.some($0.id)) }
                }.frame(width: 240).labelsHidden().onChange(of: output) { _, v in model.audio.setOutput(v) }
            }
        }
    }
}

// MARK: Sensors

struct SensorsView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .top, spacing: 12) {
                Card { PhoneOrientationView(q: model.quaternion).frame(height: 260) }.frame(width: 320)
                VStack(spacing: 10) {
                    SensorChart(title: "Accelerometer (m/s²)", points: model.accel)
                    SensorChart(title: "Gyroscope (rad/s)", points: model.gyro)
                }
            }
            Text("All sensors").font(.headline)
            Card {
                LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], alignment: .leading, spacing: 10) {
                    ForEach(model.latest.keys.sorted(), id: \.self) { t in
                        VStack(alignment: .leading) {
                            Text(SensorType.name(t)).font(.caption).foregroundStyle(.secondary)
                            Text(model.latest[t]!.values.prefix(4).map { String(format: "%7.2f", $0) }.joined(separator: " ") + " " + SensorType.unit(t))
                                .font(.callout.monospaced())
                        }
                    }
                }
            }
        }
    }
}

struct SensorChart: View {
    let title: String, points: [SensorPoint]
    var body: some View {
        Card {
            VStack(alignment: .leading) {
                Text(title).font(.caption).foregroundStyle(.secondary)
                Chart(points) {
                    LineMark(x: .value("t", $0.t), y: .value("x", $0.x), series: .value("axis", "x")).foregroundStyle(.red)
                    LineMark(x: .value("t", $0.t), y: .value("y", $0.y), series: .value("axis", "y")).foregroundStyle(.green)
                    LineMark(x: .value("t", $0.t), y: .value("z", $0.z), series: .value("axis", "z")).foregroundStyle(.blue)
                }
                .chartXAxis(.hidden).frame(height: 90)
            }
        }
    }
}

/// SceneKit phone model rotated by the Android rotation-vector quaternion (device axes X right, Y up, Z out of screen).
struct PhoneOrientationView: NSViewRepresentable {
    let q: (Float, Float, Float, Float)
    func makeNSView(context: Context) -> SCNView {
        let v = SCNView(); v.backgroundColor = .clear; v.autoenablesDefaultLighting = true
        let scene = SCNScene()
        let body = SCNBox(width: 0.72, height: 1.5, length: 0.08, chamferRadius: 0.08)
        let screen = SCNMaterial(); screen.diffuse.contents = NSColor(calibratedWhite: 0.08, alpha: 1)
        let side = SCNMaterial(); side.diffuse.contents = NSColor(calibratedWhite: 0.85, alpha: 1)
        body.materials = [screen, side, side, side, side, side]
        let node = SCNNode(geometry: body); node.name = "phone"
        scene.rootNode.addChildNode(node)
        let cam = SCNNode(); cam.camera = SCNCamera(); cam.position = SCNVector3(0, 0, 3.6)
        scene.rootNode.addChildNode(cam)
        v.scene = scene; v.pointOfView = cam
        return v
    }
    func updateNSView(_ v: SCNView, context: Context) {
        v.scene?.rootNode.childNode(withName: "phone", recursively: false)?.simdOrientation = simd_quatf(ix: q.0, iy: q.1, iz: q.2, r: q.3)
    }
}

// MARK: Location

struct LocationView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession
    @State private var camera: MapCameraPosition = .automatic

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text(model.location.map { $0.provider == "gps" ? "GPS fix" : "Approximate position" } ?? (model.locationOn ? "Waiting for a fix…" : "Location is off"))
                    .font(.title3.weight(.semibold))
                Spacer()
                Button(model.locationOn ? "Stop location" : "Start location") { model.locationOn ? model.stopLocation() : model.startLocation() }
                    .buttonStyle(.borderedProminent).disabled(!session.isConnected || model.privacy)
            }
            Map(position: $camera) {
                if let l = model.location {
                    Annotation("Pixel", coordinate: .init(latitude: l.lat, longitude: l.lon)) {
                        Image(systemName: "iphone.gen3").padding(6).background(.blue, in: Circle()).foregroundStyle(.white)
                    }
                    MapCircle(center: .init(latitude: l.lat, longitude: l.lon), radius: l.accM ?? 50).foregroundStyle(.blue.opacity(0.15))
                }
            }
            .frame(height: 380).clipShape(RoundedRectangle(cornerRadius: 12))
            .onChange(of: model.location) { _, l in
                if let l { camera = .region(MKCoordinateRegion(center: .init(latitude: l.lat, longitude: l.lon), latitudinalMeters: 800, longitudinalMeters: 800)) }
            }
            if let l = model.location {
                HStack(spacing: 10) {
                    Stat("Accuracy", l.accM.map { "±\(Int($0)) m" } ?? "—")
                    Stat("Altitude", l.altM.map { "\(Int($0)) m" } ?? "—")
                    Stat("Speed", l.speedMps.map { String(format: "%.1f km/h", $0 * 3.6) } ?? "—")
                    Stat("Satellites", "\(l.satellitesUsed)/\(l.satellitesVisible)")
                }
            }
            Text("macOS Location Services can't use the phone without a driver. PixelBridge shows the fix here, and location is never logged.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}

struct Stat: View {
    let title: String, value: String
    init(_ t: String, _ v: String) { title = t; value = v }
    var body: some View {
        Card { VStack(alignment: .leading) { Text(title).font(.caption).foregroundStyle(.secondary); Text(value).font(.title3.weight(.semibold)) } }
    }
}
