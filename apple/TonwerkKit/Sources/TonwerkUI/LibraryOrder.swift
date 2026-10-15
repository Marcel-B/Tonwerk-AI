import Foundation

/// How the library is ordered, the web page's `sort.ts`. The server lists runs newest first, so `newest` keeps that
/// order; the others keep a run's songs together and move the run by its best song.
public enum LibrarySort: String, CaseIterable, Identifiable, Sendable {
    case newest
    case oldest
    case rating
    case longest
    case shortest
    case fastest
    case slowest

    public var id: String { rawValue }

    var name: String {
        switch self {
        case .newest: String(localized: "Neueste zuerst")
        case .oldest: String(localized: "Älteste zuerst")
        case .rating: String(localized: "Beste Bewertung")
        case .longest: String(localized: "Längste zuerst")
        case .shortest: String(localized: "Kürzeste zuerst")
        case .fastest: String(localized: "Schnellste zuerst")
        case .slowest: String(localized: "Langsamste zuerst")
        }
    }

    private var ascending: Bool { self == .shortest || self == .slowest }

    /// A song's value for the order; nil sorts last whichever way.
    private func key(_ song: Song) -> Double? {
        switch self {
        case .rating: return Double(song.rating ?? 0)
        case .longest, .shortest: return song.seconds
        case .fastest, .slowest: return song.tempo
        case .newest, .oldest: return nil
        }
    }

    /// Whether `a` goes before `b`; equal ones keep their order, which the callers ensure.
    private func before(_ a: Double?, _ b: Double?) -> Bool? {
        switch (a, b) {
        case (nil, nil): return nil
        case (nil, _): return false
        case (_, nil): return true
        case let (a?, b?): return a == b ? nil : (ascending ? a < b : a > b)
        }
    }

    /// The songs in this order. Equal songs and runs keep the server's order (newest first).
    public func apply(to songs: [Song]) -> [Song] {
        var runs: [[Song]] = []
        var position: [String: Int] = [:]
        for song in songs {
            if let index = position[song.run] {
                runs[index].append(song)
            } else {
                position[song.run] = runs.count
                runs.append([song])
            }
        }
        switch self {
        case .newest:
            return songs
        case .oldest:
            return runs.reversed().flatMap { $0 }
        default:
            let sorted = runs.map(stableSorted)
            return stable(sorted) { key($0.first!) }.flatMap { $0 }
        }
    }

    private func stableSorted(_ songs: [Song]) -> [Song] {
        stable(songs) { key($0) }
    }

    private func stable<T>(_ items: [T], by value: (T) -> Double?) -> [T] {
        items.enumerated()
            .sorted { a, b in before(value(a.element), value(b.element)) ?? (a.offset < b.offset) }
            .map(\.element)
    }
}

/// Which songs the library shows by their stars, the web page's rating filter. The songs to review have a page of
/// their own here.
public enum RatingFilter: Hashable, Sendable {
    case all
    /// At least this many stars; five means only five.
    case atLeast(Int)
    case unrated

    /// For `@AppStorage`: 0 all, 1 to 5 at least, -1 unrated.
    public init(stored: Int) {
        switch stored {
        case 1...5: self = .atLeast(stored)
        case -1: self = .unrated
        default: self = .all
        }
    }

    public var stored: Int {
        switch self {
        case .all: 0
        case .atLeast(let stars): stars
        case .unrated: -1
        }
    }

    public static let choices: [RatingFilter] = [.all, .atLeast(5), .atLeast(4), .atLeast(3), .atLeast(2), .atLeast(1), .unrated]

    var name: String {
        switch self {
        case .all: String(localized: "Alle Bewertungen")
        case .atLeast(5): String(localized: "Nur ★★★★★")
        case .atLeast(let stars): String(localized: "Ab \(String(repeating: "★", count: stars))")
        case .unrated: String(localized: "Unbewertet")
        }
    }

    public func admits(_ song: Song) -> Bool {
        switch self {
        case .all: true
        case .atLeast(let stars): (song.rating ?? 0) >= stars
        case .unrated: song.rating == nil
        }
    }
}
