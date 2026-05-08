// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "LanMessenger",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "LanMessenger", targets: ["LanMessenger"]),
    ],
    dependencies: [],
    targets: [
        .executableTarget(
            name: "LanMessenger",
            path: "LanMessenger",
            // Assets.xcassets, Info.plist and entitlements are Xcode-only artefacts;
            // tell SPM to ignore them so it doesn't emit "unhandled file" warnings.
            exclude: [
                "Assets.xcassets",
                "Info.plist",
                "LanMessenger.entitlements",
            ],
            sources: [
                "App",
                "Core/Protocol",
                "Core/Crypto",
                "Core/Networking",
                "Core/Persistence",
                "Core/Services",
                "UI",
            ],
            resources: [],
            swiftSettings: [
                .unsafeFlags(["-framework", "Security"]),
            ]
        ),
        .testTarget(
            name: "LanMessengerTests",
            dependencies: ["LanMessenger"],
            path: "LanMessengerTests",
            resources: [
                .copy("known_good_exchange.json"),
            ]
        ),
    ]
)
