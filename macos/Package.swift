// swift-tools-version:5.10
// Core protocol/session library, shared by the SwiftUI app. `swift test` runs on any Mac.
import PackageDescription

let package = Package(
    name: "PixelBridgeKit",
    platforms: [.macOS(.v14)],
    products: [.library(name: "PixelBridgeKit", targets: ["PixelBridgeKit"])],
    targets: [
        .target(name: "PixelBridgeKit", path: "Sources/PixelBridgeKit"),
        .testTarget(name: "PixelBridgeKitTests", dependencies: ["PixelBridgeKit"], path: "Tests/PixelBridgeKitTests"),
    ]
)
