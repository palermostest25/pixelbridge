import AVFoundation
import CoreAudio
import Foundation
import PixelBridgeKit

struct AudioDevice: Identifiable, Hashable {
    let id: AudioDeviceID
    let name: String
    /// BlackHole / Loopback-style virtual devices make the phone mic selectable as a microphone in other apps.
    var isVirtualCable: Bool { name.localizedCaseInsensitiveContains("BlackHole") || name.localizedCaseInsensitiveContains("Loopback") }
}

/// Phone PCM16 mono 48 kHz → AVAudioEngine → a chosen output device (e.g. BlackHole 2ch). Gain, mute, level meter.
final class AudioPlayer {
    private let engine = AVAudioEngine()
    private let node = AVAudioPlayerNode()
    private let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48000, channels: 1, interleaved: false)!
    private var running = false
    private var queued = 0
    var gain: Float = 1
    var muted = false
    private(set) var levelDbfs: Double = -120
    private(set) var lastFrame = Date.distantPast
    var isLive: Bool { Date().timeIntervalSince(lastFrame) < 1 }

    init() {
        engine.attach(node)
        engine.connect(node, to: engine.mainMixerNode, format: format)
    }

    /// Route to a specific output device (nil = system default output). Never changes the system default.
    func setOutput(_ device: AudioDeviceID?) {
        stop()
        guard let device else { return }
        var id = device
        let unit = engine.outputNode.audioUnit!
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &id, UInt32(MemoryLayout<AudioDeviceID>.size))
        do { try engine.start(); node.play(); running = true } catch { Log.warn("audio output start failed: \(error)") }
    }

    func stop() {
        node.stop(); engine.stop(); running = false; queued = 0
    }

    func push(_ pcm: Data) {
        lastFrame = Date()
        let n = pcm.count / 2
        guard n > 0, let buf = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(n)) else { return }
        buf.frameLength = AVAudioFrameCount(n)
        let out = buf.floatChannelData![0]
        var sumSq: Double = 0
        let g = muted ? 0 : gain
        pcm.withUnsafeBytes { raw in
            let s = raw.bindMemory(to: Int16.self)
            for i in 0..<n {
                let v = max(-1, min(1, Float(Int16(littleEndian: s[i])) / 32768 * g))
                out[i] = v
                sumSq += Double(v * v)
            }
        }
        levelDbfs = 20 * log10(max(sqrt(sumSq / Double(n)), 1e-6))
        guard running, !muted, queued < 8 else { return } // bounded latency: drop rather than queue up
        queued += 1
        node.scheduleBuffer(buf) { [weak self] in self?.queued -= 1 }
    }

    static func outputDevices() -> [AudioDevice] {
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size)
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &ids)
        return ids.compactMap { id in
            var streams = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreams, mScope: kAudioDevicePropertyScopeOutput, mElement: kAudioObjectPropertyElementMain)
            var ssize: UInt32 = 0
            AudioObjectGetPropertyDataSize(id, &streams, 0, nil, &ssize)
            guard ssize > 0 else { return nil } // input-only device
            var nameAddr = AudioObjectPropertyAddress(mSelector: kAudioObjectPropertyName, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            var name: Unmanaged<CFString>?
            var nsize = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
            AudioObjectGetPropertyData(id, &nameAddr, 0, nil, &nsize, &name)
            return AudioDevice(id: id, name: (name?.takeRetainedValue() as String?) ?? "Device \(id)")
        }
    }
}
