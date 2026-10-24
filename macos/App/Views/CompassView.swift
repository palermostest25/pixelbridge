import Combine
import PixelBridgeKit
import SwiftUI

@MainActor final class CompassModel: ObservableObject {
    private var tracker = CompassTracker()
    @Published var reading: CompassReading?
    func accept(_ sample: SensorSample) { if tracker.accept(sample) { reading = tracker.reading } }
    func reset() { tracker = CompassTracker(); reading = nil }
}
struct CompassView: View {
    @ObservedObject var compass: CompassModel
    @EnvironmentObject var session: DeviceSession
    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            HStack(alignment: .center, spacing: 42) {
                CompassDial(heading: compass.reading?.heading ?? 0).frame(width: 330, height: 330)
                VStack(alignment: .leading, spacing: 14) {
                    if let reading = compass.reading {
                        Text(String(format: "%.0f° %@", reading.heading, reading.direction)).font(.system(size: 54, weight: .medium, design: .rounded)).monospacedDigit()
                        Text("Magnetic north").font(.headline).foregroundStyle(.secondary)
                        Text(reading.quality).font(.callout).foregroundStyle(reading.accuracy >= 2 ? BridgeStyle.accent : .orange)
                        Divider()
                        LabeledContent("Pitch", value: String(format: "%.1f°", reading.pitch))
                        LabeledContent("Roll", value: String(format: "%.1f°", reading.roll))
                        if let field = reading.fieldMicrotesla { LabeledContent("Magnetic field", value: String(format: "%.1f µT", field)) }
                    } else {
                        Text(session.isConnected ? "Finding north…" : "Connect your phone").font(.title2.weight(.semibold))
                        Text("Uses the phone’s rotation and magnetic sensors. Hold it flat, with its top edge pointing forward.").foregroundStyle(.secondary)
                    }
                }.frame(maxWidth: 340, alignment: .leading)
            }
            Text("Keep the phone away from magnets, speakers and metal surfaces. Readings pause when this view is hidden. This is magnetic heading, not GPS direction of travel.")
                .font(.callout).foregroundStyle(.secondary).frame(maxWidth: 720, alignment: .leading)
        }
    }
}
private struct CompassDial: View {
    let heading: Double
    var body: some View {
        GeometryReader { geometry in
            let side = min(geometry.size.width, geometry.size.height)
            ZStack {
                Circle().stroke(.quaternary, lineWidth: 1)
                ForEach(0..<72) { tick in
                    Rectangle().fill(tick % 18 == 0 ? BridgeStyle.accent : Color.secondary.opacity(tick % 6 == 0 ? 0.65 : 0.25))
                        .frame(width: tick % 6 == 0 ? 2 : 1, height: tick % 6 == 0 ? 17 : 8)
                        .offset(y: -side/2 + 20).rotationEffect(.degrees(Double(tick) * 5 - heading))
                }
                ForEach(Array(["N", "E", "S", "W"].enumerated()), id: \.offset) { index, text in
                    Text(text).font(.headline).foregroundStyle(index == 0 ? BridgeStyle.accent : .secondary)
                        .rotationEffect(.degrees(heading - Double(index)*90))
                        .offset(y: -side/2 + 51).rotationEffect(.degrees(Double(index)*90 - heading))
                }
                VStack(spacing: 0) {
                    Triangle().fill(BridgeStyle.accent).frame(width: 27, height: side * 0.27)
                    Triangle().fill(.secondary.opacity(0.3)).frame(width: 27, height: side * 0.27).rotationEffect(.degrees(180))
                }.rotationEffect(.degrees(-heading))
                Circle().fill(BridgeStyle.surface).frame(width: 14, height: 14).overlay(Circle().stroke(BridgeStyle.accent, lineWidth: 2))
            }.frame(width: side, height: side)
        }.accessibilityElement(children: .ignore).accessibilityLabel("Compass, magnetic heading \(Int(heading)) degrees")
    }
}
private struct Triangle: Shape {
    func path(in rect: CGRect) -> Path { Path { p in p.move(to: CGPoint(x: rect.midX, y: rect.minY)); p.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY)); p.addLine(to: CGPoint(x: rect.minX, y: rect.maxY)); p.closeSubpath() } }
}
