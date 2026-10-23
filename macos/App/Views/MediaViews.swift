import AppKit
import CoreAudio
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
    private var zoomLabel: String { String(format: "%.1f×", model.zoomRatio) }
    private var canOperate: Bool { session.isConnected && !model.privacy }

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            HStack(spacing: 10) {
                HStack(spacing: 4) {
                    ForEach(model.cameraLenses) { lens in
                        Button { model.selectCameraLens(lens) } label: {
                            VStack(spacing: 5) {
                                Image(systemName: lens.symbol).font(.system(size: 19, weight: .medium))
                                Text(lens.title).font(.caption.weight(.semibold))
                            }
                            .frame(minWidth: 76, minHeight: 53)
                            .foregroundStyle(model.selectedLensID == lens.id ? BridgeStyle.accent : .secondary)
                            .background(model.selectedLensID == lens.id ? BridgeStyle.accent.opacity(0.13) : .clear, in: RoundedRectangle(cornerRadius: 10))
                            .contentShape(RoundedRectangle(cornerRadius: 10))
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(lens.title + " camera")
                        .accessibilityAddTraits(model.selectedLensID == lens.id ? .isSelected : [])
                    }
                }
                .disabled(!canOperate)
                Spacer(minLength: 8)
                Button { model.cameraOn ? model.stopCamera() : model.startCamera() } label: {
                    Label(model.cameraOn ? "Stop camera" : "Start camera", systemImage: model.cameraOn ? "stop.fill" : "play.fill")
                }
                .buttonStyle(.borderedProminent).controlSize(.large).disabled(!canOperate)
            }

            ViewThatFits(in: .horizontal) {
                HStack(alignment: .top, spacing: 24) {
                    previewView.frame(minWidth: 360, maxWidth: .infinity)
                    VStack(alignment: .leading, spacing: 20) { zoomControls; captureControls }.frame(width: 250)
                }
                VStack(alignment: .leading, spacing: 20) { previewView; zoomControls; captureControls }
            }
            Toggle("Save power · 15 fps capture", isOn: $model.savePower).toggleStyle(.switch)
            SettingRow(icon: "video", title: "macOS webcam", subtitle: model.webcamName.map {
                "Choose “\($0)” in your calling app. \(model.webcamStatus)"
            } ?? model.webcamStatus) {
                if model.canInstallCameraExtension {
                    Button("Install camera") { model.installCameraExtension() }
                } else {
                    Button("Refresh") { model.refreshWebcamOutput() }
                }
            }
        }
    }

    private var previewView: some View {
            ZStack(alignment: .topLeading) {
                PixelBufferView(buffer: model.preview)
                    .aspectRatio(16.0 / 9.0, contentMode: .fit)
                    .clipShape(RoundedRectangle(cornerRadius: 16))
                if model.preview == nil {
                    VStack(spacing: 12) {
                        Image(systemName: "camera.aperture").font(.system(size: 38, weight: .light))
                        Text(model.privacy ? "Paused for privacy" : model.cameraError ?? (model.cameraOn ? "Opening the lens…" : "Your next point of view"))
                            .font(.headline)
                        if !model.cameraOn && !model.privacy {
                            Text("Choose a lens, then start the camera.").font(.callout).foregroundStyle(.white.opacity(0.6))
                        }
                    }
                    .foregroundStyle(.white.opacity(0.9)).multilineTextAlignment(.center)
                    .padding(24).frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    HStack(spacing: 7) {
                        Circle().fill(.green).frame(width: 6, height: 6)
                        Text("Live").fontWeight(.semibold)
                        Text("·  \(Int(model.fps)) fps  ·  \(zoomLabel)").monospacedDigit()
                    }
                    .font(.caption).foregroundStyle(.white)
                    .padding(.horizontal, 12).padding(.vertical, 8)
                    .background(.black.opacity(0.65), in: Capsule()).padding(16)
                }
            }

    }
    private var zoomControls: some View {
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .firstTextBaseline) {
                    Label("Zoom", systemImage: "plus.magnifyingglass").font(.headline)
                    Spacer()
                    Text(zoomLabel).font(.system(size: 23, weight: .medium, design: .rounded)).monospacedDigit()
                }
                if let camera = model.currentCamera, camera.maximumZoom > camera.minimumZoom {
                    HStack(spacing: 14) {
                        Text(String(format: "%.1f×", camera.minimumZoom)).font(.caption).foregroundStyle(.secondary)
                        Slider(value: Binding(get: { log2(max(camera.minimumZoom, model.zoomRatio)) }, set: { model.setCameraZoom(pow(2, $0)) }),
                               in: log2(camera.minimumZoom)...log2(camera.maximumZoom))
                            .accessibilityLabel("Camera zoom").accessibilityValue(zoomLabel)
                        Text(String(format: "%.0f×", camera.maximumZoom)).font(.caption).foregroundStyle(.secondary)
                    }
                    HStack(spacing: 8) {
                        ForEach([camera.minimumZoom, 1, 2, 4].filter { $0 >= camera.minimumZoom && $0 <= camera.maximumZoom }.reduce(into: [Double]()) { result, value in
                            if !result.contains(where: { abs($0 - value) < 0.01 }) { result.append(value) }
                        }, id: \.self) { value in
                            Button(String(format: "%.1f×", value)) { model.setCameraZoom(value) }
                                .buttonStyle(.bordered).controlSize(.small)
                                .tint(abs(model.zoomRatio - value) < 0.03 ? BridgeStyle.accent : nil)
                        }
                    }
                } else {
                    Text("Connect the updated phone app to load lens controls.").font(.callout).foregroundStyle(.secondary)
                }
            }
            .disabled(!canOperate)

    }
    private var captureControls: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(alignment: .leading, spacing: 7) {
                Label("Stabilization", systemImage: "hand.raised").font(.headline)
                Picker("Stabilization", selection: $model.stabilization) {
                    Text("Auto").tag("auto")
                    Text("Optical").tag("optical").disabled(model.currentCamera?.supportsOptical != true || model.zoomRatio < 1)
                    Text("Electronic").tag("electronic").disabled(model.currentCamera?.supportsElectronic != true)
                    Text("Off").tag("off")
                }.labelsHidden().frame(maxWidth: .infinity)
                    .onChange(of: model.stabilization) { _, _ in model.sendCameraControls() }
                Text(model.stabilizationStatus).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Divider()
            VStack(alignment: .leading, spacing: 7) {
                Label("Orientation", systemImage: "rotate.right").font(.headline)
                Picker("Orientation", selection: $model.orientationMode) {
                    Text("Auto").tag("auto")
                    Text("0°").tag("0"); Text("90°").tag("90"); Text("180°").tag("180"); Text("270°").tag("270")
                }.labelsHidden().frame(maxWidth: .infinity)
                    .onChange(of: model.orientationMode) { _, _ in model.saveCameraControls() }
                Text("Applied to preview and webcam").font(.caption).foregroundStyle(.secondary)
            }
            Divider()
            Toggle("Mirror video", isOn: $model.mirrorCamera).toggleStyle(.switch)
                .onChange(of: model.mirrorCamera) { _, _ in model.saveCameraControls() }
        }
        .padding(18)
        .background(BridgeStyle.surface, in: RoundedRectangle(cornerRadius: 14))
        .disabled(!canOperate)
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
