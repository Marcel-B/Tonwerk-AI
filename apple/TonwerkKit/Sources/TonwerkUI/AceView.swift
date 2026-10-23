import SwiftUI
import TonwerkKit

/// ACE-Step, the second song model (`AcePage.vue`): a style description instead of YuE2's tags, lyrics the LM may
/// write itself, tempo, key, metre and length to fix or leave to it, and covers or repaints of a library song. Its
/// parameters differ from YuE2's too much to share the create form; the songs go into the same library.
struct AceView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let session = model.acePage {
                AceSessionView(session: session)
            } else {
                ProgressView().onAppear { model.openAce() }
            }
        }
        .navigationTitle("ACE-Step")
    }
}

private struct AceSessionView: View {
    @Environment(AppModel.self) private var model
    @Bindable var session: AceSession
    @AppStorage("aceAdvancedOpen") private var advancedOpen = false
    @State private var lyricsCaret = LyricsCaret()

    private var auto: Text { Text("automatisch") }

    var body: some View {
        @Bindable var model = model
        Form {
            Section {
                Text("Das zweite Songmodell neben YuE2: ACE-Step 1.5 über acestep.cpp. Statt Tags eine Beschreibung des Stils; Text, Tempo, Tonart, Takt und Länge schreibt und wählt das Sprachmodell selbst, wo du nichts vorgibst. Die Songs landen in derselben Bibliothek.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let info = session.info, !info.ready {
                    notReady(info)
                }
                if let problem = session.problem {
                    Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                }
            }

            task
            song
            lyrics
            music
            advanced
            jobs
        }
        .formStyle(.grouped)
        .task { await session.load() }
        .refreshable { await session.load() }
        .onChange(of: model.connection) {
            // A job may have finished while the stream was away.
            if model.connection == .live { Task { await session.load() } }
        }
        .onChange(of: model.aceForm) { model.saveAceForm() }
    }

    private func notReady(_ info: Components.Schemas.AceInfo) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(
                info.installed
                    ? "Es fehlen Modelle (Sprachmodell, DiT, Text-Encoder und VAE). Einmal auf dem Mac ausführen:"
                    : "acestep.cpp ist auf dem Server noch nicht gebaut. Einmal auf dem Mac ausführen:",
                systemImage: "exclamationmark.triangle")
            Text("deploy/install-ace.sh")
                .font(.system(.body, design: .monospaced))
                .textSelection(.enabled)
            Text("Programme in \(info.binaries), Modelle in \(info.models).")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .foregroundStyle(.orange)
    }

    // MARK: Task

    private var task: some View {
        @Bindable var model = model
        return Section {
            Picker("Aufgabe", selection: $model.aceForm.task) {
                ForEach(AceForm.Task.allCases) { task in
                    Text(task.name).tag(task)
                }
            }
            .pickerStyle(.segmented)
            Text(model.aceForm.task.hint)
                .font(.caption)
                .foregroundStyle(.secondary)
            if !model.aceForm.usesLm {
                Picker("Ausgangssong", selection: $model.aceForm.source) {
                    Text("Song aus der Bibliothek wählen").tag("")
                    ForEach(sources) { song in
                        Text(song.number.map { "\(song.title) · Song \($0)" } ?? song.title).tag(song.id)
                    }
                }
                if model.aceForm.task == .cover {
                    TextField("Nähe zum Original", value: $model.aceForm.coverStrength, format: .number, prompt: auto)
                    Text("0 bis 1: höher bleibt näher am Ausgangssong.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } else {
                    TextField("Von (Sekunden)", value: $model.aceForm.repaintStart, format: .number, prompt: Text("0"))
                    TextField("Bis (Sekunden)", value: $model.aceForm.repaintEnd, format: .number, prompt: Text("Ende"))
                }
            }
            MachinePicker(selection: $model.aceForm.machine, engine: "ace")
        } header: {
            HStack {
                CardHeading("Aufgabe", systemImage: "waveform.path")
                Spacer()
                Button {
                    model.aceForm = AceForm()
                } label: {
                    Image(systemName: "trash")
                }
                .buttonStyle(.borderless)
                .help("Formular leeren")
            }
        }
    }

    /// Every library song with audio, newest first, as the source of a cover or repaint.
    private var sources: [Song] {
        model.songs.filter { $0.hasAudio && $0.version == nil }
    }

    // MARK: Song

    private var song: some View {
        @Bindable var model = model
        return Section {
            TextField("Titel (optional)", text: $model.aceForm.title, prompt: Text("Leer: die ersten Wörter der Beschreibung"))
            VStack(alignment: .leading, spacing: 4) {
                Text("Beschreibung")
                TextField(
                    "Beschreibung", text: $model.aceForm.caption,
                    prompt: Text("z. B. dark synthwave, female vocals, driving bass, 80s drums"), axis: .vertical
                )
                .labelsHidden()
                .lineLimit(3...8)
                Text("Genre, Instrumente, Stimme und Stimmung in freien Worten, am besten auf Englisch. Die Bausteine passen auch hier.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            StyleBlocksView(style: $model.aceForm.caption, instrumental: model.aceForm.instrumental)
        } header: {
            CardHeading("Song", systemImage: "music.note")
        }
    }

    private var lyrics: some View {
        @Bindable var model = model
        return Section {
            Toggle("Instrumental", isOn: $model.aceForm.instrumental)
            if !model.aceForm.instrumental {
                ZStack(alignment: .topLeading) {
                    LyricsTextEditor(text: $model.aceForm.lyrics, caret: lyricsCaret)
                        .font(.body.monospaced())
                        .frame(minHeight: 200)
                        .scrollContentBackground(.hidden)
                    if model.aceForm.lyrics.isEmpty && model.aceForm.usesLm {
                        Text("Leer: das Sprachmodell schreibt den Text selbst")
                            .foregroundStyle(.tertiary)
                            .padding(.top, 8)
                            .padding(.leading, 5)
                            .allowsHitTesting(false)
                    }
                }
                SectionTagsView(lyrics: $model.aceForm.lyrics, caret: lyricsCaret)
                Picker("Sprache des Gesangs", selection: $model.aceForm.language) {
                    Text("automatisch").tag("")
                    ForEach(AceForm.languages, id: \.self) { code in
                        Text(Locale.current.localizedString(forLanguageCode: code) ?? code).tag(code)
                    }
                }
            }
        } header: {
            if model.aceForm.usesLm {
                CardHeading("Text (optional)", systemImage: "text.quote")
            } else {
                CardHeading("Text", systemImage: "text.quote")
            }
        }
    }

    // MARK: Music

    private var music: some View {
        @Bindable var model = model
        return Section {
            Text(model.aceForm.usesLm
                 ? "Leer gelassen entscheidet das Sprachmodell."
                 : "Länge, Tempo und Tonart kommen vom Ausgangssong.")
                .font(.caption)
                .foregroundStyle(.secondary)
            if model.aceForm.usesLm {
                TextField("Länge (Sekunden)", value: $model.aceForm.duration, format: .number, prompt: auto)
                TextField("Tempo (BPM)", value: $model.aceForm.bpm, format: .number, prompt: auto)
                Picker("Tonart", selection: $model.aceForm.key) {
                    Text("automatisch").tag("")
                    ForEach(AceForm.keys, id: \.self) { key in
                        Text(key).tag(key)
                    }
                }
                Picker("Takt", selection: $model.aceForm.timeSignature) {
                    Text("automatisch").tag(Int?.none)
                    ForEach([2, 3, 4, 6], id: \.self) { beats in
                        Text("\(beats)/4").tag(Int?.some(beats))
                    }
                }
            }
            TextField("Seed", text: $model.aceForm.seed, prompt: Text("zufällig"))
            #if os(iOS)
                .keyboardType(.numberPad)
            #endif
            Picker("Varianten", selection: $model.aceForm.batch) {
                ForEach(1...4, id: \.self) { count in
                    Text("\(count)").tag(count)
                }
            }
            .pickerStyle(.segmented)
            HStack {
                Button {
                    Task { await session.send() }
                } label: {
                    Label("Komponieren", systemImage: "sparkles")
                }
                .buttonStyle(.borderedProminent)
                .disabled(!session.canSend)
                if session.sending {
                    ProgressView().controlSize(.small)
                }
                if !model.aceForm.usesLm && model.aceForm.source.isEmpty {
                    Text("Song aus der Bibliothek wählen")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            if let notice = session.notice {
                Text(notice)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        } header: {
            CardHeading("Musik", systemImage: "metronome")
        }
    }

    // MARK: Advanced

    private var advanced: some View {
        @Bindable var model = model
        let info = session.info
        return Section {
            DisclosureGroup(isExpanded: $advancedOpen) {
                VStack(alignment: .leading, spacing: 12) {
                    Text("Leer gelassen gilt, was das Modell vorsieht.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    if model.aceForm.usesLm {
                        Picker("Sprachmodell", selection: $model.aceForm.lmModel) {
                            Text("Standard").tag("")
                            ForEach(info?.lmModels ?? [], id: \.self) { file in
                                Text(Self.modelName(file)).tag(file)
                            }
                        }
                    }
                    Picker("DiT (Klang)", selection: $model.aceForm.ditModel) {
                        Text("Standard").tag("")
                        ForEach(info?.ditModels ?? [], id: \.self) { file in
                            Text(Self.modelName(file)).tag(file)
                        }
                    }
                    Text("Standard: das größte Sprachmodell und ein Turbo-DiT (8 Schritte). Ein SFT- oder Base-DiT braucht mehr Schritte und klingt oft feiner.")
                        .font(.caption)
                        .foregroundStyle(.secondary)

                    Text("Diffusion").font(.subheadline.weight(.semibold))
                    TextField("Schritte", value: $model.aceForm.steps, format: .number, prompt: auto)
                    TextField("Guidance", value: $model.aceForm.guidance, format: .number, prompt: auto)
                    TextField("Shift", value: $model.aceForm.shift, format: .number, prompt: auto)
                    Picker("Solver", selection: $model.aceForm.solver) {
                        Text("automatisch").tag("")
                        ForEach(AceForm.solvers, id: \.self) { solver in
                            Text(solver).tag(solver)
                        }
                    }
                    Text("Turbo: 8 Schritte, Shift 3. SFT und Base: um 50 Schritte, Guidance um 7, Shift 1.")
                        .font(.caption)
                        .foregroundStyle(.secondary)

                    if model.aceForm.usesLm {
                        Text("Sprachmodell").font(.subheadline.weight(.semibold))
                        TextField("Temperatur", value: $model.aceForm.lmTemperature, format: .number, prompt: auto)
                        TextField("CFG", value: $model.aceForm.lmCfgScale, format: .number, prompt: auto)
                        TextField("Top p", value: $model.aceForm.lmTopP, format: .number, prompt: auto)
                        TextField("Top k", value: $model.aceForm.lmTopK, format: .number, prompt: auto)
                        TextField("Negativer Prompt", text: $model.aceForm.negativePrompt)
                        Text("Was nicht vorkommen soll, für das Sprachmodell.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Toggle(isOn: $model.aceForm.enrichCaption) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text("Beschreibung ausbauen")
                                Text("Das Sprachmodell ergänzt die Beschreibung, bevor es komponiert.")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }
                    }

                    Text("Adapter (LoRA)").font(.subheadline.weight(.semibold))
                    Picker("Adapter", selection: $model.aceForm.adapter) {
                        Text("Keiner").tag("")
                        ForEach(info?.adapters ?? [], id: \.self) { adapter in
                            Text(adapter).tag(adapter)
                        }
                    }
                    if !model.aceForm.adapter.isEmpty {
                        TextField("Stärke des Adapters", value: $model.aceForm.adapterScale, format: .number, prompt: Text("1"))
                    }
                    Text("Adapter liegen als .safetensors im Ordner adapters neben den Modellen.")
                        .font(.caption)
                        .foregroundStyle(.secondary)

                    Button(model.aceForm.advancedChanged ? "Auf Standardwerte zurücksetzen" : "Alles auf Standardwerten") {
                        model.aceForm = model.aceForm.resettingAdvanced()
                    }
                    .disabled(!model.aceForm.advancedChanged)
                }
                .padding(.top, 4)
            } label: {
                HStack {
                    Label {
                        Text("Erweitert")
                    } icon: {
                        Image(systemName: "slider.horizontal.3").foregroundStyle(Theme.accent)
                    }
                    if model.aceForm.advancedChanged {
                        Text("angepasst").foregroundStyle(.orange)
                    }
                }
            }
        }
    }

    static func modelName(_ file: String) -> String {
        file.hasSuffix(".gguf") ? String(file.dropLast(5)) : file
    }

    // MARK: Jobs

    @ViewBuilder
    private var jobs: some View {
        if session.info != nil {
            Section {
                let jobs = session.jobs
                if jobs.isEmpty {
                    Text("Noch keine.").foregroundStyle(.secondary)
                }
                ForEach(jobs, id: \.id) { job in
                    jobRow(job)
                }
            } header: {
                CardHeading("Aufträge", systemImage: "list.bullet")
            }
        }
    }

    private func jobRow(_ job: Components.Schemas.AceJob) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .top, spacing: 8) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(job.title).font(.headline).lineLimit(1)
                    Text(subtitle(job))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer()
                Text(AceSession.stageName(job))
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(job.stage == "failed" ? Color.red : job.stage == "done" ? Color.green : Color.primary)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(.quaternary, in: Capsule())
                if job.finished == true {
                    Button {
                        session.reuse(job)
                    } label: {
                        Image(systemName: "arrow.counterclockwise")
                    }
                    .buttonStyle(.borderless)
                    .help("Einstellungen übernehmen")
                }
                Button(role: job.finished == true ? .destructive : nil) {
                    Task { await session.delete(job) }
                } label: {
                    Image(systemName: job.finished == true ? "trash" : "xmark")
                }
                .buttonStyle(.borderless)
                .help(job.finished == true ? "Aus der Liste entfernen" : "Abbrechen")
            }
            if let caption = job.request.caption, !caption.isEmpty {
                Text(caption).font(.callout).lineLimit(2)
            }
            if job.stage == "failed", let message = job.message {
                Text(message).font(.caption).foregroundStyle(.red)
            }
            if job.finished != true, (job.stage ?? "queued") != "queued", let fraction = job.fraction {
                ProgressView(value: min(max(fraction, 0), 1))
            }
            if job.stage == "done" {
                let songs = session.songs(of: job)
                if !songs.isEmpty {
                    FlowLayout {
                        ForEach(songs) { song in
                            Button {
                                model.player.play(song, in: songs)
                            } label: {
                                Label(song.seed.map { "Song \(song.number ?? 1) · Seed \($0)" } ?? "Song \(song.number ?? 1)",
                                      systemImage: "play.fill")
                                    .font(.caption)
                            }
                            .buttonStyle(.bordered)
                        }
                    }
                }
            }
        }
        .padding(.vertical, 4)
    }

    private func subtitle(_ job: Components.Schemas.AceJob) -> String {
        let task = (job.request.task.flatMap(AceForm.Task.init(rawValue:)) ?? .text2music).name
        var parts = [task]
        if let created = job.createdAt {
            parts.append(created.formatted(date: .abbreviated, time: .shortened))
        }
        if let seconds = job.seconds {
            parts.append(Duration.seconds(seconds).formatted(.time(pattern: .minuteSecond)))
        }
        if let ranOn = model.ranOnNote(job.ranOn) {
            parts.append(ranOn)
        }
        return parts.joined(separator: " · ")
    }
}
