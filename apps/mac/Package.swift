// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "UDriver",
    platforms: [.macOS(.v14)],
    targets: [.executableTarget(name: "UDriver", path: "Sources")]
)
