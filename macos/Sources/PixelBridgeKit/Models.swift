import Foundation

public enum SensorType {
    public static let accelerometer: Int32 = 1, magneticField: Int32 = 2, gyroscope: Int32 = 4, light: Int32 = 5, pressure: Int32 = 6
    public static let proximity: Int32 = 8, gravity: Int32 = 9, linearAcceleration: Int32 = 10, rotationVector: Int32 = 11, gameRotationVector: Int32 = 15

    public static func name(_ t: Int32) -> String {
        switch t {
        case accelerometer: "Accelerometer"; case gyroscope: "Gyroscope"; case magneticField: "Magnetometer"; case gravity: "Gravity"
        case linearAcceleration: "Linear acceleration"; case rotationVector: "Rotation vector"; case gameRotationVector: "Game rotation vector"
        case light: "Ambient light"; case pressure: "Barometer"; case proximity: "Proximity"; default: "Sensor \(t)"
        }
    }

    public static func unit(_ t: Int32) -> String {
        switch t {
        case accelerometer, gravity, linearAcceleration: "m/s²"; case gyroscope: "rad/s"; case magneticField: "µT"
        case light: "lx"; case pressure: "hPa"; case proximity: "cm"; default: ""
        }
    }
}

public struct SensorSample: Sendable {
    public let type: Int32
    public let accuracy: Int8
    public let values: [Float]
    public let timestampNs: Int64

    /// SENSOR_SAMPLE payload (section 5): int32 type, int8 accuracy, uint8 n, 2 reserved, n × float32, all big-endian.
    public init(frame: Frame) throws {
        let p = frame.payload
        guard p.count >= 8 else { throw ProtocolError.malformed("sensor payload too short") }
        let b = p.startIndex
        type = Int32(bitPattern: p.readBE(UInt32.self, at: b))
        accuracy = Int8(bitPattern: p[b + 4])
        let n = Int(p[b + 5])
        guard n <= 16, p.count == 8 + 4 * n else { throw ProtocolError.malformed("sensor payload length") }
        var v = [Float]()
        for i in 0..<n {
            let f = Float(bitPattern: p.readBE(UInt32.self, at: b + 8 + 4 * i))
            guard f.isFinite else { throw ProtocolError.malformed("non-finite sensor value") }
            v.append(f)
        }
        values = v
        timestampNs = frame.timestampNs
    }
}

public struct BatteryStatus: Sendable, Equatable {
    public let percent: Int, tempC: Double?, charging: Bool, plugged: String, voltageMv: Int, currentMa: Int?, health: String

    public init(json j: [String: Any]) {
        percent = j["percent"] as? Int ?? -1
        tempC = j["tempC"] as? Double
        charging = j["charging"] as? Bool ?? false
        plugged = j["plugged"] as? String ?? ""
        voltageMv = j["voltageMv"] as? Int ?? -1
        currentMa = j["currentMa"] as? Int
        health = j["health"] as? String ?? "unknown"
    }
}

/// LOCATION_SAMPLE (section 5a). Sensitive: never log coordinates.
public struct LocationFix: Sendable, Equatable {
    public let lat: Double, lon: Double, altM: Double?, accM: Double?, speedMps: Double?, bearingDeg: Double?
    public let time: Date, provider: String, stale: Bool, satellitesUsed: Int, satellitesVisible: Int

    public init(json j: [String: Any]) throws {
        guard let lat = j["lat"] as? Double, let lon = j["lon"] as? Double, abs(lat) <= 90, abs(lon) <= 180 else {
            throw ProtocolError.malformed("invalid coordinates")
        }
        self.lat = lat; self.lon = lon
        altM = j["altM"] as? Double; accM = j["accM"] as? Double; speedMps = j["speedMps"] as? Double; bearingDeg = j["bearingDeg"] as? Double
        time = Date(timeIntervalSince1970: Double(j["timeMs"] as? Int64 ?? Int64(j["timeMs"] as? Int ?? 0)) / 1000)
        provider = j["provider"] as? String ?? "unknown"
        stale = j["stale"] as? Bool ?? false
        satellitesUsed = j["satellitesUsed"] as? Int ?? 0
        satellitesVisible = j["satellitesVisible"] as? Int ?? 0
    }
}

/// Host monotonic clock in nanoseconds.
public func hostNowNs() -> Int64 { Int64(clock_gettime_nsec_np(CLOCK_UPTIME_RAW)) }

/// Device ↔ host clock offset from HEARTBEAT/PONG (min-RTT of last 16 samples).
public struct ClockSync {
    private var samples: [(rtt: Int64, offset: Int64)] = []
    public private(set) var offsetNs: Int64 = 0
    public private(set) var lastRttMs: Double = 0
    public var hasEstimate: Bool { !samples.isEmpty }
    public init() {}

    public mutating func add(hostSent: Int64, device: Int64, hostRecv: Int64) {
        let rtt = hostRecv - hostSent
        guard rtt >= 0 else { return }
        samples.append((rtt, device - (hostSent + hostRecv) / 2))
        if samples.count > 16 { samples.removeFirst() }
        offsetNs = samples.min { $0.rtt < $1.rtt }!.offset
        lastRttMs = Double(rtt) / 1e6
    }

    public func toHostNs(_ deviceNs: Int64) -> Int64 { deviceNs - offsetNs }
}
