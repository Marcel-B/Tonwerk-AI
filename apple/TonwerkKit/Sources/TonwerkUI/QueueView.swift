import SwiftUI
import TonwerkKit

/// What the server works on and what waits, from the event stream, like the web page's queue page: a card per song
/// with its timeline, then the waiting jobs, which can be moved and removed.
struct QueueView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if model.inProgress.isEmpty && model.queue.isEmpty && model.voiceJobs.isEmpty && model.videoJobs.isEmpty {
                ContentUnavailableView(
                    "Nichts in Arbeit", systemImage: "hourglass",
                    description: Text("Songs, Songtexte und Transkriptionen erscheinen hier, sobald sie bestellt sind."))
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 12) {
                        if !model.inProgress.isEmpty {
                            heading("In Arbeit")
                            ForEach(model.inProgress, id: \.id) { state in
                                WorkingCard(state: state)
                                    .transition(.opacity.combined(with: .move(edge: .top)))
                            }
                        }
                        if !model.voiceJobs.isEmpty {
                            heading("Stimmen und Stems")
                                .padding(.top, model.inProgress.isEmpty ? 0 : 8)
                            ForEach(model.voiceJobs) { job in
                                VoiceJobCard(job: job)
                                    .transition(.opacity)
                            }
                        }
                        if !model.videoJobs.isEmpty {
                            heading("Videos")
                                .padding(.top, model.inProgress.isEmpty && model.voiceJobs.isEmpty ? 0 : 8)
                            ForEach(model.videoJobs, id: \.id) { video in
                                VideoJobCard(video: video)
                                    .transition(.opacity)
                            }
                        }
                        if !model.queue.isEmpty {
                            heading("Wartet")
                                .padding(.top, model.inProgress.isEmpty && model.voiceJobs.isEmpty && model.videoJobs.isEmpty ? 0 : 8)
                            ForEach(Array(model.queue.enumerated()), id: \.element.id) { index, job in
                                WaitingCard(job: job, first: index == 0, last: index == model.queue.count - 1)
                                    .transition(.opacity)
                            }
                        }
                    }
                    .padding()
                    .frame(maxWidth: 760)
                    .frame(maxWidth: .infinity)
                    .animation(.easeInOut(duration: 0.25), value: model.inProgress.map(\.id))
                    .animation(.easeInOut(duration: 0.25), value: model.queue.map(\.id))
                    .animation(.easeInOut(duration: 0.25), value: model.voiceJobs.map(\.id))
                    .animation(.easeInOut(duration: 0.25), value: model.videoJobs.map(\.id))
                }
            }
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
