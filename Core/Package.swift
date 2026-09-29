// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "DevSweepCore",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "DevSweepCore", targets: ["DevSweepCore"]),
        .executable(name: "devsweep", targets: ["devsweep"]),
    ],
    dependencies: [
        .package(url: "https://github.com/jpsim/Yams.git", from: "5.1.0"),
    ],
    targets: [
        .target(
            name: "DevSweepCore",
            dependencies: ["Yams"],
            resources: [.copy("Rules")]
        ),
        .executableTarget(
            name: "devsweep",
            dependencies: ["DevSweepCore"]
        ),
        .testTarget(
            name: "DevSweepCoreTests",
            dependencies: ["DevSweepCore"]
        ),
    ]
)
