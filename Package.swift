// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "Envy",
    platforms: [.macOS("15.0")],
    targets: [
        // Model, .env parsing, shell script generation and rc-file editing. No UI, fully tested.
        .target(name: "EnvyCore"),
        // The SwiftUI app: window, menu bar extra, Keychain and persistence.
        .executableTarget(name: "Envy", dependencies: ["EnvyCore"]),
        .testTarget(name: "EnvyCoreTests", dependencies: ["EnvyCore"]),
    ]
)
