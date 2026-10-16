import SwiftUI
import TonwerkKit
import UniformTypeIdentifiers

/// The voices page (`VoicesPanel.vue`, `StemsPanel.vue`, `VoiceSwapPanel.vue`): the reference voices a song can be
/// sung with, songs and files split into stems, and a recording sung with another voice.
struct VoicesView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let session = model.voicesPage {
                VoicesSessionView(session: session)
            } else {
                ProgressView().onAppear { model.openVoices() }
            }
        }
        .navigationTitle("Stimmen")
    }
}

/// A question before something goes for good.
private struct Pending {
    let title: String
    let button: String
    let run: () async -> Void
}

private struct VoicesSessionView: View {
    @Environment(AppModel.self) private var model
    @Bindable var session: VoicesSession
    @State private var importing: Importing?
    @State private var pending: Pending?
    @State private var saving: URL?

    enum Importing {
        case voice
        case stems
        case swap
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Picker("Bereich", selection: $session.tab) {
                    ForEach(VoicesSession.Tab.allCases) { tab in
                        Text(tab.name).tag(tab)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                if let problem = session.problem {
                    Label(problem, systemImage: "exclamationmark.triangle")
                        .foregroundStyle(.red)
                }
                switch session.tab {
                case .voices: voicesTab
                case .stems: stemsTab
                case .swap: swapTab
                }
            }
            .padding()
            .frame(maxWidth: 900, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .task { await session.load() }
        .refreshable { await session.load() }
        .fileImporter(
            isPresented: Binding(get: { importing != nil }, set: { if !$0 { importing = nil } }),
            allowedContentTypes: [.audio, .movie]
        ) { result in
            let target = importing
            guard case .success(let url) = result else { return }
            Task { await take(url, for: target) }
        }
        .fileMover(
            isPresented: Binding(get: { saving != nil }, set: { if !$0 { saving = nil } }), file: saving
        ) { _ in }
        .confirmationDialog(
            pending?.title ?? "",
            isPresented: Binding(get: { pending != nil }, set: { if !$0 { pending = nil } }),
            titleVisibility: .visible
        ) {
            if let pending {
                Button(pending.button, role: .destructive) { Task { await pending.run() } }
            }
            Button("Abbrechen", role: .cancel) {}
        }
    }

    private func take(_ url: URL, for target: Importing?) async {
        do {
            let file = try await PickedFile.copy(from: url)
            switch target {
            case .voice: session.newFile = file
            case .stems:
                session.stemFile = file
                session.stemSource = .file
            case .swap: session.swapFile = file
            case nil: break
            }
            session.problem = nil
        } catch {
            session.problem = error.localizedDescription
        }
    }

    private func unavailable(_ text: LocalizedStringKey) -> some View {
        Label(text, systemImage: "info.circle")
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading)
            .card()
    }

    // MARK: Reference voices

    @ViewBuilder
    private var voicesTab: some View {
        if session.info?.voicesConfigured == false {
            unavailable("Auf dem Server ist kein Stimmendienst eingerichtet.")
        } else {
            VStack(alignment: .leading, spacing: 10) {
                CardHeading("Referenzstimmen", systemImage: "person.wave.2")
                Text("Mit diesen Stimmen lassen sich Songs neu singen: im Menü eines Songs „Mit Stimme singen …“.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let problem = session.voicesProblem {
                    Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                } else if session.voices.isEmpty {
                    Text("Noch keine Stimme.").foregroundStyle(.secondary)
                }
                ForEach(session.voices, id: \.id) { voice in
                    voiceRow(voice)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .card()
            newVoice
            if !session.versions.isEmpty {
                versionsCard
            }
        }
    }

    private func voiceRow(_ voice: Components.Schemas.ReferenceVoice) -> some View {
        let key = "voice:\(voice.id)"
        return HStack(spacing: 10) {
            PlayButton(playing: session.preview.plays(key)) {
                if let url = model.referenceVoiceURL(voice.id) {
                    session.preview.toggle(key, url: url)
                }
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(voice.label)
                Text(details(of: voice))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button(role: .destructive) {
                pending = Pending(title: String(localized: "Die Stimme „\(voice.label)“ löschen?"), button: String(localized: "Löschen"), run: {
                    await session.delete(voice)
                })
            } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(.borderless)
            .help("Löschen")
        }
    }

    private func details(of voice: Components.Schemas.ReferenceVoice) -> String {
        var parts = [String(localized: "\(Int(voice.seconds.rounded())) s")]
        if let created = voice.createdAt {
            parts.append(created.formatted(date: .abbreviated, time: .omitted))
        }
        return parts.joined(separator: " · ")
    }

    private var newVoice: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Neue Stimme", systemImage: "plus.circle")
            Text("Eine Aufnahme von 3 bis 25 Sekunden, nur Gesang, ohne Musik und Hall.")
                .font(.caption)
                .foregroundStyle(.secondary)
            HStack {
                Button {
                    importing = .voice
                } label: {
                    Label(session.newFile == nil ? "Aufnahme wählen …" : "Andere Aufnahme …", systemImage: "folder")
                }
                if let file = session.newFile {
                    Text(file.name).lineLimit(1).foregroundStyle(.secondary)
                }
            }
            if let file = session.newFile {
                TextField("Name", text: $session.newLabel)
                    .textFieldStyle(.roundedBorder)
                if let seconds = file.seconds {
                    trimming(file, seconds: seconds)
                }
                Button {
                    Task { await session.addVoice() }
                } label: {
                    HStack(spacing: 8) {
                        if session.adding {
                            ProgressView().controlSize(.small)
                        }
                        Text("Stimme hinzufügen")
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(!session.canAdd)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    /// A recording often starts with talking or silence: the part to keep is found by ear, the server cuts it.
    @ViewBuilder
    private func trimming(_ file: PickedFile, seconds: Double) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            LabeledContent("Anfang") {
                Slider(value: $session.clipStart, in: 0...seconds, onEditingChanged: { editing in
                    if !editing, session.clipStart > session.clipEnd { session.clipEnd = session.clipStart }
                })
            }
            LabeledContent("Ende") {
                Slider(value: $session.clipEnd, in: 0...seconds, onEditingChanged: { editing in
                    if !editing, session.clipEnd < session.clipStart { session.clipStart = session.clipEnd }
                })
            }
            HStack {
                PlayButton(playing: session.preview.plays("new")) {
                    session.preview.toggle("new", url: file.url, at: session.clipStart, until: session.clipEnd)
                }
                Text("\(Song.clock(session.clipStart)) – \(Song.clock(session.clipEnd)) · \(Int(session.clipSeconds.rounded())) s")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            if session.clipSeconds < VoicesSession.minSeconds {
                Text("Der Teil ist kürzer als 3 Sekunden.").font(.caption).foregroundStyle(.red)
            } else if session.clipSeconds > VoicesSession.keptSeconds {
                Text("Behalten werden die ersten 25 Sekunden des Teils.").font(.caption).foregroundStyle(.orange)
            }
        }
    }

    private var versionsCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Versionen", systemImage: "music.mic")
            ForEach(session.versions, id: \.id) { version in
                JobRow(
                    title: model.song(version.songId)?.displayTitle ?? version.title ?? version.songId,
                    detail: version.voiceLabel, stage: version.stage, fraction: version.fraction,
                    message: version.message
                ) {
                    if version.finished != true, let song = model.song(version.songId) {
                        Button("Abbrechen", role: .destructive) {
                            Task {
                                try? await model.deleteVersion(
                                    song, Song.Version(id: version.id, voice: version.voiceLabel))
                            }
                        }
                        .buttonStyle(.borderless)
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    // MARK: Stems

    @ViewBuilder
    private var stemsTab: some View {
        if session.info?.stemsConfigured == false {
            unavailable("Auf dem Server ist keine Stem-Trennung eingerichtet.")
        } else {
            separateCard
            if session.stemSets.contains(where: { $0.stage == "done" }) {
                midiCard
            }
            ForEach(session.stemSets, id: \.id) { set in
                stemSetCard(set)
            }
        }
    }

    private var separateCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Spuren trennen", systemImage: "square.3.layers.3d")
            Picker("Quelle", selection: $session.stemSource) {
                Text("Song").tag(VoicesSession.StemSource.song)
                Text("Datei").tag(VoicesSession.StemSource.file)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            switch session.stemSource {
            case .song:
                Picker("Song", selection: $session.stemSong) {
                    Text("Song wählen").tag(String?.none)
                    ForEach(model.songs.filter(\.hasAudio)) { song in
                        Text(song.displayTitle).tag(String?.some(song.id))
                    }
                }
            case .file:
                HStack {
                    Button {
                        importing = .stems
                    } label: {
                        Label(session.stemFile == nil ? "Datei wählen …" : "Andere Datei …", systemImage: "folder")
                    }
                    if let file = session.stemFile {
                        Text(file.name).lineLimit(1).foregroundStyle(.secondary)
                    }
                }
            }
            if !session.models.isEmpty {
                Picker("Modell", selection: $session.stemModel) {
                    ForEach(session.models, id: \.id) { stemModel in
                        Text(stemModel.name).tag(String?.some(stemModel.id))
                    }
                }
            }
            if !session.modelHint.isEmpty {
                Text(session.modelHint).font(.caption).foregroundStyle(.secondary)
            }
            Toggle("Hall vom Gesang trennen", isOn: $session.dereverb)
            Button {
                Task { await session.separate() }
            } label: {
                HStack(spacing: 8) {
                    if session.separating {
                        ProgressView().controlSize(.small)
                    }
                    Text("Trennen")
                }
            }
            .buttonStyle(.borderedProminent)
            .disabled(!session.canSeparate)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    private var midiCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            CardHeading("MIDI aus Stems", systemImage: "pianokeys")
            Toggle("Einstimmig", isOn: $session.midiMono)
            Toggle("Pitch Bends", isOn: $session.midiBends)
                .disabled(!session.midiMono)
            Toggle("Auf Sechzehntel quantisieren", isOn: $session.midiQuantize)
            if session.stemSets.contains(where: { $0.upload == true && $0.stage == "done" }) {
                Stepper(value: $session.midiTempo, in: 40...240, step: 1) {
                    Text("Tempo eigener Dateien: \(Int(session.midiTempo)) BPM")
                }
            }
            Text("Gesang wird zur Melodie, Bass einstimmig, Schlagzeug zu Kick, Snare und Hi-Hat.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    private func stemSetCard(_ set: Components.Schemas.StemSetState) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(session.name(of: set)).font(.headline).lineLimit(2)
                    Text(setDetails(set)).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                if set.stage != "done" {
                    StageBadge(stage: set.stage)
                }
                Button(role: .destructive) {
                    let name = session.name(of: set)
                    pending = set.finished == true
                        ? Pending(
                            title: String(localized: "Die Stems von „\(name)“ löschen?"),
                            button: String(localized: "Löschen"), run: { await session.delete(set) })
                        : Pending(
                            title: String(localized: "Das Trennen von „\(name)“ abbrechen?"),
                            button: String(localized: "Abbrechen"), run: { await session.delete(set) })
                } label: {
                    Image(systemName: set.finished == true ? "trash" : "xmark")
                }
                .buttonStyle(.borderless)
            }
            if set.stage == "failed", let message = set.message {
                Text(message).font(.caption).foregroundStyle(.red)
            }
            if set.stage == "separating" {
                ProgressView().controlSize(.small)
            }
            ForEach(set.stems ?? [], id: \.name) { stem in
                stemRow(set, stem)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    private func setDetails(_ set: Components.Schemas.StemSetState) -> String {
        var parts = [session.modelName(set.model)]
        if set.dereverb == true {
            parts.append(String(localized: "Hall getrennt"))
        }
        if let created = set.createdAt {
            parts.append(created.formatted(date: .abbreviated, time: .shortened))
        }
        return parts.joined(separator: " · ")
    }

    private func stemRow(_ set: Components.Schemas.StemSetState, _ stem: Components.Schemas.StemFile) -> some View {
        let key = session.midiKey(set, stem)
        let url = model.stemURL(set.id, stem.name)
        let current = session.preview.current == key
        return VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 10) {
                PlayButton(playing: session.preview.plays(key)) {
                    if let url { session.preview.toggle(key, url: url) }
                }
                Text(VoicesSession.stemName(stem.name))
                    .frame(minWidth: 90, alignment: .leading)
                PeaksView(
                    peaks: stem.peaks ?? [], progress: current && stem.seconds > 0 ? session.preview.position / stem.seconds : 0
                ) { fraction in
                    if let url { session.preview.seek(key, url: url, to: fraction * stem.seconds) }
                }
                .frame(height: 28)
                Text(Song.clock(stem.seconds))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                Menu {
                    Button {
                        Task { await session.toMidi(set, stem) }
                    } label: {
                        Label("Als MIDI", systemImage: "pianokeys")
                    }
                    .disabled(session.converting != nil)
                    Button {
                        Task {
                            do {
                                saving = try await model.downloadStem(set, stem, title: session.name(of: set))
                            } catch {
                                session.problem = error.localizedDescription
                            }
                        }
                    } label: {
                        Label("Sichern …", systemImage: "square.and.arrow.down")
                    }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
                .menuStyle(.button)
                .buttonStyle(.borderless)
                .menuIndicator(.hidden)
                .fixedSize()
            }
            if session.converting == key {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small)
                    Text("MIDI wird erstellt …").font(.caption).foregroundStyle(.secondary)
                }
            } else if let error = session.midiErrors[key] {
                Text(error).font(.caption).foregroundStyle(.red)
            } else if let midi = session.midi[key] {
                HStack(spacing: 12) {
                    Text("\(midi.notes) Noten").font(.caption).foregroundStyle(.secondary)
                    ShareLink(item: midi.url) {
                        Label("Teilen", systemImage: "square.and.arrow.up")
                    }
                    Button {
                        saving = midi.url
                    } label: {
                        Label("Sichern …", systemImage: "square.and.arrow.down")
                    }
                }
                .font(.caption)
                .buttonStyle(.borderless)
            }
        }
    }

    // MARK: Swap

    @ViewBuilder
    private var swapTab: some View {
        if session.info?.voicesConfigured == false || session.info?.conversionConfigured == false {
            unavailable("Auf dem Server ist keine Stimmenumwandlung eingerichtet.")
        } else {
            swapCard
            ForEach(session.swaps, id: \.id) { swap in
                swapRow(swap)
            }
        }
    }

    private var swapCard: some View {
        @Bindable var model = model
        return VStack(alignment: .leading, spacing: 10) {
            CardHeading("Stimme tauschen", systemImage: "arrow.triangle.2.circlepath")
            Text("Eine eigene Aufnahme wird mit einer Referenzstimme neu gesungen.")
                .font(.caption)
                .foregroundStyle(.secondary)
            HStack {
                Button {
                    importing = .swap
                } label: {
                    Label(session.swapFile == nil ? "Aufnahme wählen …" : "Andere Aufnahme …", systemImage: "folder")
                }
                if let file = session.swapFile {
                    Text(file.name).lineLimit(1).foregroundStyle(.secondary)
                }
            }
            Picker("Die Datei ist", selection: $session.swapSeparate) {
                Text("Nur Gesang").tag(false)
                Text("Ganzer Song").tag(true)
            }
            .pickerStyle(.segmented)
            if session.info?.stemsConfigured != true {
                Text("Für ganze Songs fehlt die Stem-Trennung auf dem Server.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            VoiceSettingsForm(
                settings: $model.voiceSettings, voices: session.voices, reverb: session.swapSeparate)
            Button {
                Task { await session.swap() }
            } label: {
                HStack(spacing: 8) {
                    if session.swapping {
                        ProgressView().controlSize(.small)
                    }
                    Text("Stimme tauschen")
                }
            }
            .buttonStyle(.borderedProminent)
            .disabled(!session.canSwap)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
        .onChange(of: session.voices.map(\.id)) { session.chooseVoice() }
        .onAppear { session.chooseVoice() }
    }

    private func swapRow(_ swap: Components.Schemas.SwapState) -> some View {
        JobRow(
            title: swap.fileName, detail: swap.voiceLabel, stage: swap.stage, fraction: swap.fraction,
            message: swap.message
        ) {
            if swap.stage == "done" {
                ForEach([false, true], id: \.self) { source in
                    let key = "swap:\(swap.id)/\(source ? "source" : "result")"
                    Button {
                        if let url = model.swapURL(swap.id, source: source) {
                            session.preview.toggle(key, url: url)
                        }
                    } label: {
                        Label(
                            source ? "Original" : "Ergebnis",
                            systemImage: session.preview.plays(key) ? "pause.fill" : "play.fill")
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.small)
                }
            }
            Button(role: .destructive) {
                pending = swap.finished == true
                    ? Pending(
                        title: String(localized: "„\(swap.fileName)“ mit \(swap.voiceLabel) löschen?"),
                        button: String(localized: "Löschen"), run: { await session.delete(swap) })
                    : Pending(
                        title: String(localized: "Den Tausch von „\(swap.fileName)“ abbrechen?"),
                        button: String(localized: "Abbrechen"), run: { await session.delete(swap) })
            } label: {
                Image(systemName: swap.finished == true ? "trash" : "xmark")
            }
            .buttonStyle(.borderless)
        }
        .card()
    }
}

/// A job of the voices' queue: what, with which voice, its stage and how far it is.
struct JobRow<Actions: View>: View {
    let title: String
    let detail: String
    let stage: String?
    let fraction: Double?
    let message: String?
    @ViewBuilder let actions: () -> Actions

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 10) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).lineLimit(1)
                    Text(detail).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer()
                if stage != "done" {
                    StageBadge(stage: stage)
                }
                actions()
            }
            if stage == "converting" || stage == "mixing" {
                ProgressView(value: min(max(fraction ?? 0, 0), 1))
            } else if stage == "separating" {
                ProgressView().controlSize(.small)
            }
            if stage == "failed", let message {
                Text(message).font(.caption).foregroundStyle(.red)
            }
        }
    }
}

struct StageBadge: View {
    let stage: String?

    var body: some View {
        Text(VoiceStages.name(stage))
            .font(.caption.weight(.semibold))
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(tint.opacity(0.15), in: Capsule())
            .foregroundStyle(tint)
    }

    private var tint: Color {
        switch stage ?? "" {
        case "failed": .red
        case "done": .green
        default: Theme.accent
        }
    }
}

private struct PlayButton: View {
    let playing: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: playing ? "pause.circle.fill" : "play.circle.fill")
                .font(.title2)
                .foregroundStyle(Theme.accent)
        }
        .buttonStyle(.borderless)
        .accessibilityLabel(playing ? Text("Pause") : Text("Anhören"))
    }
}

/// A stem's loudness outline (0–1, one scale per set, read on the server), the part played in the accent; a tap or
/// drag seeks.
struct PeaksView: View {
    let peaks: [Double]
    let progress: Double
    let seek: (Double) -> Void

    var body: some View {
        GeometryReader { geometry in
            Canvas { context, size in
                guard !peaks.isEmpty else { return }
                let count = min(peaks.count, max(1, Int(size.width / 3)))
                let width = size.width / CGFloat(count)
                for index in 0..<count {
                    // Several peaks per bar where there are more than fit: the loudest of them.
                    let from = index * peaks.count / count
                    let to = max(from + 1, (index + 1) * peaks.count / count)
                    let peak = peaks[from..<min(to, peaks.count)].max() ?? 0
                    let height = max(1.5, CGFloat(min(max(peak, 0), 1)) * size.height)
                    let rect = CGRect(
                        x: CGFloat(index) * width, y: (size.height - height) / 2, width: max(1, width - 1),
                        height: height)
                    let played = (CGFloat(index) + 0.5) / CGFloat(count) <= progress
                    context.fill(
                        Path(roundedRect: rect, cornerRadius: 1),
                        with: .color(played ? Theme.accent : Color.secondary.opacity(0.35)))
                }
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0).onEnded { value in
                    guard geometry.size.width > 0 else { return }
                    seek(min(max(value.location.x / geometry.size.width, 0), 1))
                })
        }
    }
}

/// Voice, octave, strength and steps, shared by the sing dialog and the swap.
struct VoiceSettingsForm: View {
    @Binding var settings: VoiceSettings
    let voices: [Components.Schemas.ReferenceVoice]
    /// Only a separated song has reverb of its own to keep.
    var reverb = true

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Picker("Stimme", selection: $settings.voice) {
                if voices.isEmpty {
                    Text("Noch keine Stimme").tag(String?.none)
                }
                ForEach(voices, id: \.id) { voice in
                    Text(voice.label).tag(String?.some(voice.id))
                }
            }
            row("Oktave") {
                Picker("Oktave", selection: $settings.semitones) {
                    ForEach(VoiceSettings.octaves, id: \.value) { Text($0.name).tag($0.value) }
                }
            }
            row("Stärke") {
                Picker("Stärke", selection: $settings.strength) {
                    ForEach(VoiceSettings.strengths, id: \.value) { Text($0.name).tag($0.value) }
                }
            }
            row("Schritte") {
                Picker("Schritte", selection: $settings.steps) {
                    ForEach(VoiceSettings.stepChoices, id: \.value) { Text($0.name).tag($0.value) }
                }
            }
            if reverb {
                Toggle("Hall des Originals behalten", isOn: $settings.keepReverb)
            }
        }
    }

    private func row<Content: View>(_ label: LocalizedStringKey, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            content()
                .pickerStyle(.segmented)
                .labelsHidden()
        }
    }
}

/// "Mit Stimme singen" from a song's menu (`SingDialog.vue`): the song becomes a version with a reference voice.
struct SingSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let song: Song
    @State private var voices: [Components.Schemas.ReferenceVoice] = []
    @State private var loading = true
    @State private var working = false
    @State private var problem: String?

    var body: some View {
        @Bindable var model = model
        NavigationStack {
            Form {
                Section {
                    Text(song.displayTitle).font(.headline)
                    Text("Der Gesang wird abgetrennt, mit der Stimme neu gesungen und wieder unter die Musik gemischt. Das Original bleibt.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Section {
                    if loading {
                        ProgressView()
                    } else if voices.isEmpty {
                        Text("Noch keine Stimme. Lege eine auf der Seite Stimmen an.")
                            .foregroundStyle(.secondary)
                    } else {
                        VoiceSettingsForm(settings: $model.voiceSettings, voices: voices)
                    }
                }
                if let problem {
                    Section {
                        Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                    }
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Mit Stimme singen")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Abbrechen") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Singen lassen") { Task { await sing() } }
                        .disabled(working || model.voiceSettings.voice == nil || voices.isEmpty)
                }
            }
        }
        #if os(macOS)
        .frame(minWidth: 460, minHeight: 420)
        #endif
        .task { await load() }
    }

    // Read whenever the dialog opens, since a voice may have been added meanwhile.
    private func load() async {
        do {
            voices = try await model.referenceVoices()
            if !voices.contains(where: { $0.id == model.voiceSettings.voice }) {
                model.voiceSettings.voice = voices.first?.id
            }
        } catch {
            problem = error.localizedDescription
        }
        loading = false
    }

    private func sing() async {
        working = true
        defer { working = false }
        do {
            _ = try await model.sing(song, settings: model.voiceSettings)
            dismiss()
        } catch {
            problem = error.localizedDescription
        }
    }
}
