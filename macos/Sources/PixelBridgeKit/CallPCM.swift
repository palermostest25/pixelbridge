import Foundation

/// Streaming mono float input to 16 kHz PCM. Carries interpolation state across input callbacks.
public struct CallPCMResampler {
    private let step: Double
    private var position: Double = 0
    private var previous: Float?
    public init(inputRate: Double) { step = inputRate / 16000 }
    public mutating func convert(_ input: [Float]) -> [Int16] {
        guard !input.isEmpty, step.isFinite, step > 0 else { return [] }
        let samples = previous.map { [$0] + input } ?? input
        var result: [Int16] = []; result.reserveCapacity(Int(Double(samples.count) / step) + 1)
        while position < Double(samples.count - 1) {
            let index = Int(position), fraction = Float(position - Double(index))
            let value = fraction == 0 ? samples[index] : samples[index] + (samples[index + 1] - samples[index]) * fraction
            let normalized = value.isFinite ? max(-1, min(32767.0 / 32768.0, value)) : 0
            result.append(Int16((normalized * 32768).rounded()))
            position += step
        }
        position -= Double(samples.count - 1); previous = samples.last
        return result
    }
}
