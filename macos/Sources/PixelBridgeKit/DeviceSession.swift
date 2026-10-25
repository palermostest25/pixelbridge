import Combine
import Foundation

public enum LinkState: String, Sendable {
    case noAdb, disconnected, unauthorized, offline, wrongDevice, apkMissing, apkOutdated, notPaired, connecting, connected, error

    public var label: String {
        switch self {
        case .connected: "Connected"; case .disconnected: "Not connected"; case .connecting: "Connecting…"
        case .notPaired: "Not paired"; case .apkMissing: "Phone app missing"; case .apkOutdated: "Phone app outdated"
        case .unauthorized: "USB debugging not allowed"; case .noAdb: "adb not found"; case .wrongDevice: "Unknown phone"
        case .offline: "Phone offline"; case .error: "Error"
        }
    }
}

/// Settings persisted in UserDefaults.
public struct BridgeSettings: Codable, Sendable {
    public var targetSerial = ""   // empty = the single attached phone
    public var adbPath: String?
    public var autoConnect = true
    public var sensorRateHz = 50
    public var cameraId = "0"
    public var videoWidth = 1280, videoHeight = 720, videoFps = 30, videoBitrate = 4_000_000
    public init() {}

    public static func load() -> BridgeSettings {
        guard let d = UserDefaults.standard.data(forKey: "settings"), let s = try? JSONDecoder().decode(BridgeSettings.self, from: d) else { return .init() }
        return s
    }

    public func save() { if let d = try? JSONEncoder().encode(self) { UserDefaults.standard.set(d, forKey: "settings") } }
}

/// Discovery, validation, forwards, authenticated channels and reconnection for the target serial.
/// Mirrors windows/PixelBridge.Core/DeviceSession.cs; only the configured serial is ever touched.
@MainActor
public final class DeviceSession: ObservableObject {
    public static let package = "dev.pixelbridge.app"
    public static let expectedAppVersion = "0.4.2"
    private static let approved: Set<String> = ["panther"]
    private static let channelPorts: [(String, Int)] = [("control", 27183), ("video", 27184), ("audio", 27185), ("telemetry", 27186)]

    @Published public private(set) var state: LinkState = .disconnected
    @Published public private(set) var detail = ""
    @Published public private(set) var deviceProps: [String: String] = [:]
    @Published public private(set) var otherDevices: [AdbDevice] = []
    @Published public private(set) var capabilities: [String: Any] = [:]
    @Published public private(set) var moduleStatus: [String: String] = [:]
    @Published public private(set) var portMap: [String: Int] = [:]
    @Published public private(set) var rttMs: Double = 0

    public var settings: BridgeSettings
    public private(set) var adb: AdbClient?
    public var clock = ClockSync()
    private let pairing = PairingStore()
    private var channels: [String: ChannelClient] = [:]
    private var ownedForwards: [Int] = []
    private var runTask: Task<Void, Never>?
    private var heartbeat: Task<Void, Never>?
    private var lastPong: Int64 = 0
    private var nextAttempt = Date.distantPast
    private var nextVersionProbe = Date.distantPast
    private var backoff: TimeInterval = 2
    private var busy = false
    private var radioPending: [Int: CheckedContinuation<[String: Any], Error>] = [:]
    private var radioId = 0

    // Event streams (delivered on the main actor).
    public let phoneAttached = PassthroughSubject<Void, Never>()
    private var presentSerial: String?
    private var attemptedCompanionUpdates = Set<String>()
    public let sensor = PassthroughSubject<SensorSample, Never>()
    public let battery = PassthroughSubject<BatteryStatus, Never>()
    public let location = PassthroughSubject<LocationFix, Never>()
    public let videoFrame = PassthroughSubject<Frame, Never>()
    public let videoConfig = PassthroughSubject<[String: Any], Never>()
    public let audioFrame = PassthroughSubject<Frame, Never>()
    public let streamEvent = PassthroughSubject<(UInt8, [String: Any]), Never>()
    public let radioEvent = PassthroughSubject<[String: Any], Never>()
    public let notification = PassthroughSubject<[String: Any], Never>()

    private var discoveredSerial = ""
    public var serial: String { discoveredSerial.isEmpty ? settings.targetSerial.trimmingCharacters(in: .whitespacesAndNewlines) : discoveredSerial }
    public var isConnected: Bool { state == .connected }
    public var pairedAt: Date? { pairing.pairedAt(serial: serial) }

    public init(settings: BridgeSettings = .load()) { self.settings = settings }

    public func start() {
        runTask = Task { [weak self] in
            while !Task.isCancelled {
                await self?.tick()
                try? await Task.sleep(nanoseconds: 1_500_000_000)
            }
        }
    }

    private func set(_ s: LinkState, _ d: String = "") {
        guard state != s || detail != d else { return }
        state = s; detail = d
        Log.info("link state \(s.rawValue)\(d.isEmpty ? "" : ": \(d)")")
    }

    private func tick() async {
        guard let path = AdbClient.locate(configured: settings.adbPath) else { set(.noAdb, "Install adb: brew install android-platform-tools"); return }
        if adb?.path != path { adb = AdbClient(path: path) }
        if isConnected {
            if hostNowNs() - lastPong > 5_000_000_000 { Log.warn("heartbeat timeout"); await teardown("heartbeat timeout") }
            return
        }
        guard let adb else { return }
        let discovery = await adb.run(["devices", "-l"], timeout: 10)
        guard discovery.ok else {
            set(.error, "USB discovery failed: \(discovery.stderr.isEmpty ? discovery.stdout : discovery.stderr)")
            return
        }
        let devices = AdbClient.parseDevices(discovery.stdout)
        let target: AdbDevice
        switch AdbClient.selectTarget(devices, configuredSerial: settings.targetSerial) {
        case .missing:
            presentSerial = nil
            discoveredSerial = ""
            otherDevices = devices
            set(.disconnected, settings.targetSerial.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                ? "No Android phone detected. Connect a USB data cable and enable USB debugging on the phone."
                : "The selected phone is not connected. Check its USB cable or clear Phone serial in Settings to detect automatically.")
            backoff = 2; nextAttempt = .distantPast
            return
        case .multiple:
            discoveredSerial = ""
            otherDevices = devices
            set(.wrongDevice, "More than one phone is connected. Set Phone serial in Settings, or disconnect the other phones.")
            return
        case .selected(let device):
            target = device
            discoveredSerial = device.serial
            otherDevices = devices.filter { $0.serial != device.serial }
        }
        if presentSerial != target.serial { presentSerial = target.serial; phoneAttached.send(()) }
        switch target.state {
        case "device": break
        case "unauthorized": set(.unauthorized, "Allow USB debugging for this Mac on the phone"); return
        default: set(.offline, "adb state '\(target.state)'"); return
        }
        if settings.autoConnect, [.apkMissing, .apkOutdated].contains(state), Date() >= nextVersionProbe {
            nextVersionProbe = Date().addingTimeInterval(5)
            if await adb.packageVersion(serial, Self.package) == Self.expectedAppVersion { retryNow() }
        }
        guard settings.autoConnect, Date() >= nextAttempt else { return }
        await connect()
    }

    public func retryNow() { nextAttempt = .distantPast; backoff = 2; settings.autoConnect = true }

    private func needsUser(_ s: LinkState, _ d: String) { nextAttempt = .distantFuture; set(s, d) }

    private func fail(_ s: LinkState, _ d: String) {
        nextAttempt = Date().addingTimeInterval(backoff)
        backoff = min(backoff * 2, 30)
        set(s, "\(d) (retrying)")
    }

    public func connect() async {
        guard let adb, !busy, !isConnected else { return }
        busy = true
        defer { busy = false }
        set(.connecting, "validating device")
        let echo = await adb.shell(serial, "echo pb-ok", timeout: 10)
        guard echo.ok, echo.stdout.contains("pb-ok") else { fail(.offline, "shell handshake failed"); return }
        let completed = await adb.getprop(serial, "sys.boot_completed")
        let current = await adb.shell(serial, "am get-current-user", timeout: 5).stdout.trimmingCharacters(in: .whitespacesAndNewlines)
        let userID = Int(current).flatMap { $0 >= 0 ? $0 : nil } ?? 0
        let userState = await adb.shell(serial, "am get-started-user-state \(userID)", timeout: 5).stdout
        let readiness = PhoneBoot.evaluate(completed: completed, userState: userState)
        guard readiness == .ready else {
            set(.connecting, readiness == .booting ? "Android is starting. PixelBridge will reconnect automatically." : "Unlock Android once after its restart. PixelBridge will continue automatically.")
            nextAttempt = Date().addingTimeInterval(2); return
        }
        var props: [String: String] = [:]
        for p in ["ro.product.device", "ro.product.model", "ro.build.version.release", "ro.build.version.sdk", "ro.build.id"] {
            props[p] = await adb.getprop(serial, p)
        }
        deviceProps = props
        guard Self.approved.contains(props["ro.product.device"] ?? "") else { needsUser(.wrongDevice, "device '\(props["ro.product.device"] ?? "?")' is not pre-approved"); return }
        var phoneVersion = await adb.packageVersion(serial, Self.package)
        let updateKey = serial + ":" + (phoneVersion ?? "missing")
        if phoneVersion != Self.expectedAppVersion,
           UserDefaults.standard.object(forKey: "autoUpdateCompanion") as? Bool ?? true,
           !attemptedCompanionUpdates.contains(updateKey),
           let apk = Bundle.main.url(forResource: "PixelBridge", withExtension: "apk") {
            attemptedCompanionUpdates.insert(updateKey)
            set(.connecting, "updating phone companion…")
            let result = await adb.run(["-s", serial, "install", "-r", apk.path], timeout: 120)
            guard result.ok else { needsUser(.apkOutdated, "Companion update failed: " + String((result.stderr + result.stdout).prefix(350)) + ". Updates require the original signing key."); return }
            phoneVersion = await adb.packageVersion(serial, Self.package)
        }
        guard let version = phoneVersion else { needsUser(.apkMissing, "PixelBridge app not installed on the phone"); return }
        guard version == Self.expectedAppVersion else { needsUser(.apkOutdated, "phone app \(version), Mac expects \(Self.expectedAppVersion)"); return }

        set(.connecting, "launching phone app")
        _ = await adb.shell(serial, "am start -n \(Self.package)/.MainActivity", timeout: 10)
        await removeOwnedForwards()
        do {
            var map: [String: Int] = [:]
            for (name, port) in Self.channelPorts {
                let host = try await adb.forward(serial, devicePort: port)
                ownedForwards.append(host)
                map[name] = host
            }
            portMap = map
            set(.connecting, "reading saved pairing from Keychain—approve PixelBridge if macOS asks")
            let pairingSerial = serial
            let key = await Task.detached { PairingStore().load(serial: pairingSerial) }.value
            var control: ChannelClient?
            for attempt in 0..<10 where control == nil {
                do { control = try await ChannelClient.connect(port: map["control"]!, channel: "control", key: key) }
                catch let e as AuthError { throw e }
                catch { if attempt == 9 { throw error }; try? await Task.sleep(nanoseconds: 500_000_000) }
            }
            register(control!)
            for name in ["telemetry", "video", "audio"] { register(try await ChannelClient.connect(port: map[name]!, channel: name, key: key)) }
            lastPong = hostNowNs()
            backoff = 2
            set(.connected, "\(props["ro.product.model"] ?? "Pixel") · Android \(props["ro.build.version.release"] ?? "")")
            channels.values.forEach { $0.startReading() }
            heartbeat = Task { [weak self] in
                while !Task.isCancelled {
                    self?.send(.json(MsgType.heartbeat, ["hostNs": hostNowNs()]))
                    try? await Task.sleep(nanoseconds: 1_000_000_000)
                }
            }
        } catch let e as AuthError {
            await cleanup()
            needsUser(.notPaired, e.description)
        } catch {
            await cleanup()
            fail(.error, "\(error)")
        }
    }

    private func register(_ c: ChannelClient) {
        channels[c.channel] = c
        c.onFrame = { [weak self] f in Task { @MainActor in self?.dispatch(c.channel, f) } }
        c.onClose = { [weak self] err in
            Task { @MainActor in
                guard let self, self.isConnected else { return }
                Log.warn("\(c.channel) channel closed: \(err.map { "\($0)" } ?? "eof")")
                await self.teardown("\(c.channel) closed")
            }
        }
    }

    private func dispatch(_ channel: String, _ f: Frame) {
        do {
            switch f.type {
            case MsgType.sensorSample: sensor.send(try SensorSample(frame: f))
            case MsgType.batteryStatus: if let j = f.jsonObject { battery.send(BatteryStatus(json: j)) }
            case MsgType.locationSample: if let j = f.jsonObject { location.send(try LocationFix(json: j)) }
            case MsgType.videoFrame: videoFrame.send(f)
            case MsgType.videoConfig: if let j = f.jsonObject { videoConfig.send(j) }
            case MsgType.audioFrame: audioFrame.send(f)
            case MsgType.capabilities: capabilities = f.jsonObject ?? [:]
            case MsgType.status: moduleStatus = (f.jsonObject?["modules"] as? [String: String]) ?? [:]
            case MsgType.notificationEvent: if let j = f.jsonObject { notification.send(j) }
            case MsgType.pong:
                if let j = f.jsonObject, let h = (j["hostNs"] as? NSNumber)?.int64Value, let d = (j["deviceNs"] as? NSNumber)?.int64Value {
                    let now = hostNowNs()
                    clock.add(hostSent: h, device: d, hostRecv: now)
                    rttMs = clock.lastRttMs
                    lastPong = now
                }
            case MsgType.streamStarted, MsgType.streamStopped, MsgType.streamError:
                if let j = f.jsonObject { streamEvent.send((f.type, j)) }
            case MsgType.radioResponse:
                if let j = f.jsonObject, let id = j["id"] as? Int, let cont = radioPending.removeValue(forKey: id) { cont.resume(returning: j) }
            case MsgType.radioEvent: if let j = f.jsonObject { radioEvent.send(j) }
            case MsgType.error: Log.warn("device error \(f.text)")
            default: Log.warn("\(channel): ignoring type \(f.type)")
            }
        } catch { Log.warn("\(channel): bad frame \(f.type): \(error)") }
    }

    @discardableResult
    public func send(_ f: Frame) -> Bool { channels["control"]?.send(f) ?? false }

    @discardableResult
    public func startStream(_ kind: String, _ options: [String: Any]) -> Bool { send(.json(MsgType.startStream, ["kind": kind, "options": options])) }

    @discardableResult
    public func stopStream(_ kind: String) -> Bool { send(.json(MsgType.stopStream, ["kind": kind])) }

    public func requestIdr() { send(Frame(type: MsgType.requestIdr, payload: Data())) }

    /// RADIO_REQUEST (section 9). Returns the RADIO_RESPONSE object.
    public func radio(_ op: String, _ args: [String: Any] = [:], timeout: Double = 15) async throws -> [String: Any] {
        radioId += 1
        let id = radioId
        guard send(.json(MsgType.radioRequest, ["id": id, "op": op, "args": args])) else { throw ProtocolError.closed }
        let timer = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(timeout * 1e9))
            await MainActor.run { self?.radioPending.removeValue(forKey: id)?.resume(throwing: ProtocolError.malformed("timeout")) }
        }
        defer { timer.cancel() }
        return try await withCheckedThrowingContinuation { radioPending[id] = $0 }
    }

    /// One-time pairing through the authorised ADB transport (section 3).
    public func pair() async -> Bool {
        guard let adb, !serial.isEmpty, let key = try? pairing.createAndSave(serial: serial) else { return false }
        let b64url = key.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        let host = (Host.current().localizedName ?? "Mac").filter { $0.isLetter || $0.isNumber }
        let r = await adb.run(["-s", serial, "shell", "am", "start", "-n", "\(Self.package)/.PairActivity", "--es", "key", b64url, "--es", "host", host], timeout: 15)
        let ok = r.ok && !r.stdout.contains("Error") && !r.stderr.contains("Error")
        Log.info(ok ? "pairing key delivered" : "pairing failed: \(r.stderr)")
        nextAttempt = Date().addingTimeInterval(2); backoff = 2
        if ok { set(.disconnected, "paired; reconnecting") }
        return ok
    }

    public func unpair() { pairing.delete(serial: serial) }

    public func install(apk: URL) async -> AdbResult? {
        guard let adb, !serial.isEmpty else { return nil }
        let r = await adb.run(["-s", serial, "install", "-r", apk.path], timeout: 180)
        nextAttempt = .distantPast
        if r.ok { set(.disconnected, "installed; reconnecting") }
        return r
    }

    public func disconnect() async { settings.autoConnect = false; await teardown("user disconnect") }

    private func teardown(_ reason: String) async {
        let was = state
        await cleanup()
        if was == .connected { fail(.disconnected, reason) }
    }

    private func cleanup() async {
        heartbeat?.cancel(); heartbeat = nil
        if state == .connected { state = .disconnected }
        channels.values.forEach { $0.close() }
        channels.removeAll()
        for (_, c) in radioPending { c.resume(throwing: ProtocolError.closed) }
        radioPending.removeAll()
        capabilities = [:]; moduleStatus = [:]
        await removeOwnedForwards()
    }

    private func removeOwnedForwards() async {
        if let adb { for p in ownedForwards { await adb.removeForward(serial, hostPort: p) } }
        ownedForwards.removeAll()
        portMap = [:]
    }

    public func shutdown() async {
        runTask?.cancel()
        await teardown("shutdown")
    }
}

public enum Log {
    private static let queue = DispatchQueue(label: "pb.log")
    nonisolated(unsafe) public static var recent: [String] = []
    public static func info(_ m: String) { write("INFO", m) }
    public static func warn(_ m: String) { write("WARN", m) }
    private static func write(_ level: String, _ m: String) {
        let line = "\(ISO8601DateFormatter().string(from: Date())) \(level) \(m)"
        queue.async {
            recent.append(line)
            if recent.count > 2000 { recent.removeFirst(recent.count - 2000) }
            let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("PixelBridge/logs")
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let url = dir.appendingPathComponent("pixelbridge.log")
            if let h = try? FileHandle(forWritingTo: url) { h.seekToEndOfFile(); h.write(Data((line + "\n").utf8)); try? h.close() }
            else { try? Data((line + "\n").utf8).write(to: url) }
        }
    }
}
