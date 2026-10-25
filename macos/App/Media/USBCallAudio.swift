import AVFoundation
import Combine
import Foundation
import PixelBridgeKit

@MainActor final class USBCallAudio: ObservableObject {
    @Published private(set) var enabled = UserDefaults.standard.object(forKey: "usbCallAudio") as? Bool ?? true
    @Published private(set) var active = false
    @Published private(set) var listeningMuted = false
    @Published private(set) var status = "Mac microphone and speakers are ready for your next call."
    @Published private(set) var microphoneLevel: Double = -80
    @Published private(set) var microphoneAtPhone: Double = -80
    @Published private(set) var microphonePeak: Double = -80
    @Published private(set) var microphoneBufferMs: Double = 0
    @Published private(set) var microphoneDiagnostics = ""
    private var io: CallAudioIO?
    private var callID: String?
    private var attemptedID: String?
    private var generation = 0
    private var allowed = true
    private var latestCalls: [[String: Any]] = []
    private var muted = false

    func update(_ calls: [[String: Any]], muted: Bool, session: DeviceSession) {
        guard allowed else { return }
        latestCalls = calls; self.muted = muted; io?.setMicrophoneMuted(muted)
        let id = calls.first { ["dialing", "active"].contains($0["state"] as? String ?? "") }?["id"] as? String
        guard id != callID else { return }
        stop(); callID = id
        guard enabled, let id, id != attemptedID else { if id == nil { attemptedID = nil }; return }
        attemptedID = id; start(id, session: session)
    }
    func setEnabled(_ value: Bool, session: DeviceSession) {
        enabled = value; UserDefaults.standard.set(value, forKey: "usbCallAudio")
        stop(); callID = nil; attemptedID = nil
        if value { update(latestCalls, muted: muted, session: session) }
        else { status = "Call audio is on the Pixel or its connected headset." }
    }
    func setAllowed(_ value: Bool) { allowed = value; if !value { latestCalls = []; callID = nil; attemptedID = nil; stop(); microphoneDiagnostics = ""; microphonePeak = -80 } }
    func retry(session: DeviceSession) { callID = nil; attemptedID = nil; update(latestCalls, muted: muted, session: session) }
    func toggleListeningMute() { listeningMuted.toggle(); io?.setListeningMuted(listeningMuted) }
    private func start(_ id: String, session: DeviceSession) {
        generation += 1; let token = generation; microphonePeak = -80; microphoneBufferMs = 0; microphoneDiagnostics = ""
        status = "Allow microphone access to use Mac call audio…"
        Task {
            guard await AVCaptureDevice.requestAccess(for: .audio) else { if token == generation { status = "Allow PixelBridge microphone access in macOS Privacy & Security, then retry Mac audio." }; return }
            guard token == generation, allowed, enabled, callID == id, let adb = session.adb, !session.serial.isEmpty else { return }
            status = "Connecting Mac call audio…"
            let result = await adb.run(["-s", session.serial, "shell", "pm path dev.pixelbridge.app"], timeout: 8)
            guard token == generation, callID == id else { return }
            let path = result.stdout.split(separator: "\n").first(where: { $0.hasPrefix("package:") })?.dropFirst(8).trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard result.ok, path.range(of: "^/[A-Za-z0-9_/.=+~\\-]+$", options: .regularExpression) != nil else { status = "Could not locate the phone’s call-audio helper. Update the phone app, then retry."; return }
            do {
                let bridge = try CallAudioIO(adb: adb.path, serial: session.serial, apkPath: path, onReady: { [weak self] in
                    Task { @MainActor in guard let self, token == self.generation else { return }; self.active = true; self.status = "Call audio is using this Mac over \(session.transportLabel)." }
                }, onError: { [weak self] message in
                    Task { @MainActor in guard let self, token == self.generation else { return }; self.stop(); self.status = message }
                }, onMicrophone: { [weak self] level, duration in
                    Task { @MainActor in guard let self, token == self.generation else { return }; self.microphoneLevel = level; self.microphonePeak = max(self.microphonePeak, level); self.microphoneBufferMs = max(self.microphoneBufferMs, duration) }
                }, onStats: { [weak self] values in
                    Task { @MainActor in
                        guard let self, token == self.generation else { return }
                        self.microphoneAtPhone = 20 * log10(max(Double(values[0]), 1) / 32768)
                        self.microphoneDiagnostics = "Mic \(values[0]); received \(values[1]); played \(values[2]); muted \(values[3]); mode \(values[4]); route \(values[5])"
                    }
                })
                io = bridge; bridge.setMicrophoneMuted(muted); bridge.setListeningMuted(listeningMuted)
                try bridge.start()
            } catch { stop(); status = "Mac call audio could not start: \(error.localizedDescription)" }
        }
    }
    func stop() { generation += 1; io?.close(); io = nil; active = false; microphoneLevel = -80; microphoneAtPhone = -80; if enabled { status = "Mac microphone and speakers are ready for your next call." } }
}

/// Bounded, ephemeral duplex PCM through an authenticated ADB pipe. No TCP listener or recording.
private final class CallAudioIO {
    private let process = Process(), input = Pipe(), output = Pipe(), errors = Pipe()
    private let engine = AVAudioEngine(), player = AVAudioPlayerNode()
    private let inputQueue = DispatchQueue(label: "pb.call.mic", qos: .userInitiated)
    private let outputQueue = DispatchQueue(label: "pb.call.speaker", qos: .userInitiated)
    private let microphoneSlots = DispatchSemaphore(value: 3), playbackSlots = DispatchSemaphore(value: 4)
    private let lock = NSLock()
    private var closed = false, microphoneMuted = false, tapInstalled = false, microphoneReady = false
    private let onReady: () -> Void, onError: (String) -> Void
    private let onMicrophone: (Double, Double) -> Void, onStats: ([Int]) -> Void
    private var lastMicrophoneReport: TimeInterval = 0
    private let format = AVAudioFormat(standardFormatWithSampleRate: 16000, channels: 1)!
    init(adb: String, serial: String, apkPath: String, onReady: @escaping () -> Void, onError: @escaping (String) -> Void, onMicrophone: @escaping (Double, Double) -> Void, onStats: @escaping ([Int]) -> Void) throws {
        self.onReady = onReady; self.onError = onError
        self.onMicrophone = onMicrophone; self.onStats = onStats
        process.executableURL = URL(fileURLWithPath: adb)
        process.arguments = ["-s", serial, "shell", "-T", "CLASSPATH='\(apkPath)' app_process / dev.pixelbridge.app.phone.ShellCallAudio"]
        process.standardInput = input; process.standardOutput = output; process.standardError = errors
        // Drain diagnostics without retaining call data or allowing a blocked stderr pipe.
        errors.fileHandleForReading.readabilityHandler = { handle in _ = handle.availableData }
    }
    func start() throws {
        let node = engine.inputNode
        try node.setVoiceProcessingEnabled(true)
        let source = node.outputFormat(forBus: 0)
        guard source.sampleRate > 0, source.channelCount > 0 else { throw CallAudioFailure("No Mac microphone is available") }
        node.isVoiceProcessingInputMuted = false
        var resampler = CallPCMResampler(inputRate: source.sampleRate)
        engine.attach(player); engine.connect(player, to: engine.mainMixerNode, format: format)
        node.installTap(onBus: 0, bufferSize: 960, format: source) { [weak self] buffer, _ in
            // Never fill the USB pipe with speech while Android is still opening its audio route.
            guard let self, self.lock.withLock({ !self.closed && self.microphoneReady }), microphoneSlots.wait(timeout: .now()) == .success else { return }
            guard let channels = buffer.floatChannelData else { microphoneSlots.signal(); return }
            let count = Int(buffer.frameLength), channelCount = Int(buffer.format.channelCount)
            var mono = [Float](repeating: 0, count: count)
            for channel in 0..<channelCount { for index in 0..<count { let sample = buffer.format.isInterleaved ? channels[0][index * channelCount + channel] : channels[channel][index]; mono[index] += sample / Float(channelCount) } }
            let pcm = resampler.convert(mono)
            guard !pcm.isEmpty else { microphoneSlots.signal(); return }
            let data = pcm.withUnsafeBytes { Data($0) }
            let now = ProcessInfo.processInfo.systemUptime
            if now - lastMicrophoneReport > 0.25 {
                lastMicrophoneReport = now
                var energy: Double = 0
                for sample in pcm { let value = Double(sample); energy += value * value }
                onMicrophone(20 * log10(max(sqrt(energy / Double(pcm.count)), 1) / 32768), Double(count) / source.sampleRate * 1000)
            }
            inputQueue.async { [weak self] in
                guard let self else { return }; defer { microphoneSlots.signal() }
                guard !isClosed, ProcessInfo.processInfo.systemUptime - now < 0.2 else { return }
                let pcm = lock.withLock { self.microphoneMuted ? Data(count: data.count) : data }
                do { try writePCM(pcm) } catch { if !isClosed { onError("Microphone connection stopped. Retry Mac audio.") } }
            }
        }
        tapInstalled = true
        do { try process.run(); engine.prepare(); try engine.start(); player.play() }
        catch { close(); throw error }
        outputQueue.async { [weak self] in self?.receive() }
    }
    private var isClosed: Bool { lock.withLock { closed } }
    func setMicrophoneMuted(_ value: Bool) { lock.withLock { microphoneMuted = value } }
    func setListeningMuted(_ value: Bool) { player.volume = value ? 0 : 1 }
    private func writePCM(_ data: Data) throws {
        guard data.count <= 6400, data.count % 2 == 0 else { return }
        var frame = Data([1]); var size = UInt32(data.count).bigEndian
        withUnsafeBytes(of: &size) { frame.append(contentsOf: $0) }; frame.append(data)
        try input.fileHandleForWriting.write(contentsOf: frame)
    }
    private func read(_ count: Int) throws -> Data {
        var result = Data()
        while result.count < count {
            guard let part = try output.fileHandleForReading.read(upToCount: count - result.count), !part.isEmpty else { throw CallAudioFailure("Computer call audio ended") }
            result.append(part)
        }
        return result
    }
    private func int32(_ data: Data) -> UInt32 { data.reduce(0) { ($0 << 8) | UInt32($1) } }
    private func receive() {
        do {
            let header = try read(12)
            guard header.prefix(4) == Data("PBA1".utf8), int32(header.subdata(in: 4..<8)) == 16000, int32(header.subdata(in: 8..<12)) == 1 else { throw CallAudioFailure("Update the phone app to enable Computer call audio") }
            var ready = false
            while !isClosed {
                let frame = try read(5), size = Int(int32(frame.subdata(in: 1..<5)))
                guard size >= 0, size <= 6400 else { throw CallAudioFailure("Invalid USB audio frame") }
                let data = try read(size)
                if frame[0] == 4, size == 24 {
                    onStats(stride(from: 0, to: 24, by: 4).map { Int(int32(data.subdata(in: $0..<($0 + 4)))) })
                    continue
                }
                if frame[0] == 3 { throw CallAudioFailure(String(data: data, encoding: .utf8) ?? "This phone’s Computer call audio is unavailable") }
                guard frame[0] == 1, size > 0, size % 2 == 0 else { throw CallAudioFailure("Invalid USB speaker audio") }
                if !ready { ready = true; lock.withLock { microphoneReady = true }; onReady() }
                guard playbackSlots.wait(timeout: .now()) == .success else { continue }
                guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(size / 2)), let samples = buffer.floatChannelData?.pointee else { playbackSlots.signal(); continue }
                buffer.frameLength = AVAudioFrameCount(size / 2)
                for index in 0..<(size / 2) { samples[index] = Float(Int16(bitPattern: UInt16(data[index * 2]) | UInt16(data[index * 2 + 1]) << 8)) / 32768 }
                player.scheduleBuffer(buffer, completionCallbackType: .dataPlayedBack) { [weak self] _ in self?.playbackSlots.signal() }
            }
        } catch { if !isClosed { onError("Mac call audio: \(error.localizedDescription)") } }
    }
    func close() {
        guard lock.withLock({ if closed { return false }; closed = true; return true }) else { return }
        if tapInstalled { engine.inputNode.removeTap(onBus: 0); tapInstalled = false }
        player.stop(); engine.stop()
        try? input.fileHandleForWriting.close()
        if process.isRunning { process.terminate() }
        errors.fileHandleForReading.readabilityHandler = nil
    }
    deinit { close() }
}
private struct CallAudioFailure: LocalizedError { let message: String; init(_ message: String) { self.message = message }; var errorDescription: String? { message } }
