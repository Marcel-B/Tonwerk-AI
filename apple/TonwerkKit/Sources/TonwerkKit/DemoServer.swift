import Foundation

/// A small Tonwerk server inside the app, for trying it without one: App Review and anyone who has not set up a server
/// yet see a library of three songs (synthesized for this, files in `Demo/`), play and rate them, keep playlists and
/// look at a score. It answers every request to ``url`` through `URLSession.shared`, which the generated client and the
/// hand-written calls all use; what needs the real models (creating songs, voices, the tool pages' jobs) answers 503
/// with a problem that says so. Ratings, notes, titles and playlists are kept in memory for this run of the app.
///
/// AVPlayer does not load through `URLProtocol`, so the player asks ``playable(_:)`` for the bundled file instead.
public final class DemoServer: URLProtocol {
    /// Never resolves (`.invalid` is reserved), so nothing leaves the device even if the protocol were not registered.
    public static let url = URL(string: "https://demo.tonwerk.invalid")!

    public static func isDemo(_ server: URL?) -> Bool {
        server?.host == url.host
    }

    private static let registered: Void = { URLProtocol.registerClass(DemoServer.self) }()

    /// Once per process, before the first request to ``url``.
    public static func register() {
        _ = registered
    }

    /// The bundled audio for a song's stream or audio address on the demo server; any other address unchanged.
    public static func playable(_ address: URL) -> URL {
        guard isDemo(address), let file = Route(address.path).songFile.flatMap(Store.audioFile) else { return address }
        return file
    }

    // MARK: URLProtocol

    override public class func canInit(with request: URLRequest) -> Bool {
        isDemo(request.url)
    }

    override public class func canonicalRequest(for request: URLRequest) -> URLRequest {
        request
    }

    private var pings: Timer?

    override public func startLoading() {
        guard let address = request.url else { return }
        let route = Route(address.path)
        let method = request.httpMethod ?? "GET"
        if method == "GET", route.parts == ["api", "events"] {
            streamEvents(address)
            return
        }
        let answer = Store.shared.answer(method: method, route: route, body: readBody())
        let response = HTTPURLResponse(
            url: address, statusCode: answer.status, httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": answer.type, "Content-Length": String(answer.body.count)])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: answer.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override public func stopLoading() {
        pings?.invalidate()
        pings = nil
    }

    /// The snapshot, then a ping now and then as the real server sends, so the connection stays open and live.
    private func streamEvents(_ address: URL) {
        let response = HTTPURLResponse(
            url: address, statusCode: 200, httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": "text/event-stream", "Cache-Control": "no-cache"])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        let snapshot = String(decoding: Store.resource("status", "json") ?? Data("{}".utf8), as: UTF8.self)
            .replacingOccurrences(of: "\n", with: "")
        client?.urlProtocol(self, didLoad: Data("event: snapshot\ndata: \(snapshot)\n\n".utf8))
        let timer = Timer(timeInterval: 15, target: self, selector: #selector(ping), userInfo: nil, repeats: true)
        RunLoop.current.add(timer, forMode: .common)
        pings = timer
    }

    @objc private func ping() {
        client?.urlProtocol(self, didLoad: Data("event: ping\ndata: {}\n\n".utf8))
    }

    /// The request's JSON; URLSession hands a body to a protocol as a stream.
    private func readBody() -> Data {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 16_384)
        while true {
            let count = stream.read(&buffer, maxLength: buffer.count)
            guard count > 0 else { break }
            data.append(buffer, count: count)
        }
        return data
    }
}

/// A request's path, split at the slashes.
struct Route {
    let parts: [String]

    init(_ path: String) {
        parts = path.split(separator: "/").map { String($0).removingPercentEncoding ?? String($0) }
    }

    /// `run/songN` for `api/songs/{run}/{song}/…`.
    var song: (run: String, name: String)? {
        guard parts.count >= 4, parts[0] == "api", parts[1] == "songs" else { return nil }
        return (parts[2], parts[3])
    }

    /// The song whose audio the address asks for.
    var songFile: String? {
        guard let song, parts.count == 5, ["stream", "audio"].contains(parts[4]) else { return nil }
        return "\(song.run)/\(song.name)"
    }

    func matches(_ pattern: String) -> Bool {
        let wanted = pattern.split(separator: "/")
        return wanted.count == parts.count && zip(wanted, parts).allSatisfy { $0 == "*" || $0 == $1 }
    }
}

/// The demo's library and playlists, changed by rating, notes, renaming and playlist edits while the app runs.
final class Store: @unchecked Sendable {
    static let shared = Store()

    struct Answer {
        var status: Int
        var type: String
        var body: Data
    }

    private let lock = NSLock()
    private var runs: [[String: Any]]
    private var playlists: [[String: Any]]

    private init() {
        runs = Self.json("library") as? [[String: Any]] ?? []
        playlists = Self.json("playlists") as? [[String: Any]] ?? []
    }

    static func resource(_ name: String, _ type: String) -> Data? {
        Bundle.module.url(forResource: name, withExtension: type, subdirectory: "Demo").flatMap { try? Data(contentsOf: $0) }
    }

    private static func json(_ name: String) -> Any? {
        resource(name, "json").flatMap { try? JSONSerialization.jsonObject(with: $0) }
    }

    /// Each run's songs share the run's piece; the files are named after the run's title.
    static func audioFile(_ song: String) -> URL? {
        let run = song.split(separator: "/").first.map(String.init) ?? ""
        let name = run.split(separator: "-").dropFirst(2).joined(separator: "-").lowercased()
        return Bundle.module.url(forResource: name, withExtension: "m4a", subdirectory: "Demo")
    }

    /// Tool pages read these lists when they open; empty, they show their empty state instead of an error.
    private static let emptyLists = [
        "api/templates", "api/voices", "api/stems", "api/swaps", "api/queue", "api/sketches", "api/logic/presets",
        "api/instruments", "api/logic/synths/presets", "api/logic/synths/tracks", "api/stems/models",
        "api/songs/*/*/videos", "api/songs/*/*/images",
    ]

    func answer(method: String, route: Route, body: Data) -> Answer {
        lock.lock()
        defer { lock.unlock() }
        let request = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any] ?? [:]
        switch (method, route) {
        case ("GET", let r) where r.matches("api/status"):
            return Self.file("status", "json", "application/json")
        case ("GET", let r) where r.matches("api/library"):
            return .json(runs)
        case ("GET", let r) where r.matches("api/playlists"):
            return .json(playlists)
        case ("GET", let r) where r.matches("api/health"):
            return Answer(status: 200, type: "text/plain", body: Data("ok".utf8))
        case ("GET", let r) where r.songFile != nil:
            guard let file = r.songFile.flatMap(Self.audioFile), let data = try? Data(contentsOf: file) else {
                return .problem(404)
            }
            return Answer(status: 200, type: "audio/mp4", body: data)
        case ("GET", let r) where r.matches("api/songs/*/*/score"):
            return Self.file("score", "abc", "text/vnd.abc")
        case ("GET", let r) where r.matches("api/songs/*/*/request"):
            return songRequest(r).map { Answer.json($0) } ?? Answer.problem(404)
        case ("GET", let r) where r.matches("api/instruments/assignments"):
            return .json([String: Any]())
        case ("GET", let r) where Self.emptyLists.contains(where: r.matches):
            return .json([Any]())
        case ("PUT", let r) where r.matches("api/songs/*/*/rating"):
            changeSong(r) { $0["rating"] = request["rating"] ?? NSNull() }
            return Answer(status: 204, type: "text/plain", body: Data())
        case ("PUT", let r) where r.matches("api/songs/*/*/note"):
            changeSong(r) { $0["note"] = request["note"] ?? NSNull() }
            return Answer(status: 204, type: "text/plain", body: Data())
        case ("PUT", let r) where r.matches("api/runs/*/title"):
            if let index = runs.firstIndex(where: { $0["id"] as? String == r.parts[2] }) {
                let title = (request["title"] as? String) ?? ""
                runs[index]["title"] = title.isEmpty ? runs[index]["originalTitle"] : title
            }
            return Answer(status: 204, type: "text/plain", body: Data())
        case ("PUT", let r) where r.matches("api/playlists/*/songs"):
            guard let index = playlists.firstIndex(where: { "\($0["id"] ?? "")" == r.parts[2] }) else {
                return .problem(404)
            }
            playlists[index]["songIds"] = request["songIds"] as? [String] ?? []
            return .json(playlists[index])
        case ("POST", let r) where r.matches("api/playlists"):
            let id = (playlists.compactMap { $0["id"] as? Int }.max() ?? 0) + 1
            let list: [String: Any] = ["id": id, "name": request["name"] as? String ?? "Playlist", "songIds": [String]()]
            playlists.append(list)
            var answer = Answer.json(list)
            answer.status = 201
            return answer
        case ("GET", _):
            return .problem(404)
        default:
            return .problem(503)
        }
    }

    private func changeSong(_ route: Route, _ change: (inout [String: Any]) -> Void) {
        guard let song = route.song else { return }
        let id = "\(song.run)/\(song.name)"
        for (r, run) in runs.enumerated() {
            guard var songs = run["songs"] as? [[String: Any]],
                  let s = songs.firstIndex(where: { $0["id"] as? String == id })
            else { continue }
            change(&songs[s])
            runs[r]["songs"] = songs
        }
    }

    /// What "use everything" puts into the form: the run's style and lyrics, the song's seed and quality, the score.
    private func songRequest(_ route: Route) -> [String: Any]? {
        guard let song = route.song, let run = runs.first(where: { $0["id"] as? String == song.run }),
              let info = (run["songs"] as? [[String: Any]])?.first(where: { $0["id"] as? String == "\(song.run)/\(song.name)" })
        else { return nil }
        let lyrics = run["lyrics"] as? String ?? ""
        return [
            "title": run["title"] ?? "", "style": run["style"] ?? "", "lyrics": lyrics,
            "instrumental": lyrics.isEmpty, "quality": info["quality"] ?? NSNull(), "cot": NSNull(),
            "seed": info["seed"] ?? NSNull(), "engines": NSNull(), "draftSteps": NSNull(), "maxTokens": NSNull(),
            "abc": Self.resource("score", "abc").map { String(decoding: $0, as: UTF8.self) } ?? NSNull(),
            "fullSteps": NSNull(),
        ]
    }

    private static func file(_ name: String, _ type: String, _ contentType: String) -> Answer {
        guard let data = resource(name, type) else { return .problem(404) }
        return Answer(status: 200, type: contentType, body: data)
    }
}

extension Store.Answer {
    static func json(_ value: Any) -> Self {
        let data = (try? JSONSerialization.data(withJSONObject: value)) ?? Data("null".utf8)
        return .init(status: 200, type: "application/json", body: data)
    }

    /// The real server's error shape, with words the app shows.
    static func problem(_ status: Int) -> Self {
        let detail = status == 404
            ? "Im Demo-Modus gibt es das nicht."
            : "Im Demo-Modus nicht möglich: Dafür braucht es deinen eigenen Tonwerk-Server."
        let body: [String: Any] = ["title": "Demo", "status": status, "detail": detail]
        let data = (try? JSONSerialization.data(withJSONObject: body)) ?? Data()
        return .init(status: status, type: "application/problem+json", body: data)
    }
}
