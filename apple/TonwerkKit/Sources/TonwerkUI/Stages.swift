import Foundation
import TonwerkKit

/// The worker's stages in German, and a song's way through them, the web page's `SongTimeline.vue`.
enum Stages {
    /// The long name, for the line above the timeline (`stage_…` in `i18n.ts`).
    static func name(_ stage: String) -> String {
        switch stage {
        case "queued": String(localized: "Wartet")
        case "planning": String(localized: "Plant")
        case "tokens": String(localized: "Komponiert")
        case "synth": String(localized: "Synthetisiert")
        case "decode": String(localized: "Rendert Audio")
        case "ready": String(localized: "Fertig")
        case "failed": String(localized: "Fehlgeschlagen")
        case "cancelled": String(localized: "Abgebrochen")
        default: stage
        }
    }

    /// The short name under a step (`step_…`).
    static func step(_ stage: String) -> String {
        switch stage {
        case "queued": String(localized: "Warten")
        case "planning": String(localized: "Partitur")
        case "tokens": String(localized: "Tokens")
        case "synth": String(localized: "Synthese")
        case "decode": String(localized: "Audio")
        default: name(stage)
        }
    }

    struct Step: Equatable {
        enum State: Equatable {
            case done
            case current
            case pending
        }

        let stage: String
        let state: State
        /// How long the stage took, or has taken so far.
        let seconds: Double?
    }

    /// A render starts from saved tokens, so it only synthesizes and decodes. Planning is skipped when planning is
    /// off, which the worker does not announce: it shows as pending until composing begins and then drops out.
    static let pipeline = ["planning", "tokens", "synth", "decode"]
    static let renderPipeline = ["synth", "decode"]

    static func steps(
        history: [(stage: String, startedAt: Date)], current: String, render: Bool, finished: Bool, now: Date
    ) -> [Step] {
        // A finished song's last entry is its end (ready, failed, cancelled), which the line above says.
        let shown = finished ? Array(history.dropLast()) : history
        var steps = shown.enumerated().map { index, entry -> Step in
            if index + 1 < history.count {
                let next = history[index + 1].startedAt
                return Step(stage: entry.stage, state: .done, seconds: max(0, next.timeIntervalSince(entry.startedAt)))
            }
            return Step(stage: entry.stage, state: .current, seconds: max(0, now.timeIntervalSince(entry.startedAt)))
        }
        if !finished {
            let ahead = render ? renderPipeline : pipeline
            let reached = ahead.firstIndex(of: current) ?? -1
            steps += ahead.dropFirst(reached + 1).map { Step(stage: $0, state: .pending, seconds: nil) }
        }
        return steps
    }

    static func steps(of song: Components.Schemas.SongState, now: Date) -> [Step] {
        steps(
            history: (song.stages ?? []).map { (stage: $0.stage, startedAt: $0.startedAt) },
            current: song.stage ?? "queued",
            render: song.render ?? false,
            finished: song.finished ?? false,
            now: now)
    }

    /// A waiting job's kind, as the queue page names it.
    static func kind(_ kind: Components.Schemas.JobKind) -> (name: String, symbol: String) {
        switch kind {
        case .song: (String(localized: "Song"), "music.note")
        case .render: (String(localized: "Voll rendern"), "waveform")
        case .lyrics: (String(localized: "Songtext"), "text.quote")
        case .transcription: (String(localized: "Transkription"), "music.quarternote.3")
        }
    }
}
