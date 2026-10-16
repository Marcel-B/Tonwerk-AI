import AVKit
import SwiftUI
import TonwerkKit

/// The music video dialog, the web page's `VideoDialog.vue`: the app draws the still layers and shows a still of the
/// result, the Mac renders the video in the background and lists it here (and in the queue) with its progress; a
/// finished one is watched, shared or saved.
struct VideoSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let song: Song
    /// The artist the share dialog remembers goes under the title.
    @AppStorage("exportArtist") private var artist = ""
    @State private var settings = VideoSettings.load()
    @State private var cover: CGImage?
    @State private var lines: [String]?
    @State private var layers: VideoLayers?
    @State private var preview: CGImage?
    @State private var drawing = false
    @State private var media: Components.Schemas.MediaInfo?
    @State private var loadingMedia = false
    @State private var loaded: [Components.Schemas.VideoState] = []
    @State private var sending = false
    /// The one video being watched, with its player, so a redraw of the list does not start it over.
    @State private var watching: (id: String, player: AVPlayer)?
    @State private var preparing: String?
    @State private var file: (id: String, url: URL)?
    @State private var saving = false
    @State private var message: String?
    @State private var failed = false

    /// What a new drawing depends on; the motif only matters on the server.
    private struct Drawing: Equatable {
        let settings: VideoSettings
        let lines: [String]?
        let artist: String
        let cover: Bool
    }

    var body: some View {
        NavigationStack {
            Form {
                Section { previewImage }
                pictureSection
                lookSection
                Section { start }
                if !videos.isEmpty {
                    Section {
                        ForEach(videos, id: \.id) { video in
                            row(video)
                        }
                    } header: {
                        CardHeading("Videos", systemImage: "film.stack")
                    }
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Musikvideo")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Fertig") { dismiss() }
                }
            }
            .task { await load() }
            .task(id: Drawing(settings: settings.drawn, lines: lines, artist: artist, cover: cover != nil)) {
                await redraw()
            }
            .onChange(of: settings) { settings.save() }
            .onDisappear { watching?.player.pause() }
            .onChange(of: settings.source) {
                if settings.photos && media == nil { Task { await loadMedia() } }
            }
            .fileMover(isPresented: $saving, file: file?.url) { result in
                if case .success = result {
                    file = nil
                    failed = false
                    message = String(localized: "Gesichert.")
                }
            }
        }
        #if os(macOS)
        .frame(minWidth: 500, idealWidth: 560, minHeight: 640, idealHeight: 760)
        #endif
    }

    private var videos: [Components.Schemas.VideoState] {
        model.videos(of: song, loaded: loaded)
    }

    /// The Mac's folders the photos can come from; empty ones cannot be picked.
    private var usableFolders: [Components.Schemas.MediaFolder] {
        (media?.folders ?? []).filter { $0.photos + $0.videos > 0 }
    }

    private var ready: Bool {
        layers != nil && !drawing && !sending
            && (!settings.photos || (media?.installed == true && usableFolders.contains { $0.name == settings.media }))
    }

    private var previewImage: some View {
        let layout = VideoLayout.of(settings.format)
        return ZStack {
            Rectangle().fill(.black.opacity(0.85))
            if let preview {
                Image(decorative: preview, scale: 1)
                    .resizable()
                    .scaledToFit()
            }
            if drawing && preview == nil {
                ProgressView()
            }
        }
        .aspectRatio(Double(layout.width) / Double(layout.height), contentMode: .fit)
        .frame(maxWidth: .infinity, maxHeight: settings.format == .portrait ? 420 : nil)
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .listRowInsets(EdgeInsets())
        .listRowBackground(Color.clear)
    }

    private var pictureSection: some View {
        Section {
            Picker("Bild", selection: $settings.source) {
                ForEach(VideoSource.allCases) { source in
                    Text(source.name).tag(source)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            if settings.photos {
                if let media, !media.installed {
                    Text("Fotovideos brauchen auf dem Mac einmal deploy/install-montage.sh (oder setup-mac.sh mit --montage).")
                        .foregroundStyle(.red)
                } else {
                    HStack {
                        Picker("Ordner", selection: $settings.media) {
                            if !usableFolders.contains(where: { $0.name == settings.media }) {
                                Text("Kein Ordner").tag(settings.media)
                            }
                            ForEach(usableFolders, id: \.name) { folder in
                                Text("\(folder.name) (\(folder.photos) Fotos, \(folder.videos) Clips)").tag(folder.name)
                            }
                        }
                        Button {
                            Task { await loadMedia() }
                        } label: {
                            if loadingMedia {
                                ProgressView().controlSize(.small)
                            } else {
                                Image(systemName: "arrow.clockwise")
                            }
                        }
                        .buttonStyle(.borderless)
                        .help("Ordner neu lesen")
                    }
                    TextField("Motiv", text: $settings.motif, prompt: Text("z. B. Meer, Berge, Menschen"))
                }
            }
        } header: {
            CardHeading("Bild", systemImage: "photo")
        } footer: {
            if settings.photos {
                Text(media.map { "Ordner liegen in \($0.root) auf dem Mac. Ein Motiv wählt passende Bilder, leer nimmt die schärfsten. Geschnitten wird auf den Takt." }
                    ?? "Die Fotos und Clips kommen aus einem Ordner auf dem Mac, geschnitten auf den Takt.")
            }
        }
    }

    private var lookSection: some View {
        Section {
            Picker("Format", selection: $settings.format) {
                ForEach(VideoFormat.allCases) { format in
                    Text(format.name).tag(format)
                }
            }
            Picker("Musik im Bild", selection: $settings.effect) {
                ForEach(VideoEffect.allCases) { effect in
                    Text(effect.name).tag(effect)
                }
            }
            if !settings.photos {
                Picker("Bewegung", selection: $settings.motion) {
                    ForEach(VideoMotion.allCases) { motion in
                        Text(motion.name).tag(motion)
                    }
                }
            }
            Toggle("Cover zeigen", isOn: $settings.showCover)
            Toggle("Titel zeigen", isOn: $settings.showTitle)
            Toggle("Songtext einblenden", isOn: $settings.showLyrics)
                .disabled(lines == nil)
        } header: {
            CardHeading("Aussehen", systemImage: "paintpalette")
        } footer: {
            Text(lines == nil
                ? "Der Songtext lässt sich einblenden, sobald seine Zeiten gehört sind (im großen Player)."
                : "Die Musik bewegt Balken oder Welle und die Partikel, die gesungene Zeile steht im Band.")
        }
    }

    @ViewBuilder
    private var start: some View {
        Button(action: send) {
            HStack(spacing: 8) {
                if sending {
                    ProgressView().controlSize(.small)
                } else {
                    Image(systemName: "film")
                }
                Text(sending ? "Wird gesendet …" : "Video erstellen")
            }
            .frame(maxWidth: .infinity)
        }
        .buttonStyle(.borderedProminent)
        .disabled(!ready)
        if let message {
            Label(message, systemImage: failed ? "exclamationmark.triangle" : "checkmark.circle")
                .foregroundStyle(failed ? Color.red : Theme.accent)
        }
    }

    @ViewBuilder
    private func row(_ video: Components.Schemas.VideoState) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(AppModel.describe(video)).lineLimit(2)
                    Text(details(video)).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                if video.stage == "done" {
                    Button {
                        watch(video)
                    } label: {
                        Image(systemName: watching?.id == video.id ? "eye.slash" : "play.rectangle")
                    }
                    .help(watching?.id == video.id ? "Ausblenden" : "Ansehen")
                    Button {
                        Task { await prepare(video) }
                    } label: {
                        if preparing == video.id {
                            ProgressView().controlSize(.small)
                        } else {
                            Image(systemName: "square.and.arrow.down.on.square")
                        }
                    }
                    .disabled(preparing != nil)
                    .help("Zum Teilen oder Sichern laden")
                } else {
                    Text(AppModel.videoStage(video))
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(.quaternary, in: Capsule())
                }
                Button(role: .destructive) {
                    Task { await remove(video) }
                } label: {
                    Image(systemName: video.finished == true ? "trash" : "xmark")
                }
                .help(video.finished == true ? "Löschen" : "Abbrechen")
            }
            .buttonStyle(.borderless)
            if video.stage == "rendering" {
                ProgressView(value: min(max(video.fraction ?? 0, 0), 1))
            }
            if video.stage == "failed", let problem = video.message {
                Text(problem).font(.caption).foregroundStyle(.red)
            }
            if let watching, watching.id == video.id {
                VideoPlayer(player: watching.player)
                    .aspectRatio(video.format == VideoFormat.portrait.rawValue ? 9.0 / 16 : 16.0 / 9, contentMode: .fit)
                    .frame(maxHeight: 420)
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
            }
            if let file, file.id == video.id {
                HStack {
                    ShareLink(item: file.url) {
                        Label("Teilen", systemImage: "square.and.arrow.up")
                    }
                    Spacer()
                    Button {
                        saving = true
                    } label: {
                        Label("Sichern …", systemImage: "square.and.arrow.down")
                    }
                }
                .buttonStyle(.bordered)
            }
        }
        .padding(.vertical, 2)
    }

    private func details(_ video: Components.Schemas.VideoState) -> String {
        var parts: [String] = []
        if let created = video.createdAt {
            parts.append(created.formatted(date: .abbreviated, time: .shortened))
        }
        if let bytes = video.bytes {
            parts.append(ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file))
        }
        return parts.joined(separator: " · ")
    }

    // MARK: Work

    private func load() async {
        if settings.photos { Task { await loadMedia() } }
        async let picture = model.videoCover(of: song)
        async let timed = model.timedLines(of: song)
        async let listed = try? model.listVideos(of: song)
        cover = await picture
        lines = await timed
        loaded = await listed ?? []
    }

    private func loadMedia() async {
        loadingMedia = true
        defer { loadingMedia = false }
        do {
            let info = try await model.videoMedia()
            media = info
            if !info.folders.contains(where: { $0.name == settings.media && $0.photos + $0.videos > 0 }) {
                settings.media = info.folders.first { $0.photos + $0.videos > 0 }?.name ?? ""
            }
        } catch {
            failed = true
            message = error.localizedDescription
        }
    }

    /// Drawn again on every change; a newer drawing cancels this task before it lands.
    private func redraw() async {
        guard let cover else { return }
        drawing = true
        let settings = settings.drawn
        let (songId, title, subtitle, lyrics) = (song.id, song.title, artist.trimmingCharacters(in: .whitespaces), lines)
        let result = await Task.detached(priority: .userInitiated) { () -> (VideoLayers, CGImage?)? in
            guard let layers = VideoArt.layers(
                cover: cover, songId: songId, title: title, subtitle: subtitle, lyrics: lyrics, settings: settings)
            else { return nil }
            return (layers, VideoArt.preview(of: layers, settings: settings))
        }.value
        guard !Task.isCancelled else { return }
        layers = result?.0
        preview = result?.1
        drawing = false
        if result == nil {
            failed = true
            message = String(localized: "Das Bild ließ sich nicht zeichnen.")
        }
    }

    private func send() {
        guard let layers else { return }
        sending = true
        message = nil
        Task {
            do {
                let video = try await model.addVideo(song, settings: settings, layers: layers)
                loaded = [video] + loaded.filter { $0.id != video.id }
                failed = false
                message = String(localized: "Der Mac rendert das Video, es erscheint hier und in der Warteschlange.")
            } catch {
                failed = true
                message = String(localized: "Das Video ließ sich nicht starten: \(error.localizedDescription)")
            }
            sending = false
        }
    }

    private func watch(_ video: Components.Schemas.VideoState) {
        watching?.player.pause()
        guard watching?.id != video.id, let url = model.videoURL(video) else {
            watching = nil
            return
        }
        model.player.pause()
        let player = AVPlayer(url: url)
        watching = (video.id, player)
        player.play()
    }

    private func prepare(_ video: Components.Schemas.VideoState) async {
        preparing = video.id
        defer { preparing = nil }
        do {
            file = (video.id, try await model.downloadVideo(video))
        } catch {
            failed = true
            message = error.localizedDescription
        }
    }

    private func remove(_ video: Components.Schemas.VideoState) async {
        do {
            try await model.deleteVideo(video)
            loaded.removeAll { $0.id == video.id }
            if watching?.id == video.id { watching = nil }
            if file?.id == video.id { file = nil }
        } catch {
            failed = true
            message = error.localizedDescription
        }
    }
}
