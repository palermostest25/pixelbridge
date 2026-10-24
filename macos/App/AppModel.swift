import AppKit
import Combine
import CoreMedia
import CoreVideo
import Foundation
import PixelBridgeKit
import SystemExtensions
import Security
import SystemConfiguration

struct BleDevice: Identifiable, Hashable {
    var id: String { address }
    let address: String
    var name: String?
    var rssi: Int
    var connectable: Bool
    var state = "seen"
    var characteristics: [GattCharacteristic] = []
    var title: String { (name?.isEmpty == false ? name : nil) ?? "Unnamed device" }
}

struct GattCharacteristic: Identifiable, Hashable {
    var id: String { service + "/" + uuid }
    let service: String, uuid: String, properties: [String]
    var value = ""
}

struct WifiNetwork: Identifiable, Hashable {
    var id: String { bssid + ssid }
    let ssid: String, bssid: String, rssi: Int, frequencyMhz: Int, security: String
    var band: String { frequencyMhz >= 5925 ? "6 GHz" : frequencyMhz >= 4900 ? "5 GHz" : "2.4 GHz" }
}

struct SensorPoint: Identifiable { let id: Int; let t: Double; let x: Float, y: Float, z: Float }

@MainActor final class VideoPreviewState: ObservableObject { @Published var buffer: CVPixelBuffer? }

/// All UI state for the macOS app; mirrors windows/PixelBridge.App/AppController.cs.
@MainActor
final class AppModel: NSObject, ObservableObject {
    let session = DeviceSession()
    lazy var phone = PhoneWorkspaceModel(session: session)
    let compass = CompassModel()
    let callAudio = USBCallAudio()
    @Published private(set) var ringingCallID: String?
    private var callRingtone: NSSound?
    private let decoder = H264Decoder()
    private let sink = CameraSink()
    private let frameProcessor = VideoFrameProcessor()
    let audio = AudioPlayer()
    private var bag = Set<AnyCancellable>()

    let videoPreview = VideoPreviewState()
    var preview: CVPixelBuffer? { get { videoPreview.buffer } set { videoPreview.buffer = newValue } }
    @Published var fps: Double = 0
    @Published var savePower = UserDefaults.standard.object(forKey: "savePower") as? Bool ?? true { didSet { UserDefaults.standard.set(savePower, forKey: "savePower"); if cameraOn { startCamera() } } }
    @Published var cameraOn = false
    @Published var micOn = false
    @Published var locationOn = false
    @Published var privacy = false
    @Published var cameraError: String?
    @Published var battery: BatteryStatus?
    @Published var latest: [Int32: SensorSample] = [:]
    @Published var accel: [SensorPoint] = []
    @Published var gyro: [SensorPoint] = []
    @Published var quaternion: (Float, Float, Float, Float) = (0, 0, 0, 1)
    @Published var location: LocationFix?
    @Published var bleDevices: [BleDevice] = []
    @Published var bonded: [BleDevice] = []
    @Published var scanning = false
    @Published var wifi: [String: Any] = [:]
    @Published var wifiNetworks: [WifiNetwork] = []
    @Published var radioStatus = ""
    @Published var notifications: [(Date, String, String?)] = []
    @Published var extensionState = "Not installed"
    @Published var webcamName: String?
    @Published var webcamStatus = ""

    var canInstallCameraExtension: Bool {
        guard let task = SecTaskCreateFromSelf(nil),
              let allowed = SecTaskCopyValueForEntitlement(task, "com.apple.developer.system-extension.install" as CFString, nil) as? Bool,
              allowed else { return false }
        return FileManager.default.fileExists(atPath: Bundle.main.bundlePath + "/Contents/Library/SystemExtensions/dev.pixelbridge.PixelBridgeCamera.systemextension")
    }

    func refreshWebcamOutput() {
        webcamName = CameraSink.availableOutputName()
        webcamStatus = webcamName == nil ? "A signed camera extension is required. Install and enable OBS Virtual Camera, then refresh." : "Ready. Start the camera to send video."
    }
    @Published var tetherInterface: String?
    @Published var micLevel: Double = -120

    @Published var cameras: [PhoneCamera] = []
    @Published var zoomRatio = UserDefaults.standard.double(forKey: "cameraZoom") == 0 ? 1 : UserDefaults.standard.double(forKey: "cameraZoom")
    @Published var stabilization = UserDefaults.standard.string(forKey: "cameraStabilization") ?? "auto"
    @Published var orientationMode = UserDefaults.standard.string(forKey: "cameraOrientation") ?? "auto"
    @Published var mirrorCamera = UserDefaults.standard.bool(forKey: "cameraMirror")
    @Published var appliedOptical: Bool?
    @Published var appliedElectronic: Bool?
    @Published var appliedZoom: Double?
    @Published var usbSharingEnabled = false
    @Published var tetherBusy = false
    private var tetherStatusQueryBusy = false
    @Published var tetherMessage = ""
    private var automaticRotation = 0
    private var cameraControlTask: Task<Void, Never>?

    var currentCamera: PhoneCamera? { cameras.first { $0.id == session.settings.cameraId } }
    var cameraLenses: [CameraLens] {
        cameras.flatMap { camera -> [CameraLens] in
            if camera.facing == "front" { return [CameraLens(cameraID: camera.id, kind: "front", title: "Front", symbol: "person.crop.rectangle", zoom: 1)] }
            var lenses: [CameraLens] = []
            if camera.minimumZoom < 0.95 {
                lenses.append(CameraLens(cameraID: camera.id, kind: "ultra", title: "Ultra wide", symbol: "camera.viewfinder", zoom: camera.minimumZoom))
            }
            lenses.append(CameraLens(cameraID: camera.id, kind: "wide", title: "Wide", symbol: "camera.aperture", zoom: 1))
            return lenses
        }
    }
    var selectedLensID: String {
        session.settings.cameraId + "-" + (currentCamera?.facing == "front" ? "front" : zoomRatio < 0.95 ? "ultra" : "wide")
    }
    var stabilizationStatus: String {
        if !cameraOn { return "Applies when the camera starts" }
        if appliedOptical == true { return "Optical stabilization active" }
        if appliedElectronic == true { return "Electronic stabilization active" }
        if appliedOptical != nil { return "Stabilization off" }
        return "Waiting for camera confirmation…"
    }

    private var frameCount = 0, lastFpsTick = Date()
    private var sampleIndex = 0
    private var lastPreview = Date.distantPast
    var compassVisible = false { didSet { updateSensorDemand() } }
    var sensorsVisible = false { didSet { updateSensorDemand() } }
    var cameraVisible = false
    private var sensorUIUpdates: [Int32: Date] = [:]
    private var windowVisible: Bool { NSApplication.shared.windows.contains { $0.title == "PixelBridge" && $0.isVisible && !$0.isMiniaturized && $0.occlusionState.contains(.visible) } }
    func updateSensorDemand() {
        if (sensorsVisible || compassVisible) && windowVisible && !privacy { session.startStream("sensors", sensorsVisible ? ["rateHz": session.settings.sensorRateHz] : ["rateHz": 10, "types": [1, 2, 9, 11]]) }
        else { session.stopStream("sensors") }
    }

    override init() {
        super.init()
        refreshWebcamOutput()
        decoder.onFrame = { [weak self] pb, pts in
            Task { @MainActor in self?.onDecoded(pb, pts) }
        }
        session.$capabilities.sink { [weak self] capabilities in
            guard let self else { return }
            self.cameras = (capabilities["cameras"] as? [[String: Any]] ?? []).compactMap(PhoneCamera.init)
            if let camera = self.currentCamera { self.zoomRatio = min(camera.maximumZoom, max(camera.minimumZoom, self.zoomRatio)) }
        }.store(in: &bag)
        session.videoConfig.sink { [weak self] config in
            guard let self, config["cameraId"] as? String == self.session.settings.cameraId else { return }
            if let rotation = config["rotationDegrees"] as? Int { self.automaticRotation = rotation }
            self.appliedOptical = config["opticalStabilizationActive"] as? Bool
            self.appliedElectronic = config["videoStabilizationActive"] as? Bool
            self.appliedZoom = (config["appliedZoomRatio"] as? NSNumber)?.doubleValue
        }.store(in: &bag)
        session.videoFrame.sink { [weak self] f in self?.decoder.decode(f.payload, timestampNs: f.timestampNs) }.store(in: &bag)
        session.audioFrame.sink { [weak self] f in guard let self, self.micOn, !self.privacy else { return }; self.audio.push(f.payload); self.micLevel = self.audio.levelDbfs }.store(in: &bag)
        session.battery.sink { [weak self] b in self?.battery = b }.store(in: &bag)
        session.location.sink { [weak self] l in if self?.privacy == false { self?.location = l } }.store(in: &bag)
        session.sensor.sink { [weak self] s in self?.onSensor(s) }.store(in: &bag)
        session.radioEvent.sink { [weak self] e in self?.onRadioEvent(e) }.store(in: &bag)
        session.notification.sink { [weak self] n in
            let redacted = n["redacted"] as? Bool ?? true
            self?.notifications.insert((Date(), n["appLabel"] as? String ?? "?", redacted ? nil : n["text"] as? String), at: 0)
        }.store(in: &bag)
        session.streamEvent.sink { [weak self] (type, j) in
            if type == MsgType.streamError, j["kind"] as? String == "video" { self?.cameraError = j["message"] as? String }
        }.store(in: &bag)
        session.$state.sink { [weak self] s in
            guard let self else { return }
            if s == .connected {
                if !self.privacy { Task { await self.refreshCallAudio() } }
                self.refreshUSBSharing()
                self.updateSensorDemand()
                if self.cameraOn { self.startCamera() }
                if self.micOn { self.startMic() }
                if self.locationOn { self.startLocation() }
            } else { self.preview = nil; self.sink.detach(); self.updateRinging([]); self.callAudio.update([], muted: false, session: self.session) }
        }.store(in: &bag)
        session.phoneAttached.sink { _ in
            if UserDefaults.standard.object(forKey: "showOnConnect") as? Bool ?? true {
                NotificationCenter.default.post(name: .pixelBridgeShowDashboard, object: nil)
            }
        }.store(in: &bag)
        session.start()
        Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in Task { @MainActor in self?.refreshTether() } }
        refreshTether()
    }

    // MARK: streams

    func startCamera() {
        guard !privacy else { return }
        cameraOn = true; cameraError = nil
        appliedOptical = nil; appliedElectronic = nil; appliedZoom = nil
        fps = 0; frameCount = 0; lastFpsTick = Date()
        let s = session.settings
        session.startStream("video", ["cameraId": s.cameraId, "width": s.videoWidth, "height": s.videoHeight, "fps": savePower ? min(15, s.videoFps) : s.videoFps, "bitrate": s.videoBitrate, "zoomRatio": zoomRatio, "stabilization": stabilization])
    }

    func stopCamera() { cameraOn = false; session.stopStream("video"); preview = nil; sink.detach(); refreshWebcamOutput() }

    func selectCameraLens(_ lens: CameraLens) {
        let changedCamera = session.settings.cameraId != lens.cameraID
        session.settings.cameraId = lens.cameraID; session.settings.save()
        zoomRatio = lens.zoom
        if stabilization == "optical" && (lens.kind == "ultra" || currentCamera?.supportsOptical != true) { stabilization = "auto" }
        saveCameraControls()
        if cameraOn {
            if changedCamera { preview = nil; decoder.invalidate(); startCamera() }
            else { sendCameraControls() }
        }
    }
    func setCameraZoom(_ value: Double) {
        guard value.isFinite, let camera = currentCamera else { return }
        zoomRatio = min(camera.maximumZoom, max(camera.minimumZoom, value))
        saveCameraControls()
        cameraControlTask?.cancel()
        cameraControlTask = Task { [weak self] in
            do { try await Task.sleep(nanoseconds: 80_000_000) } catch { return }
            self?.sendCameraControls()
        }
    }
    func sendCameraControls() {
        saveCameraControls()
        guard cameraOn, session.isConnected, !privacy else { return }
        session.startStream("video", ["updateOnly": true, "zoomRatio": zoomRatio, "stabilization": stabilization])
    }
    func saveCameraControls() {
        let defaults = UserDefaults.standard
        defaults.set(zoomRatio, forKey: "cameraZoom")
        defaults.set(stabilization, forKey: "cameraStabilization")
        defaults.set(orientationMode, forKey: "cameraOrientation")
        defaults.set(mirrorCamera, forKey: "cameraMirror")
    }
    func refreshUSBSharing() {
        guard !tetherBusy, !tetherStatusQueryBusy, let adb = session.adb, !session.serial.isEmpty else { return }
        tetherStatusQueryBusy = true
        let serial = session.serial
        Task {
            defer { tetherStatusQueryBusy = false }
            let result = await adb.shell(serial, "svc usb getFunctions", timeout: 5)
            if result.ok && !tetherBusy { usbSharingEnabled = (result.stdout + result.stderr).contains("ncm") || (result.stdout + result.stderr).contains("rndis") }
        }
    }
    func setInternetSharing(_ enabled: Bool) {
        guard !tetherBusy, let adb = session.adb, !session.serial.isEmpty else { return }
        tetherBusy = true
        tetherMessage = enabled ? "Enabling USB internet sharing…" : "Disabling USB internet sharing…"
        let serial = session.serial
        Task {
            _ = await adb.shell(serial, "svc usb setFunctions \(enabled ? "ncm" : "mtp")", timeout: 8)
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            let check = await adb.shell(serial, "svc usb getFunctions", timeout: 10)
            refreshTether()
            if check.ok {
                let active = (check.stdout + check.stderr).contains("ncm") || (check.stdout + check.stderr).contains("rndis")
                usbSharingEnabled = active
                tetherMessage = active == enabled ? (enabled ? "USB internet sharing enabled" : "USB internet sharing disabled") : "The phone did not change USB mode. Try again."
            } else { tetherMessage = "Could not confirm USB mode. Reconnect the phone and try again." }
            tetherBusy = false
        }
    }

    func startMic() { guard !privacy else { return }; micOn = true; session.startStream("audio", ["rateHz": 48000]) }
    func stopMic() { micOn = false; session.stopStream("audio") }

    func startLocation() { guard !privacy else { return }; locationOn = true; session.startStream("location", ["intervalMs": 1000]) }
    func stopLocation() { locationOn = false; session.stopStream("location"); location = nil }

    func setPrivacy(_ on: Bool) {
        privacy = on
        phone.setEnabled(!on)
        callAudio.setAllowed(!on)
        if !on { Task { await refreshCallAudio() } }
        if on { updateRinging([]) }
        if on { compass.reset() }
        if on {
            ["video", "audio", "sensors", "location"].forEach { session.stopStream($0) }
            preview = nil; location = nil; sink.detach()
        } else {
            updateSensorDemand()
            if cameraOn { startCamera() }; if micOn { startMic() }; if locationOn { startLocation() }
        }
    }

    private func onDecoded(_ pb: CVPixelBuffer, _ pts: CMTime) {
        guard cameraOn, !privacy else { return }
        let rotation = orientationMode == "auto" ? automaticRotation : Int(orientationMode) ?? 0
        guard let processed = frameProcessor.process(pb, rotation: rotation, mirrored: mirrorCamera) else { return }
        sink.send(processed, pts: pts)
        let outputStatus = sink.lastError ?? (sink.framesSent > 0 && sink.isAttached ? "Sending live video." : "Preparing webcam output…")
        if webcamStatus != outputStatus { webcamStatus = outputStatus }
        if let name = sink.outputName, webcamName != name { webcamName = name }
        frameCount += 1
        let now = Date()
        if now.timeIntervalSince(lastFpsTick) >= 1 { fps = Double(frameCount) / now.timeIntervalSince(lastFpsTick); frameCount = 0; lastFpsTick = now }
        if cameraVisible && windowVisible && now.timeIntervalSince(lastPreview) > 1.0 / 15 { preview = processed; lastPreview = now }
    }

    private func onSensor(_ s: SensorSample) {
        guard !privacy, windowVisible else { return }
        if compassVisible { compass.accept(s) }
        guard sensorsVisible else { return }
        let now = Date()
        guard now.timeIntervalSince(sensorUIUpdates[s.type] ?? .distantPast) >= 1.0 / 15 else { return }
        sensorUIUpdates[s.type] = now
        latest[s.type] = s
        sampleIndex += 1
        let t = Double(s.timestampNs) / 1e9
        func push(_ arr: inout [SensorPoint]) {
            guard s.values.count >= 3 else { return }
            arr.append(SensorPoint(id: sampleIndex, t: t, x: s.values[0], y: s.values[1], z: s.values[2]))
            if arr.count > 300 { arr.removeFirst(arr.count - 300) }
        }
        switch s.type {
        case SensorType.accelerometer: push(&accel)
        case SensorType.gyroscope: push(&gyro)
        case SensorType.rotationVector where s.values.count >= 3:
            let (x, y, z) = (s.values[0], s.values[1], s.values[2])
            quaternion = (x, y, z, s.values.count >= 4 ? s.values[3] : max(0, 1 - x * x - y * y - z * z).squareRoot())
        default: break
        }
    }

    // MARK: radio

    private func call(_ op: String, _ args: [String: Any] = [:], timeout: Double = 15) async -> [String: Any]? {
        do {
            let r = try await session.radio(op, args, timeout: timeout)
            if r["ok"] as? Bool == true { return r["result"] as? [String: Any] ?? [:] }
            radioStatus = r["message"] as? String ?? "failed"
        } catch { radioStatus = "\(error)" }
        return nil
    }

    func scanBle() async {
        bleDevices = []
        if await call("ble.scan.start", ["durationMs": 15000]) != nil { scanning = true }
    }

    func stopBle() async { _ = await call("ble.scan.stop"); scanning = false }

    func loadBonded() async {
        guard let r = await call("bt.bonded"), let arr = r["devices"] as? [[String: Any]] else { return }
        bonded = arr.map { BleDevice(address: $0["address"] as? String ?? "", name: $0["name"] as? String, rssi: 0, connectable: true) }
    }

    func connect(_ d: BleDevice) async { update(d.address) { $0.state = "connecting" }; _ = await call("ble.connect", ["address": d.address]) }
    func disconnect(_ d: BleDevice) async { _ = await call("ble.disconnect", ["address": d.address]); update(d.address) { $0.state = "seen" } }

    func read(_ d: BleDevice, _ c: GattCharacteristic) async {
        guard let r = await call("ble.read", ["address": d.address, "service": c.service, "characteristic": c.uuid]),
              let b64 = r["valueB64"] as? String, let v = Data(base64Encoded: b64) else { return }
        setValue(d.address, c, Self.format(v))
    }

    func write(_ d: BleDevice, _ c: GattCharacteristic, hex: String) async {
        let clean = hex.filter(\.isHexDigit)
        guard clean.count % 2 == 0, !clean.isEmpty else { radioStatus = "Enter hex bytes, e.g. 01 FF"; return }
        var bytes = Data(); var i = clean.startIndex
        while i < clean.endIndex { let j = clean.index(i, offsetBy: 2); bytes.append(UInt8(clean[i..<j], radix: 16)!); i = j }
        _ = await call("ble.write", ["address": d.address, "service": c.service, "characteristic": c.uuid, "valueB64": bytes.base64EncodedString(), "withResponse": true])
    }

    func notify(_ d: BleDevice, _ c: GattCharacteristic) async {
        _ = await call("ble.notify", ["address": d.address, "service": c.service, "characteristic": c.uuid, "enable": true])
    }

    func refreshWifi() async { wifi = await call("wifi.status") ?? [:] }

    func scanWifi() async {
        radioStatus = "Scanning…"
        guard let r = await call("wifi.scan", timeout: 20), let arr = r["results"] as? [[String: Any]] else { return }
        wifiNetworks = arr.map { WifiNetwork(ssid: ($0["ssid"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? "(hidden network)", bssid: $0["bssid"] as? String ?? "",
                                             rssi: $0["rssi"] as? Int ?? -100, frequencyMhz: $0["frequencyMhz"] as? Int ?? 0, security: $0["security"] as? String ?? "") }
        radioStatus = "\(arr.count) networks"
    }

    private func onRadioEvent(_ e: [String: Any]) {
        guard let event = e["event"] as? String else { return }
        let addr = e["address"] as? String ?? ""
        switch event {
        case "phone.calls":
            let calls = privacy ? [] : (e["calls"] as? [[String: Any]] ?? [])
            updateRinging(calls)
            callAudio.update(calls, muted: e["muted"] as? Bool ?? false, session: session)
        case "ble.scanResult":
            let rssi = e["rssi"] as? Int ?? -100
            if let i = bleDevices.firstIndex(where: { $0.address == addr }) {
                bleDevices[i].rssi = rssi
                if bleDevices[i].name == nil { bleDevices[i].name = e["name"] as? String }
            } else {
                bleDevices.append(BleDevice(address: addr, name: e["name"] as? String, rssi: rssi, connectable: e["connectable"] as? Bool ?? false))
            }
        case "ble.scanStopped": scanning = false; bleDevices.sort { $0.rssi > $1.rssi }
        case "ble.connectionState": update(addr) { $0.state = e["state"] as? String ?? "?" }
        case "ble.services":
            let chars = (e["services"] as? [[String: Any]] ?? []).flatMap { s in
                (s["characteristics"] as? [[String: Any]] ?? []).map {
                    GattCharacteristic(service: s["uuid"] as? String ?? "", uuid: $0["uuid"] as? String ?? "", properties: $0["properties"] as? [String] ?? [])
                }
            }
            update(addr) { $0.characteristics = chars; $0.state = "ready" }
        case "ble.notification":
            if let b64 = e["valueB64"] as? String, let v = Data(base64Encoded: b64), let cu = e["characteristic"] as? String,
               let d = (bleDevices + bonded).first(where: { $0.address == addr }), let c = d.characteristics.first(where: { $0.uuid == cu }) {
                setValue(addr, c, Self.format(v) + "  (notified)")
            }
        default: break
        }
    }

    private func updateRinging(_ calls: [[String: Any]]) {
        let id = calls.first { $0["state"] as? String == "ringing" }?["id"] as? String
        guard id != ringingCallID else { return }
        callRingtone?.stop(); ringingCallID = id
        if id != nil {
            callRingtone = Bundle.main.url(forResource: "CallRing", withExtension: "wav").flatMap { NSSound(contentsOf: $0, byReference: true) } ?? NSSound(named: NSSound.Name("Glass"))
            callRingtone?.loops = true; callRingtone?.play()
        }
    }
    func silenceCallRingtone() { callRingtone?.stop() }
    private func refreshCallAudio() async {
        await phone.refreshCallStatus()
        guard !privacy else { return }
        callAudio.update(phone.calls.map(\.values), muted: phone.muted, session: session)
        updateRinging(phone.calls.map(\.values))
    }

    private func update(_ addr: String, _ f: (inout BleDevice) -> Void) {
        if let i = bleDevices.firstIndex(where: { $0.address == addr }) { f(&bleDevices[i]) }
        if let i = bonded.firstIndex(where: { $0.address == addr }) { f(&bonded[i]) }
    }

    private func setValue(_ addr: String, _ c: GattCharacteristic, _ v: String) {
        update(addr) { d in if let i = d.characteristics.firstIndex(of: c) { d.characteristics[i].value = v } }
    }

    static func format(_ v: Data) -> String {
        let hex = v.map { String(format: "%02X", $0) }.joined()
        if let s = String(data: v, encoding: .utf8), !s.isEmpty, s.unicodeScalars.allSatisfy({ $0.value >= 32 && $0.value != 127 }) {
            return "\"\(s)\"  ·  0x\(hex)"
        }
        return "0x\(hex)"
    }

    // MARK: USB tethering (read-only): macOS shows an NCM tether as an Ethernet-like interface.

    func refreshTether() {
        refreshUSBSharing()
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/networksetup")
        p.arguments = ["-listallhardwareports"]
        let pipe = Pipe(); p.standardOutput = pipe
        try? p.run(); p.waitUntilExit()
        let out = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        // Hardware-port entries persist after unplugging. Require a live link and IPv4 address.
        let blocks = out.components(separatedBy: "\n\n")
        let store = SCDynamicStoreCreate(nil, "PixelBridge" as CFString, nil, nil)
        tetherInterface = blocks.compactMap { block -> String? in
            guard block.localizedCaseInsensitiveContains("Pixel") || block.localizedCaseInsensitiveContains("NCM") || block.localizedCaseInsensitiveContains("Android"),
                  let interface = block.components(separatedBy: "\n").first(where: { $0.hasPrefix("Device: ") })?.replacingOccurrences(of: "Device: ", with: ""),
                  let link = SCDynamicStoreCopyValue(store, "State:/Network/Interface/\(interface)/Link" as CFString) as? [String: Any],
                  link["Active"] as? Bool == true,
                  let ipv4 = SCDynamicStoreCopyValue(store, "State:/Network/Interface/\(interface)/IPv4" as CFString) as? [String: Any],
                  let addresses = ipv4["Addresses"] as? [String],
                  addresses.contains(where: { !$0.hasPrefix("169.254.") && $0 != "0.0.0.0" }) else { return nil }
            return interface
        }.first
    }

    @Published var phoneScreenStatus = ""
    private var phoneScreenProcess: Process?
    func closePhoneScreen() { if phoneScreenProcess?.isRunning == true { phoneScreenProcess?.terminate() }; phoneScreenProcess = nil }
    func openPhoneScreen() {
        if let process = phoneScreenProcess, process.isRunning {
            NSRunningApplication(processIdentifier: process.processIdentifier)?.activate(options: [.activateAllWindows])
            return
        }
        guard let executable = ["/opt/homebrew/bin/scrcpy", "/usr/local/bin/scrcpy"].first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else {
            phoneScreenStatus = "Phone-screen support is missing. Install scrcpy, then try again."
            return
        }
        guard !session.serial.isEmpty, let adb = session.adb else {
            phoneScreenStatus = "Connect and authorize the phone first."
            return
        }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = ["-s", session.serial, "--no-audio", "--force-adb-forward", "--max-size=1280", "--max-fps=15", "--turn-screen-off", "--verbosity=warn", "--window-title", "PixelBridge phone"]
        var environment = ProcessInfo.processInfo.environment
        environment["ADB"] = adb.path
        environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:" + (environment["PATH"] ?? "/usr/bin:/bin")
        process.environment = environment
        let errors = Pipe()
        process.standardError = errors
        process.standardOutput = FileHandle.nullDevice
        process.terminationHandler = { [weak self] ended in
            let detail = String(decoding: errors.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            Task { @MainActor in
                self?.phoneScreenStatus = ended.terminationStatus == 0 ? "Phone screen closed." : "Phone screen could not open. " + (detail.isEmpty ? "Check the USB connection and try again." : String(detail.suffix(700)))
                self?.phoneScreenProcess = nil
            }
        }
        do {
            try process.run()
            phoneScreenProcess = process
            phoneScreenStatus = "Phone screen opened in a separate window."
        } catch { phoneScreenStatus = "Could not launch the phone screen: \(error.localizedDescription)" }
    }

    // MARK: camera extension install (user approves in System Settings › Privacy & Security)

    func installCameraExtension() {
        guard canInstallCameraExtension else {
            extensionState = "This build cannot install a webcam extension. Use the installed OBS Virtual Camera output."
            return
        }
        let req = OSSystemExtensionRequest.activationRequest(forExtensionWithIdentifier: "dev.pixelbridge.PixelBridgeCamera", queue: .main)
        req.delegate = self
        OSSystemExtensionManager.shared.submitRequest(req)
        extensionState = "Waiting for approval…"
    }
}

extension AppModel: OSSystemExtensionRequestDelegate {
    nonisolated func request(_ request: OSSystemExtensionRequest, actionForReplacingExtension existing: OSSystemExtensionProperties,
                             withExtension ext: OSSystemExtensionProperties) -> OSSystemExtensionRequest.ReplacementAction { .replace }
    nonisolated func requestNeedsUserApproval(_ request: OSSystemExtensionRequest) {
        Task { @MainActor in self.extensionState = "Approve PixelBridge Camera in System Settings › Privacy & Security" }
    }
    nonisolated func request(_ request: OSSystemExtensionRequest, didFinishWithResult result: OSSystemExtensionRequest.Result) {
        Task { @MainActor in self.extensionState = result == .completed ? "Installed" : "Installed (restart required)" }
    }
    nonisolated func request(_ request: OSSystemExtensionRequest, didFailWithError error: Error) {
        Task { @MainActor in self.extensionState = "Install failed: \(error.localizedDescription)" }
    }
}
