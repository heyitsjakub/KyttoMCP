// swift-tools-version: 6.0
import PackageDescription

// KyttoCore holds every piece of logic that is not UI and not AppKit:
// config parsing, the client registry, detection and normalization.
// It builds and tests without launching the app, which keeps the
// round-trip tests (the ones that de-risk M2) on a fast loop.
let package = Package(
    name: "KyttoCore",
    platforms: [.macOS(.v15)],
    products: [
        .library(name: "KyttoCore", targets: ["KyttoCore"]),
        .executable(name: "kytto-mcp-proxy", targets: ["KyttoGateway"]),
    ],
    targets: [
        .target(
            name: "KyttoCore",
            resources: [
                .process("Catalog/catalog.json"),
                // The cl100k_base rank table (§7.4). Copied rather than
                // processed: it is not a resource type SwiftPM understands, and
                // a byte-exact table is the whole point of bundling one.
                .copy("Tokenizer/cl100k_base.tiktoken"),
            ]
        ),
        .executableTarget(
            name: "KyttoGateway",
            dependencies: ["KyttoCore"],
            linkerSettings: [
                // Xcode embeds the executable beside the app binary and the
                // package framework one directory up. Keep the helper portable
                // after the app leaves DerivedData.
                .unsafeFlags(["-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks"]),
            ]
        ),
        .testTarget(
            name: "KyttoCoreTests",
            dependencies: ["KyttoCore"],
            resources: [.copy("Fixtures")]
        ),
    ]
)
