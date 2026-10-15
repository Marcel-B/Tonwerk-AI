import Foundation
import OpenAPIRuntime
import OpenAPIURLSession
@_exported import TonwerkAPI

/// One Tonwerk server: the generated client (`api`, one method per operation of the OpenAPI description) set up for
/// the server's JSON, plus the live event stream, which OpenAPI cannot describe in types.
public struct Tonwerk: Sendable {
    public let server: URL
    public let api: Client

    /// - Parameter server: the server's root, as ``ServerAddress/parse(_:)`` makes it; the paths carry `/api` themselves.
    public init(server: URL, session: URLSession = .shared) {
        self.server = server
        api = Client(
            serverURL: server,
            configuration: .tonwerk,
            transport: URLSessionTransport(configuration: .init(session: session)))
    }

    /// For tests and previews: the client over another transport.
    public init(server: URL, transport: any ClientTransport) {
        self.server = server
        api = Client(serverURL: server, configuration: .tonwerk, transport: transport)
    }
}

extension Configuration {
    /// The server writes .NET's dates, with up to seven fractional digits.
    public static let tonwerk = Configuration(dateTranscoder: TonwerkDates())
}
