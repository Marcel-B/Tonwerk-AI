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
        /// How long the stage worked, or has worked so far: its clock stops while it waits for an engine.
        let seconds: Double?
        /// How long it waited meanwhile, shown apart.
        var waited: Double = 0
    }

    /// A render starts from saved tokens, so it only synthesizes and decodes. Planning is skipped when planning is
    /// off, which the worker does not announce: it shows as pending until composing begins and then drops out.
    static let pipeline = ["planning", "tokens", "synth", "decode"]
    static let renderPipeline = ["synth", "decode"]

    static func steps(
        history: [(stage: String, startedAt: Date, waited: Double)], current: String, render: Bool, finished: Bool,
        waitingSince: Date? = nil, now: Date
    ) -> [Step] {
        // A finished song's last entry is its end (ready, failed, cancelled), which the line above says.
        let shown = finished ? Array(history.dropLast()) : history
        var steps = shown.enumerated().map { index, entry -> Step in
            if index + 1 < history.count {
                let next = history[index + 1].startedAt
                let length = max(0, next.timeIntervalSince(entry.startedAt))
                return Step(stage: entry.stage, state: .done, seconds: max(0, length - entry.waited), waited: entry.waited)
            }
            // A wait still running belongs to the current stage.
            let open = waitingSince.map { max(0, now.timeIntervalSince($0)) } ?? 0
            let waited = entry.waited + open
            let length = max(0, now.timeIntervalSince(entry.startedAt))
            return Step(stage: entry.stage, state: .current, seconds: max(0, length - waited), waited: waited)
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
            history: (song.stages ?? []).map { (stage: $0.stage, startedAt: $0.startedAt, waited: $0.waited ?? 0) },
            current: song.stage ?? "queued",
            render: song.render ?? false,
            finished: song.finished ?? false,
            waitingSince: song.waitingSince,
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
