import Foundation

public struct CompassReading: Sendable {
    public let heading: Double, pitch: Double, roll: Double
    public let fieldMicrotesla: Double?
    public let accuracy: Int
    public var direction: String { ["N", "NE", "E", "SE", "S", "SW", "W", "NW"][Int((heading / 45).rounded()) % 8] }
    public var quality: String { accuracy >= 3 ? "High accuracy" : accuracy == 2 ? "Medium accuracy" : accuracy == 1 ? "Low accuracy—move the phone in a figure eight" : "Unreliable—move away from magnets and calibrate" }
}
public enum CompassMath {
    public static func normalize(_ degrees: Double) -> Double { (degrees.truncatingRemainder(dividingBy: 360) + 360).truncatingRemainder(dividingBy: 360) }
    public static func shortestDelta(from: Double, to: Double) -> Double { normalize(to - from + 180) - 180 }
    public static func quaternion(_ q: [Float], accuracy: Int, field: Double? = nil) -> CompassReading? {
        guard q.count >= 3, q.allSatisfy(\.isFinite) else { return nil }
        var x = Double(q[0]), y = Double(q[1]), z = Double(q[2])
        var w = q.count >= 4 ? Double(q[3]) : max(0, 1 - x*x - y*y - z*z).squareRoot()
        let norm = (x*x + y*y + z*z + w*w).squareRoot(); guard norm > 1e-8 else { return nil }
        x /= norm; y /= norm; z /= norm; w /= norm
        let heading = normalize(atan2(2 * (x*y - z*w), 1 - 2 * (x*x + z*z)) * 180 / .pi)
        let pitch = asin(max(-1, min(1, -2 * (y*z + x*w)))) * 180 / .pi
        let roll = atan2(-2 * (x*z - y*w), 1 - 2 * (x*x + y*y)) * 180 / .pi
        return CompassReading(heading: heading, pitch: pitch, roll: roll, fieldMicrotesla: field, accuracy: accuracy)
    }
    public static func gravity(_ g: [Float], magnetic m: [Float], accuracy: Int) -> CompassReading? {
        guard g.count >= 3, m.count >= 3, g.allSatisfy(\.isFinite), m.allSatisfy(\.isFinite) else { return nil }
        var ax = Double(g[0]), ay = Double(g[1]), az = Double(g[2])
        let ex = Double(m[0]), ey = Double(m[1]), ez = Double(m[2])
        var hx = ey*az - ez*ay, hy = ez*ax - ex*az, hz = ex*ay - ey*ax
        let h = (hx*hx + hy*hy + hz*hz).squareRoot(), norm = (ax*ax + ay*ay + az*az).squareRoot()
        guard h >= 0.1, norm >= 0.1 else { return nil }
        hx /= h; hy /= h; hz /= h; ax /= norm; ay /= norm; az /= norm
        return CompassReading(heading: normalize(atan2(hy, az*hx - ax*hz) * 180 / .pi), pitch: asin(max(-1, min(1, -ay))) * 180 / .pi,
                              roll: atan2(-ax, az) * 180 / .pi, fieldMicrotesla: (ex*ex + ey*ey + ez*ez).squareRoot(), accuracy: accuracy)
    }
}
public struct CompassTracker {
    private var gravity: [Float]?, magnetic: [Float]?
    private var accuracy = 0
    private var rotationAt: Int64 = 0, lastPublish: Int64 = 0
    public private(set) var reading: CompassReading?
    public init() {}
    public mutating func accept(_ s: SensorSample) -> Bool {
        if s.type == SensorType.gravity || s.type == SensorType.accelerometer { gravity = s.values }
        if s.type == SensorType.magneticField { magnetic = s.values; accuracy = Int(s.accuracy) }
        var next: CompassReading?
        let field = magnetic.map { $0.prefix(3).reduce(0.0) { $0 + Double($1)*Double($1) }.squareRoot() }
        if s.type == SensorType.rotationVector { rotationAt = s.timestampNs; next = CompassMath.quaternion(s.values, accuracy: Int(s.accuracy), field: field) }
        else if s.timestampNs - rotationAt > 1_000_000_000, let gravity, let magnetic { next = CompassMath.gravity(gravity, magnetic: magnetic, accuracy: accuracy) }
        guard let next, s.timestampNs - lastPublish >= 100_000_000 else { return false }
        reading = next; lastPublish = s.timestampNs; return true
    }
}
