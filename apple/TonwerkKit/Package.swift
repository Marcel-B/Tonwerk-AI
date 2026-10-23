// swift-tools-version: 6.0
import PackageDescription

// What the iPhone and Mac apps share: the client generated from the server's OpenAPI description
// (Sources/TonwerkAPI/openapi.json, written by the server's tests) and the hand-written parts around it.
let package = Package(
    name: "TonwerkKit",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        .library(name: "TonwerkKit", targets: ["TonwerkKit"]),
        .library(name: "TonwerkUI", targets: ["TonwerkUI"]),
    ],
    dependencies: [
        .package(url: "https://github.com/apple/swift-openapi-generator", from: "1.7.0"),
        .package(url: "https://github.com/apple/swift-openapi-runtime", from: "1.7.0"),
        .package(url: "https://github.com/apple/swift-openapi-urlsession", from: "1.0.0"),
        .package(url: "https://github.com/apple/swift-http-types", from: "1.0.0"),
        // Updates of the Mac app downloaded from GitHub (Updates.swift); MIT, see NOTICE.
        .package(url: "https://github.com/sparkle-project/Sparkle", from: "2.9.6"),
    ],
    targets: [
        .target(
            name: "TonwerkAPI",
            dependencies: [.product(name: "OpenAPIRuntime", package: "swift-openapi-runtime")],
            plugins: [.plugin(name: "OpenAPIGenerator", package: "swift-openapi-generator")]
        ),
        .target(
            name: "TonwerkKit",
            dependencies: [
                "TonwerkAPI",
                .product(name: "OpenAPIRuntime", package: "swift-openapi-runtime"),
                .product(name: "OpenAPIURLSession", package: "swift-openapi-urlsession"),
            ],
            // The demo server's library, playlists, score and three songs synthesized for it (DemoServer.swift).
            resources: [.copy("Demo")]
        ),
        // Tonwerk's synthesizers: the Tonwerk Synth plugin's plain C++ DSP behind a C interface (include/TonwerkDSP.h).
        .target(name: "TonwerkDSP"),
        // The apps' screens and player, so the Xcode project only holds the app's entry point and a CI `swift build`
        // compiles nearly everything. Swift 5 mode: AVFoundation's and MediaPlayer's callbacks are not yet annotated
        // for strict concurrency.
        .target(
            name: "TonwerkUI",
            dependencies: [
                "TonwerkKit",
                "TonwerkDSP",
                .product(name: "Sparkle", package: "Sparkle", condition: .when(platforms: [.macOS])),
            ],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .testTarget(
            name: "TonwerkUITests",
            dependencies: ["TonwerkUI", "TonwerkDSP"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .testTarget(name: "TonwerkDSPTests", dependencies: ["TonwerkDSP"]),
        .testTarget(
            name: "TonwerkKitTests",
            dependencies: [
                "TonwerkKit",
                .product(name: "OpenAPIRuntime", package: "swift-openapi-runtime"),
                .product(name: "HTTPTypes", package: "swift-http-types"),
            ],
            resources: [.copy("Fixtures")]
        ),
    ],
    cxxLanguageStandard: .cxx20
)
