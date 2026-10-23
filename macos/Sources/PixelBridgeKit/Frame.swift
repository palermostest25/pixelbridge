import Foundation

/// Message type codes (protocol/PROTOCOL.md section 2).
public enum MsgType {
    public static let hello: UInt8 = 0x01, capabilities: UInt8 = 0x02, authResponse: UInt8 = 0x04, authOk: UInt8 = 0x05, authFail: UInt8 = 0x06
    public static let startStream: UInt8 = 0x10, stopStream: UInt8 = 0x11, streamStarted: UInt8 = 0x12, streamStopped: UInt8 = 0x13
    public static let streamError: UInt8 = 0x14, status: UInt8 = 0x15
    public static let videoConfig: UInt8 = 0x20, videoFrame: UInt8 = 0x21, requestIdr: UInt8 = 0x22
    public static let audioConfig: UInt8 = 0x30, audioFrame: UInt8 = 0x31
    public static let sensorSample: UInt8 = 0x40, batteryStatus: UInt8 = 0x41, networkStatus: UInt8 = 0x42
    public static let notificationEvent: UInt8 = 0x43, locationSample: UInt8 = 0x44
    public static let heartbeat: UInt8 = 0x50, pong: UInt8 = 0x51, error: UInt8 = 0x7F
    public static let radioRequest: UInt8 = 0x60, radioResponse: UInt8 = 0x61, radioEvent: UInt8 = 0x62
}

public struct FrameFlags: OptionSet, Sendable {
    public let rawValue: UInt16
    public init(rawValue: UInt16) { self.rawValue = rawValue }
    public static let keyframe = FrameFlags(rawValue: 1)
    public static let codecConfig = FrameFlags(rawValue: 2)
    public static let endOfStream = FrameFlags(rawValue: 4)
    public static let known: FrameFlags = [.keyframe, .codecConfig, .endOfStream]
}

public struct Frame: Sendable {
    public static let version: UInt8 = 1
    public static let headerSize = 24

    public var type: UInt8
    public var payload: Data
    public var flags: FrameFlags = []
    public var timestampNs: Int64 = 0

    public init(type: UInt8, payload: Data, flags: FrameFlags = [], timestampNs: Int64 = 0) {
        self.type = type; self.payload = payload; self.flags = flags; self.timestampNs = timestampNs
    }

    public static func json(_ type: UInt8, _ object: Any, timestampNs: Int64 = 0) -> Frame {
        Frame(type: type, payload: (try? JSONSerialization.data(withJSONObject: object)) ?? Data("{}".utf8), timestampNs: timestampNs)
    }

    public var jsonObject: [String: Any]? { (try? JSONSerialization.jsonObject(with: payload)) as? [String: Any] }
    public var text: String { String(decoding: payload, as: UTF8.self) }

    /// Big-endian 24-byte header + payload (section 1).
    public func encode(sequence: UInt64) -> Data {
        var d = Data(capacity: Frame.headerSize + payload.count)
        d.appendBE(UInt32(payload.count))
        d.append(Frame.version)
        d.append(type)
        d.appendBE(flags.rawValue)
        d.appendBE(sequence)
        d.appendBE(UInt64(bitPattern: timestampNs))
        d.append(payload)
        return d
    }
}

public enum ProtocolError: Error, CustomStringConvertible {
    case badVersion(UInt8), tooLarge(UInt32, Int), unknownFlags(UInt16), badSequence(UInt64, UInt64), closed, malformed(String)
    public var description: String {
        switch self {
        case .badVersion(let v): "unsupported version \(v)"
        case .tooLarge(let n, let max): "payload \(n) exceeds \(max)"
        case .unknownFlags(let f): "unknown flags \(f)"
        case .badSequence(let got, let want): "sequence \(got) != \(want)"
        case .closed: "connection closed"
        case .malformed(let m): m
        }
    }
}

public enum ChannelLimits {
    public static func maxPayload(_ channel: String) -> Int {
        switch channel { case "video": 2 * 1024 * 1024; case "audio": 16384; default: 65536 }
    }
}

/// Incremental validating decoder: feed bytes, pull complete frames.
public struct FrameDecoder {
    private var buffer = Data()
    private var expected: UInt64 = 0
    private let maxPayload: Int

    public init(maxPayload: Int) { self.maxPayload = maxPayload }

    public mutating func feed(_ bytes: Data) { buffer.append(bytes) }

    /// Returns the next frame, nil if more bytes are needed, or throws on any protocol violation.
    public mutating func next() throws -> Frame? {
        guard buffer.count >= Frame.headerSize else { return nil }
        let base = buffer.startIndex
        let length = buffer.readBE(UInt32.self, at: base)
        let version = buffer[base + 4]
        let type = buffer[base + 5]
        let flags = buffer.readBE(UInt16.self, at: base + 6)
        let seq = buffer.readBE(UInt64.self, at: base + 8)
        let ts = Int64(bitPattern: buffer.readBE(UInt64.self, at: base + 16))
        guard version == Frame.version else { throw ProtocolError.badVersion(version) }
        guard Int(length) <= maxPayload else { throw ProtocolError.tooLarge(length, maxPayload) }
        guard FrameFlags(rawValue: flags).subtracting(.known).isEmpty else { throw ProtocolError.unknownFlags(flags) }
        guard buffer.count >= Frame.headerSize + Int(length) else { return nil }
        guard seq == expected else { throw ProtocolError.badSequence(seq, expected) }
        expected += 1
        let payload = buffer.subdata(in: (base + Frame.headerSize)..<(base + Frame.headerSize + Int(length)))
        buffer.removeSubrange(base..<(base + Frame.headerSize + Int(length)))
        return Frame(type: type, payload: payload, flags: FrameFlags(rawValue: flags), timestampNs: ts)
    }
}

extension Data {
    mutating func appendBE<T: FixedWidthInteger>(_ v: T) {
        var be = v.bigEndian
        Swift.withUnsafeBytes(of: &be) { append(contentsOf: $0) }
    }

    func readBE<T: FixedWidthInteger>(_: T.Type, at offset: Int) -> T {
        var v: T = 0
        for i in 0..<MemoryLayout<T>.size { v = (v << 8) | T(self[offset + i]) }
        return v
    }
}
