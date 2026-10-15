# TonwerkKit

What Tonwerk's iPhone and Mac apps share: a client for the server's API, generated at build time by Apple's
[Swift OpenAPI Generator](https://github.com/apple/swift-openapi-generator) from `Sources/TonwerkAPI/openapi.json`,
and the parts OpenAPI cannot describe (the server's address, .NET's dates, the live event stream).

`openapi.json` and the fixtures under `Tests/TonwerkKitTests/Fixtures` are written by the server's tests. After
changing an endpoint:

```sh
TONWERK_UPDATE_OPENAPI=1 dotnet test tests/YueUI.Api.Tests -p:SkipClientAppBuild=true --filter "FullyQualifiedName~OpenApiTests"
```

The server's CI fails while the committed copy differs from what the server describes.

Building needs a Swift 6 toolchain (Xcode 16 on a Mac); `swift build` and `swift test` in this folder. CI runs them on
macOS.

```swift
import TonwerkKit

let tonwerk = Tonwerk(server: ServerAddress.parse("mac-mini.tail6105c0.ts.net:8443")!)
let runs = try await tonwerk.api.listLibrary().ok.body.json
for try await event in try await tonwerk.events() { … }
```
