// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "OpenWithCleanerMac",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "CleanerCore", targets: ["CleanerCore"]),
        .executable(name: "OpenWithCleanerMac", targets: ["OpenWithCleanerMac"])
    ],
    targets: [
        .target(name: "CleanerCore"),
        .executableTarget(name: "OpenWithCleanerMac", dependencies: ["CleanerCore"]),
        .testTarget(name: "CleanerCoreTests", dependencies: ["CleanerCore"])
    ]
)
