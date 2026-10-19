import Foundation
import OpenAPIRuntime

/// One event of `GET /api/events`. The server describes the names and their data in its schema `ServerEvents`
/// (Status/ServerEvents.cs); an event this version does not know yet arrives as ``unknown(_:)``.
public enum ServerEvent: Sendable {
    /// First on every connection: everything at once. A dropped stream reconnects and starts with one again.
    case snapshot(Components.Schemas.StatusSnapshot)
    /// Sent while nothing else happens, so a dead connection shows.
    case ping
    /// Songs were added, renamed, rated or deleted: read the library again.
    case library
    case song(Components.Schemas.SongState)
    case transcription(Components.Schemas.TranscriptionState)
    case log(Components.Schemas.LogEntry)
    case worker(Components.Schemas.WorkerInfo)
    case lyrics(Components.Schemas.LyricsState)
    /// The whole queue of waiting jobs.
    case queue([Components.Schemas.QueuedJob])
    case memory(Components.Schemas.MemoryInfo)
    case version(Components.Schemas.VersionState)
    case stems(Components.Schemas.StemSetState)
    case swap(Components.Schemas.SwapState)
    case video(Components.Schemas.VideoState)
    case image(Components.Schemas.ImageState)
    case ace(Components.Schemas.AceJob)
    case speech(Components.Schemas.SpeechTake)
    case singing(Components.Schemas.SingTake)
    case unknown(String)

    public init(name: String, data: String) throws {
        let json = Data(data.utf8)
        let decoder = JSONDecoder.tonwerk()
        func decode<T: Decodable>(_: T.Type = T.self) throws -> T { try decoder.decode(T.self, from: json) }
        switch name {
        case "snapshot": self = .snapshot(try decode())
        case "ping": self = .ping
        case "library": self = .library
        case "song": self = .song(try decode())
        case "transcription": self = .transcription(try decode())
        case "log": self = .log(try decode())
        case "worker": self = .worker(try decode())
        case "lyrics": self = .lyrics(try decode())
        case "queue": self = .queue(try decode())
        case "memory": self = .memory(try decode())
        case "version": self = .version(try decode())
        case "stems": self = .stems(try decode())
        case "swap": self = .swap(try decode())
        case "video": self = .video(try decode())
        case "image": self = .image(try decode())
        case "ace": self = .ace(try decode())
        case "speech": self = .speech(try decode())
        case "singing": self = .singing(try decode())
        default: self = .unknown(name)
        }
    }

    /// The events in a body of `text/event-stream`.
    public static func events(in body: HTTPBody) -> AsyncThrowingStream<ServerEvent, any Error> {
        let events = body.asDecodedServerSentEvents()
        return AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    for try await event in events {
                        continuation.yield(try ServerEvent(name: event.event ?? "message", data: event.data ?? "{}"))
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
}

extension Tonwerk {
    /// One connection to the live state. It ends when the server drops it (a restart, or a phone that fell behind);
    /// connecting again starts with a fresh ``ServerEvent/snapshot(_:)``.
    public func events() async throws -> AsyncThrowingStream<ServerEvent, any Error> {
        let body = try await api.streamEvents().ok.body.textEventStream
        return ServerEvent.events(in: body)
    }
}
