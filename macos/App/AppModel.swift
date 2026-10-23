import Combine
import CoreMedia
import CoreVideo
import Foundation
import PixelBridgeKit
import SystemExtensions

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

/// All UI state for the macOS app; mirrors windows/PixelBridge.App/AppController.cs.
@MainActor
final class AppModel: NSObject, ObservableObject {
    let session = DeviceSession()
    private let decoder = H264Decoder()
    private let sink = CameraSink()
    let audio = AudioPlayer()
    private var bag = Set<AnyCancellable>()

    @Published var preview: CVPixelBuffer?
    @Published var fps: Double = 0
    @Published var cameraOn = false, micOn = false, locationOn = false
    @Published var privacy = false
    @Published var cameraError: String?
    @Published var battery: BatteryStatus?
    @Published var latest: [Int32: SensorSample] = [:]
    @Published var accel: [SensorPoint] = [], gyro: [SensorPoint] = []
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
    @Published var tetherInterface: String?
    @Published var micLevel: Double = -120

    private var frameCount = 0, lastFpsTick = Date()
    private var sampleIndex = 0
    private var lastPreview = Date.distantPast

    override init() {
        super.init()
        decoder.onFrame = { [weak self] pb, pts in
            Task { @MainActor in self?.onDecoded(pb, pts) }
        }
        session.videoFrame.sink { [weak self] f in self?.decoder.decode(f.payload, timestampNs: f.timestampNs) }.store(in: &bag)
        session.audioFrame.sink { [weak self] f in self?.audio.push(f.payload); self?.micLevel = self?.audio.levelDbfs ?? -120 }.store(in: &bag)
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
                if self.cameraOn { self.startCamera() }
                if self.micOn { self.startMic() }
                if self.locationOn { self.startLocation() }
            } else { self.preview = nil; self.sink.detach() }
        }.store(in: &bag)
        session.start()
        Timer.scheduledTimer(withTimeInterval: 10, repeats: true) { [weak self] _ in Task { @MainActor in self?.refreshTether() } }
        refreshTether()
    }

    // MARK: streams

    func startCamera() {
        guard !privacy else { return }
        cameraOn = true; cameraError = nil
        let s = session.settings
        session.startStream("video", ["cameraId": s.cameraId, "width": s.videoWidth, "height": s.videoHeight, "fps": s.videoFps, "bitrate": s.videoBitrate])
    }

    func stopCamera() { cameraOn = false; session.stopStream("video"); preview = nil }

    func switchCamera(_ id: String) { session.settings.cameraId = id; session.settings.save(); if cameraOn { startCamera() } }

    func startMic() { guard !privacy else { return }; micOn = true; session.startStream("audio", ["rateHz": 48000]) }
    func stopMic() { micOn = false; session.stopStream("audio") }

    func startLocation() { guard !privacy else { return }; locationOn = true; session.startStream("location", ["intervalMs": 1000]) }
    func stopLocation() { locationOn = false; session.stopStream("location"); location = nil }

    func setPrivacy(_ on: Bool) {
        privacy = on
        if on {
            ["video", "audio", "sensors", "location"].forEach { session.stopStream($0) }
            preview = nil; location = nil
        } else {
            session.startStream("sensors", ["rateHz": session.settings.sensorRateHz])
            if cameraOn { startCamera() }; if micOn { startMic() }; if locationOn { startLocation() }
        }
    }

    private func onDecoded(_ pb: CVPixelBuffer, _ pts: CMTime) {
        guard cameraOn, !privacy else { return }
        sink.send(pb, pts: pts)
        frameCount += 1
        let now = Date()
        if now.timeIntervalSince(lastFpsTick) >= 1 { fps = Double(frameCount) / now.timeIntervalSince(lastFpsTick); frameCount = 0; lastFpsTick = now }
        if now.timeIntervalSince(lastPreview) > 1.0 / 30 { preview = pb; lastPreview = now }
    }

    private func onSensor(_ s: SensorSample) {
        guard !privacy else { return }
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
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/networksetup")
        p.arguments = ["-listallhardwareports"]
        let pipe = Pipe(); p.standardOutput = pipe
        try? p.run(); p.waitUntilExit()
        let out = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        // Blocks look like "Hardware Port: Pixel 7\nDevice: en9\n…"; match the phone or generic NCM names.
        let blocks = out.components(separatedBy: "\n\n")
        tetherInterface = blocks.first { $0.localizedCaseInsensitiveContains("Pixel") || $0.localizedCaseInsensitiveContains("NCM") || $0.localizedCaseInsensitiveContains("Android") }?
            .components(separatedBy: "\n").first { $0.hasPrefix("Hardware Port:") }?.replacingOccurrences(of: "Hardware Port: ", with: "")
    }

    // MARK: camera extension install (user approves in System Settings › Privacy & Security)

    func installCameraExtension() {
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
