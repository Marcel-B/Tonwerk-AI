import SwiftUI
import TonwerkKit

/// What the server works on and what waits, from the event stream, like the web page's queue page: a card per song
/// with its timeline, then the waiting jobs, which can be moved and removed.
struct QueueView: View {
    @Environment(AppModel.self) private var model

    private var empty: Bool {
        model.inProgress.isEmpty && model.finishedSongs.isEmpty && model.queue.isEmpty && model.voiceJobs.isEmpty && model.videoJobs.isEmpty
            && model.runningTranscriptions.isEmpty && model.speechJobs.isEmpty && model.singJobs.isEmpty
            && model.aceJobs.isEmpty
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                heading("Speicher")
                ModelOverview()
                if !model.finishedSongs.isEmpty || !model.readySongIds.isEmpty {
                    HStack(spacing: 8) {
                        if !model.finishedSongs.isEmpty {
                            Button {
                                withAnimation { model.hideFinished() }
                            } label: {
                                Label("Erledigte ausblenden", systemImage: "eye.slash")
                            }
                            .help("Fertige, fehlgeschlagene und abgebrochene Songs von dieser Seite nehmen; die Bibliothek behält sie")
                        }
                        if !model.readySongIds.isEmpty {
                            Button {
                                Task { await model.playlistOfReadySongs() }
                            } label: {
                                Label("Als Playlist (\(model.readySongIds.count))", systemImage: "music.note.list")
                            }
                            .help("Die fertigen Songs der Warteschlange als neue Playlist, zum Anhören und Bewerten")
                        }
                    }
                    .buttonStyle(.bordered)
                }
                if empty {
                    VStack(spacing: 8) {
                        Image(systemName: "hourglass")
                            .font(.system(size: 34, weight: .light))
                            .foregroundStyle(.tertiary)
                        Text("Nichts in Arbeit")
                            .font(.headline)
                        Text("Songs, Songtexte, Stimmen und Videos erscheinen hier, sobald sie bestellt sind.")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 40)
                }
                if !model.inProgress.isEmpty {
                    heading("In Arbeit")
                        .padding(.top, 8)
                    ForEach(model.inProgress, id: \.id) { state in
                        WorkingCard(state: state)
                            .transition(.opacity.combined(with: .move(edge: .top)))
                    }
                }
                if !model.finishedSongs.isEmpty {
                    heading("Erledigt")
                        .padding(.top, 8)
                    ForEach(model.finishedSongs, id: \.id) { state in
                        FinishedCard(state: state)
                            .transition(.opacity)
                    }
                }
                if !model.voiceJobs.isEmpty {
                    heading("Stimmen und Stems")
                        .padding(.top, 8)
                    ForEach(model.voiceJobs) { job in
                        VoiceJobCard(job: job)
                            .transition(.opacity)
                    }
                }
                if !model.videoJobs.isEmpty {
                    heading("Videos")
                        .padding(.top, 8)
                    ForEach(model.videoJobs, id: \.id) { video in
                        VideoJobCard(video: video)
                            .transition(.opacity)
                    }
                }
                if !model.runningTranscriptions.isEmpty {
                    heading("Transkription")
                        .padding(.top, 8)
                    ForEach(model.runningTranscriptions, id: \.id) { state in
                        TranscriptionJobCard(state: state)
                            .transition(.opacity)
                    }
                }
                if !model.speechJobs.isEmpty {
                    heading("Sprachlabor")
                        .padding(.top, 8)
                    ForEach(model.speechJobs, id: \.id) { take in
                        SpeechJobCard(take: take)
                            .transition(.opacity)
                    }
                }
                if !model.singJobs.isEmpty {
                    heading("Singen")
                        .padding(.top, 8)
                    ForEach(model.singJobs, id: \.id) { take in
                        SingJobCard(take: take)
                            .transition(.opacity)
                    }
                }
                if !model.aceJobs.isEmpty {
                    heading("ACE-Step")
                        .padding(.top, 8)
                    ForEach(model.aceJobs, id: \.id) { job in
                        AceJobCard(job: job)
                            .transition(.opacity)
                    }
                }
                if !model.queue.isEmpty {
                    heading("Wartet")
                        .padding(.top, 8)
                    ForEach(Array(model.queue.enumerated()), id: \.element.id) { index, job in
                        WaitingCard(job: job, first: index == 0, last: index == model.queue.count - 1)
                            .transition(.opacity)
                    }
                }
                WorkerLog()
                    .padding(.top, 8)
            }
            .padding()
            .frame(maxWidth: 760)
            .frame(maxWidth: .infinity)
            .animation(.easeInOut(duration: 0.25), value: model.inProgress.map(\.id))
            .animation(.easeInOut(duration: 0.25), value: model.finishedSongs.map(\.id))
            .animation(.easeInOut(duration: 0.25), value: model.queue.map(\.id))
            .animation(.easeInOut(duration: 0.25), value: model.voiceJobs.map(\.id))
            .animation(.easeInOut(duration: 0.25), value: model.videoJobs.map(\.id))
        }
        .navigationTitle("Warteschlange")
    }

    private func heading(_ text: LocalizedStringKey) -> some View {
        Text(text)
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(.secondary)
            .textCase(.uppercase)
    }
}

/// A song the worker is on: title, what it does now, and its stages with how long each took.
private struct WorkingCard: View {
    @Environment(AppModel.self) private var model
    let state: Components.Schemas.SongState

    private var title: String {
        state.title.flatMap { $0.isEmpty ? nil : $0 } ?? state.run
    }

    private var stage: String { state.stage ?? "queued" }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top, spacing: 12) {
                DrawnCover(id: state.id)
                    .frame(width: 44, height: 44)
                VStack(alignment: .leading, spacing: 3) {
                    Text(title)
                        .font(.headline)
                        .lineLimit(1)
                    Text(subtitle)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer(minLength: 8)
                Text(Stages.name(stage))
                    .font(.caption.weight(.semibold))
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(Theme.accent.opacity(0.15), in: Capsule())
                    .foregroundStyle(Theme.accent)
                Menu {
                    Button("Abbrechen", systemImage: "xmark.circle", role: .destructive) {
                        Task { await model.cancel(state) }
                    }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
                .menuStyle(.button)
                .buttonStyle(.borderless)
                .menuIndicator(.hidden)
                .fixedSize()
            }
            TimelineView(.periodic(from: .now, by: 1)) { context in
                StageTimeline(steps: Stages.steps(of: state, now: context.date), fraction: state.fraction ?? 0)
            }
            if let detail = state.detail, !detail.isEmpty {
                Text([state.engine, detail].compactMap { $0 }.joined(separator: " · "))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
        .card()
    }

    private var subtitle: String {
        var parts: [String] = []
        if state.render == true {
            parts.append(String(localized: "Voll rendern"))
        } else if let quality = state.quality {
            parts.append(quality == "full" ? String(localized: "Volle Qualität") : String(localized: "Entwurf"))
        }
        if let voice = state.voice?.voiceLabel {
            parts.append(String(localized: "Stimme \(voice)"))
        }
        if let seed = state.seed {
            parts.append("Seed \(seed)")
        }
        return parts.isEmpty ? Stages.name(stage) : parts.joined(separator: " · ")
    }
}

/// A song the worker is done with, folded to one line as on the web page: its end, how long it took in all and the
/// worker's last word; the steps open on request.
private struct FinishedCard: View {
    @Environment(AppModel.self) private var model
    let state: Components.Schemas.SongState
    @State private var showSteps = false

    private var title: String {
        state.title.flatMap { $0.isEmpty ? nil : $0 } ?? state.run
    }

    private var listed: Song? { model.songs.first { $0.id == state.id } }

    /// From the first stage's start to the last's, which is the song's end.
    private var total: Double? {
        guard let stages = state.stages, let first = stages.first, let last = stages.last, stages.count > 1 else {
            return nil
        }
        return last.startedAt.timeIntervalSince(first.startedAt)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(title)
                    .font(.headline)
                    .lineLimit(1)
                if let index = state.index {
                    Text("Song \(index)")
                        .foregroundStyle(.secondary)
                }
                if let voice = state.voice?.voiceLabel {
                    Label(voice, systemImage: "person")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer(minLength: 8)
                if let song = listed, state.stage == "ready" {
                    Button {
                        let ready = model.readySongIds.compactMap { id in model.songs.first { $0.id == id } }
                        model.player.play(song, in: ready.isEmpty ? [song] : ready)
                    } label: {
                        Image(systemName: "play.circle")
                    }
                    .buttonStyle(.borderless)
                    .help("Abspielen")
                }
            }
            HStack(spacing: 10) {
                Text(Stages.name(state.stage ?? "ready"))
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(state.stage == "failed" ? Color.red : Color.primary)
                if let total {
                    Text("insgesamt \(Song.clock(total))")
                        .font(.subheadline.monospacedDigit())
                        .foregroundStyle(.secondary)
                }
                if !(state.stages ?? []).isEmpty {
                    Button(showSteps ? "Schritte ausblenden" : "Schritte") {
                        withAnimation { showSteps.toggle() }
                    }
                    .buttonStyle(.borderless)
                    .font(.subheadline)
                }
            }
            if let note = state.message ?? state.detail, !note.isEmpty {
                Text(note)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
            }
            if showSteps {
                StageTimeline(steps: Stages.steps(of: state, now: .now), fraction: 1)
            }
        }
        .card()
    }
}

/// The worker's log under the queue, closed by default: its stderr can bring several lines a second.
private struct WorkerLog: View {
    @Environment(AppModel.self) private var model
    @State private var open = false

    var body: some View {
        DisclosureGroup(isExpanded: $open) {
            if model.workerLog.isEmpty {
                Text("Noch keine Einträge.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 2) {
                            ForEach(Array(model.workerLog.enumerated()), id: \.offset) { index, entry in
                                line(entry)
                                    .id(index)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .frame(maxHeight: 320)
                    // Opens at its end and stays there while lines come in, as on the web page.
                    .onAppear { proxy.scrollTo(model.workerLog.count - 1, anchor: .bottom) }
                    .onChange(of: model.workerLog.count) { _, count in
                        proxy.scrollTo(count - 1, anchor: .bottom)
                    }
                }
            }
        } label: {
            Text("Protokoll")
                .font(.headline)
        }
    }

    private func line(_ entry: Components.Schemas.LogEntry) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(entry.time.formatted(date: .omitted, time: .standard))
                .foregroundStyle(.secondary)
            Text(entry.message)
                .foregroundStyle(color(entry.level))
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .font(.caption.monospaced())
        .textSelection(.enabled)
    }

    private func color(_ level: String) -> Color {
        switch level {
        case "error": .red
        case "warning", "warn": .orange
        default: .primary
        }
    }
}

/// The steps side by side: a check for what is done, a ring that fills for the running one, a dot for what comes.
private struct StageTimeline: View {
    let steps: [Stages.Step]
    let fraction: Double

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            ForEach(Array(steps.enumerated()), id: \.offset) { index, step in
                VStack(alignment: .leading, spacing: 6) {
                    HStack(spacing: 0) {
                        marker(step)
                        if index < steps.count - 1 {
                            Rectangle()
                                .fill(step.state == .done ? Theme.accent : Color.secondary.opacity(0.25))
                                .frame(height: 2)
                                .padding(.horizontal, 4)
                        }
                    }
                    VStack(alignment: .leading, spacing: 1) {
                        Text(Stages.step(step.stage))
                            .font(.caption.weight(step.state == .pending ? .regular : .medium))
                            .foregroundStyle(step.state == .pending ? .secondary : .primary)
                        if let seconds = step.seconds {
                            Text(Song.clock(seconds))
                                .font(.caption2.monospacedDigit())
                                .foregroundStyle(.secondary)
                        }
                    }
                    .lineLimit(1)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
    }

    @ViewBuilder
    private func marker(_ step: Stages.Step) -> some View {
        switch step.state {
        case .done:
            Image(systemName: "checkmark.circle.fill")
                .foregroundStyle(Theme.accent)
                .font(.system(size: 16))
        case .current:
            ZStack {
                Circle().stroke(Theme.accent.opacity(0.25), lineWidth: 3)
                Circle()
                    .trim(from: 0, to: min(max(fraction, 0), 1))
                    .stroke(Theme.accent, style: StrokeStyle(lineWidth: 3, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .animation(.easeOut(duration: 0.3), value: fraction)
            }
            .frame(width: 14, height: 14)
            .padding(1)
        case .pending:
            Circle()
                .strokeBorder(Color.secondary.opacity(0.4), lineWidth: 2)
                .frame(width: 16, height: 16)
        }
    }
}

/// A version, stem set or swap: they wait in a queue of their own on the server, one at a time.
private struct VoiceJobCard: View {
    @Environment(AppModel.self) private var model
    let job: VoiceJob

    var body: some View {
        let names = model.title(of: job)
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: job.symbol)
                .font(.title3)
                .foregroundStyle(Theme.accent)
                .frame(width: 44, height: 44)
                .background(Theme.accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            JobRow(title: names.title, detail: names.detail, stage: job.stage, fraction: job.fraction, message: nil) {
                Button(role: .destructive) {
                    Task { await model.cancel(job) }
                } label: {
                    Image(systemName: "xmark")
                }
                .buttonStyle(.borderless)
                .help("Abbrechen")
            }
        }
        .card(padding: 12)
    }
}

/// A music video ffmpeg renders on the Mac; it works beside the models, one video at a time.
private struct VideoJobCard: View {
    @Environment(AppModel.self) private var model
    let video: Components.Schemas.VideoState

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "film")
                .font(.title3)
                .foregroundStyle(Theme.accent)
                .frame(width: 44, height: 44)
                .background(Theme.accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 10) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(model.song(video.songId)?.displayTitle ?? video.title).lineLimit(1)
                        Text(AppModel.describe(video)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Text(AppModel.videoStage(video))
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(.quaternary, in: Capsule())
                    Button(role: .destructive) {
                        Task { try? await model.deleteVideo(video) }
                    } label: {
                        Image(systemName: "xmark")
                    }
                    .buttonStyle(.borderless)
                    .help("Abbrechen")
                }
                if video.stage == "rendering" {
                    ProgressView(value: min(max(video.fraction ?? 0, 0), 1))
                }
            }
        }
        .card(padding: 12)
    }
}

/// The transcription SheetSage2 is on, with its progress; the waiting ones are in the queue below.
private struct TranscriptionJobCard: View {
    @Environment(AppModel.self) private var model
    let state: Components.Schemas.TranscriptionState

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "music.quarternote.3")
                .font(.title3)
                .foregroundStyle(Theme.accent)
                .frame(width: 44, height: 44)
                .background(Theme.accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 10) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(state.fileName).lineLimit(1)
                        Text(TranscriptionSession.Melody.name(of: state.task))
                            .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Text(AppModel.transcriptionStage(state.stage))
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(.quaternary, in: Capsule())
                    Button(role: .destructive) {
                        Task { try? await model.cancelTranscription(state.id) }
                    } label: {
                        Image(systemName: "xmark")
                    }
                    .buttonStyle(.borderless)
                    .help("Abbrechen")
                }
                TranscriptionProgress(state: state)
            }
        }
        .card(padding: 12)
    }
}

/// A take the speech lab speaks or cleans, or one waiting in its line; the lab keeps a line of its own.
private struct SpeechJobCard: View {
    @Environment(AppModel.self) private var model
    let take: Components.Schemas.SpeechTake

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: take.source == nil ? "bubble.left.and.text.bubble.right" : "waveform.badge.minus")
                .font(.title3)
                .foregroundStyle(Theme.accent)
                .frame(width: 44, height: 44)
                .background(Theme.accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 10) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(take.source ?? take.modelLabel).lineLimit(1)
                        Text(take.source == nil ? take.text : take.modelLabel)
                            .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Text(SpeechSession.stageName(take))
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(.quaternary, in: Capsule())
                    Button(role: .destructive) {
                        Task { try? await model.deleteSpeechTake(take.id) }
                    } label: {
                        Image(systemName: "xmark")
                    }
                    .buttonStyle(.borderless)
                    .help("Abbrechen")
                }
                if take.stage == "cleaning", let progress = take.progress {
                    ProgressView(value: min(max(progress, 0), 1))
                }
            }
        }
        .card(padding: 12)
    }
}

/// A take of the singing page in the works, with its stage and a cancel.
private struct SingJobCard: View {
    @Environment(AppModel.self) private var model
    let take: Components.Schemas.SingTake

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "music.mic")
                .font(.title3)
                .foregroundStyle(Theme.accent)
                .frame(width: 44, height: 44)
                .background(Theme.accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 10) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(take.title ?? "").lineLimit(1)
                        Text(take.voiceLabel ?? "").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Text(SingSession.stageName(take))
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(.quaternary, in: Capsule())
                    Button(role: .destructive) {
                        Task { try? await model.deleteSingTake(take.id) }
                    } label: {
                        Image(systemName: "xmark")
                    }
                    .buttonStyle(.borderless)
                    .help("Abbrechen")
                }
                if take.stage == "singing", let progress = take.progress {
                    ProgressView(value: min(max(progress, 0), 1))
                }
            }
        }
        .card(padding: 12)
    }
}

/// An ACE-Step job in the works, with its stage and a cancel.
private struct AceJobCard: View {
    @Environment(AppModel.self) private var model
    let job: Components.Schemas.AceJob

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "waveform.path")
                .font(.title3)
                .foregroundStyle(Theme.accent)
                .frame(width: 44, height: 44)
                .background(Theme.accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 10) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(job.title).lineLimit(1)
                        Text(job.request.caption ?? "").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Text(AceSession.stageName(job))
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(.quaternary, in: Capsule())
                    Button(role: .destructive) {
                        Task { try? await model.deleteAceJob(job.id) }
                    } label: {
                        Image(systemName: "xmark")
                    }
                    .buttonStyle(.borderless)
                    .help("Abbrechen")
                }
                if let fraction = job.fraction, (job.stage ?? "queued") != "queued" {
                    ProgressView(value: min(max(fraction, 0), 1))
                }
            }
        }
        .card(padding: 12)
    }
}

/// A job waiting for its turn, with moving and removing.
private struct WaitingCard: View {
    @Environment(AppModel.self) private var model
    let job: Components.Schemas.QueuedJob
    let first: Bool
    let last: Bool

    var body: some View {
        let kind = Stages.kind(job.kind)
        HStack(spacing: 12) {
            Image(systemName: kind.symbol)
                .font(.title3)
                .foregroundStyle(Theme.accent)
                .frame(width: 44, height: 44)
                .background(Theme.accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            VStack(alignment: .leading, spacing: 3) {
                Text(job.title.isEmpty ? kind.name : job.title)
                    .font(.headline)
                    .lineLimit(1)
                Text(details(kind.name))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            Text(job.createdAt, style: .relative)
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
            HStack(spacing: 2) {
                Button {
                    Task { await model.move(job, by: -1) }
                } label: {
                    Image(systemName: "chevron.up")
                }
                .disabled(first)
                .help("Nach vorn")
                Button {
                    Task { await model.move(job, by: 1) }
                } label: {
                    Image(systemName: "chevron.down")
                }
                .disabled(last)
                .help("Nach hinten")
                Button(role: .destructive) {
                    Task { await model.remove(job) }
                } label: {
                    Image(systemName: "xmark")
                }
                .help("Aus der Warteschlange nehmen")
            }
            .buttonStyle(.borderless)
        }
        .card(padding: 12)
    }

    private func details(_ kind: String) -> String {
        var parts = [kind]
        if let batch = job.batch, batch > 1 {
            parts.append(String(localized: "\(Int(batch)) Songs"))
        }
        if let quality = job.quality {
            parts.append(quality == "full" ? String(localized: "Voll") : String(localized: "Entwurf"))
        }
        if let voice = job.voiceLabel {
            parts.append(String(localized: "Stimme \(voice)"))
        }
        return parts.joined(separator: " · ")
    }
}

extension AppModel {
    /// Stops a song the worker is on; its state turns to cancelled through the event stream.
    func cancel(_ state: Components.Schemas.SongState) async {
        guard let tonwerk, let slash = state.id.lastIndex(of: "/") else { return }
        _ = try? await tonwerk.api.cancelSong(
            path: .init(run: String(state.id[..<slash]), song: String(state.id[state.id.index(after: slash)...])))
    }

    func move(_ job: Components.Schemas.QueuedJob, by offset: Int) async {
        guard let tonwerk else { return }
        _ = try? await tonwerk.api.moveInQueue(path: .init(id: job.id), body: .json(.init(offset: Int32(offset))))
    }

    func remove(_ job: Components.Schemas.QueuedJob) async {
        guard let tonwerk else { return }
        _ = try? await tonwerk.api.removeFromQueue(path: .init(id: job.id))
    }
}
