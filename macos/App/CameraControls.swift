import Foundation

struct PhoneCamera: Identifiable {
    let id: String
    let facing: String
    let minimumZoom: Double
    let maximumZoom: Double
    let supportsOptical: Bool
    let supportsElectronic: Bool

    init?(_ data: [String: Any]) {
        guard let id = data["id"] as? String else { return nil }
        self.id = id
        facing = data["facing"] as? String ?? "back"
        minimumZoom = max(0.1, (data["minZoom"] as? NSNumber)?.doubleValue ?? 1)
        maximumZoom = max(minimumZoom, (data["maxZoom"] as? NSNumber)?.doubleValue ?? 1)
        supportsOptical = data["opticalStabilization"] as? Bool ?? false
        supportsElectronic = data["videoStabilization"] as? Bool ?? false
    }
}

struct CameraLens: Identifiable {
    let cameraID: String
    let kind: String
    let title: String
    let symbol: String
    let zoom: Double
    var id: String { cameraID + "-" + kind }
}
