import SwiftUI
import TonwerkKit

/// The models that take turns in the Mac's 24 GB, as the web page's `QueueOverview.vue` shows them over the queue:
/// which one holds the memory now and how much waits for each. The occasional ones (cover, video, speech, ACE-Step)
/// only get a tile while they have something in the works.
enum QueueModel: String, CaseIterable, Identifiable {
    case yue
    case lyrics
    case voice
    case image
    case speech
    case ace
    case video
    case transcription

    var id: String { rawValue }

    var name: String {
        switch self {
        case .yue: "YuE2"
        case .lyrics: String(localized: "Textmodell")
        case .voice: String(localized: "Stimme")
        case .image: String(localized: "Bild")
        case .speech: String(localized: "Sprache")
        case .ace: "ACE-Step"
        case .video: String(localized: "Video")
        case .transcription: String(localized: "Transkription")
        }
    }

    var symbol: String {
        switch self {
        case .yue: "sparkles"
        case .lyrics: "text.quote"
        case .voice: "person.wave.2"
        case .image: "paintbrush.pointed"
        case .speech: "bubble.left.and.text.bubble.right"
        case .ace: "waveform.path"
        case .video: "film"
        case .transcription: "music.quarternote.3"
        }
    }

    /// The model the server says holds the memory (`StatusSnapshot.Memory`), else YuE2 while its worker is busy.
    static func holder(memory: Components.Schemas.LargeModel?, workerBusy: Bool) -> QueueModel? {
        switch memory {
        case .lyrics: .lyrics
        case .voices: .voice
        case .images: .image
        case .speech: .speech
        case .ace: .ace
        case nil: workerBusy ? .yue : nil
        }
    }
}

extension AppModel {
    var memoryOwner: QueueModel? {
        QueueModel.holder(memory: memoryHolder, workerBusy: worker?.busy == true)
    }

    func running(_ model: QueueModel) -> Int {
        switch model {
        case .yue: inProgress.count
        case .lyrics: drafts.values.contains { $0.stage == "writing" } ? 1 : 0
        case .voice: voiceJobs.filter { $0.stage != nil && $0.stage != "queued" }.count
        case .image: images.values.filter { $0.stage == "loading" || $0.stage == "painting" }.count
        case .video: videoJobs.filter { $0.stage != nil && $0.stage != "queued" }.count
        case .transcription: runningTranscriptions.count
        // The app has no pages for these yet; only the memory says they run.
        case .speech, .ace: memoryOwner == model ? 1 : 0
        }
    }

    func waiting(_ model: QueueModel) -> Int {
        switch model {
        case .yue: queue.filter { $0.kind == .song || $0.kind == .render }.count
        case .lyrics: queue.filter { $0.kind == .lyrics }.count
        case .voice: voiceJobs.filter { ($0.stage ?? "queued") == "queued" }.count
        case .image: images.values.filter { $0.stage == "queued" }.count
        case .video: videoJobs.filter { ($0.stage ?? "queued") == "queued" }.count
        case .transcription: waitingTranscriptions.count
        case .speech, .ace: 0
        }
    }

    /// A video renders on the CPU beside the models, and SheetSage2 is small enough to run beside YuE2, so both work
    /// even while another one holds the memory.
    func isWorking(_ model: QueueModel) -> Bool {
        memoryOwner == model || ((model == .video || model == .transcription) && running(model) > 0)
    }

    /// The queue's badge: everything waiting or in the works.
    var queueCount: Int {
        queue.count + inProgress.count + voiceJobs.count + videoJobs.count + runningTranscriptions.count
    }

    /// The tiles to show: the three that always take turns, and the occasional ones while they have work.
    var shownModels: [QueueModel] {
        QueueModel.allCases.filter { model in
            switch model {
            case .yue, .lyrics, .voice: true
            default: isWorking(model) || running(model) + waiting(model) > 0
            }
        }
    }

    func state(of model: QueueModel) -> String {
        var parts: [String] = []
        let busy = running(model)
        if isWorking(model) {
            parts.append(busy > 1 ? String(localized: "rechnet \(busy)") : String(localized: "rechnet"))
        } else if model == .yue && worker?.status == .ready {
            // Loaded but idle: the next song starts without loading, a draft or version first shuts it down.
            parts.append(String(localized: "geladen"))
        }
        let queued = waiting(model)
        if queued > 0 {
            parts.append(String(localized: "\(queued) warten"))
        }
        return parts.isEmpty ? String(localized: "frei") : parts.joined(separator: " · ")
    }
}

struct ModelOverview: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: 8)], spacing: 8) {
            ForEach(model.shownModels) { tile in
                ModelTile(model: tile, working: model.isWorking(tile), waiting: model.waiting(tile) > 0,
                          state: model.state(of: tile))
            }
        }
        .help("Im Speicher hat immer nur ein großes Modell Platz: YuE2, das Textmodell oder Stimme und Stem-Trennung. Hervorgehoben ist, wer gerade rechnet.")
        .animation(.easeInOut(duration: 0.25), value: model.shownModels.map(\.id))
    }
}

private struct ModelTile: View {
    let model: QueueModel
    let working: Bool
    let waiting: Bool
    let state: String
    @State private var pulse = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: model.symbol)
                .font(.body.weight(.medium))
                .foregroundStyle(working ? Theme.accent : Color.secondary)
                .frame(width: 30, height: 30)
                .background((working ? Theme.accent : Color.secondary).opacity(0.12), in: Circle())
                .opacity(working && pulse ? 0.45 : 1)
            VStack(alignment: .leading, spacing: 1) {
                Text(model.name)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(working || waiting ? Color.primary : Color.secondary)
                Text(state)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .lineLimit(1)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .background(working ? Theme.accent.opacity(0.12) : Color.clear,
                    in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(working ? Theme.accent : Color.secondary.opacity(0.3),
                              style: StrokeStyle(lineWidth: 1, dash: waiting && !working ? [4, 3] : []))
        }
        .accessibilityElement(children: .combine)
        .onAppear { animate() }
        .onChange(of: working) { animate() }
    }

    private func animate() {
        guard working && !reduceMotion else {
            pulse = false
            return
        }
        withAnimation(.easeInOut(duration: 0.8).repeatForever(autoreverses: true)) { pulse = true }
    }
}
