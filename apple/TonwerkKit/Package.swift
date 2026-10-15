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
            ]
        ),
        // The apps' screens and player, so the Xcode project only holds the app's entry point and a CI `swift build`
        // compiles nearly everything. Swift 5 mode: AVFoundation's and MediaPlayer's callbacks are not yet annotated
        // for strict concurrency.
        .target(
            name: "TonwerkUI",
            dependencies: ["TonwerkKit"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .testTarget(
            name: "TonwerkUITests",
            dependencies: ["TonwerkUI"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .testTarget(
            name: "TonwerkKitTests",
            dependencies: [
                "TonwerkKit",
                .product(name: "OpenAPIRuntime", package: "swift-openapi-runtime"),
                .product(name: "HTTPTypes", package: "swift-http-types"),
            ],
            resources: [.copy("Fixtures")]
        ),
    ]
)
