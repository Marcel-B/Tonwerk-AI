import SwiftUI
import TonwerkKit

/// A list of songs to play, rate and sort into playlists: the library, the songs to review, a playlist.
struct SongList: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    let title: String
    let songs: [Song]
    var playlist: Components.Schemas.PlaylistInfo?
    var empty: String = "Noch keine Songs."
    /// Whether this list answers ``AppModel/revealing``: only the library's does.
    var reveals = false
    @State private var query = ""
    @State private var scope = Song.SearchScope.titleStyle
    @State private var selected: Song.ID?
    /// The song to scroll to once the list shows it, after a reveal.
    @State private var scrollTarget: Song.ID?
    /// The song whose deletion waits for its confirmation.
    @State private var deleting: Song?
    @State private var deleteProblem: String?
    /// Choosing several songs to delete together (library and review list, not a playlist).
    @State private var picking = false
    @State private var picked = Set<Song.ID>()
    /// The chosen songs' deletion, waiting for its confirmation.
    @State private var bulk: SongDeletion?
    @State private var bulkDeleting = false
    @AppStorage("librarySort") private var sort = LibrarySort.newest
    @AppStorage("ratingFilter") private var storedFilter = 0
    #if os(macOS)
    @AppStorage("detailsOpen") private var details = true
    #else
    /// On the iPhone the details come up as a sheet over the list, opened from the row's ⓘ or its menu; nothing stays
    /// selected, as in the Music app.
    @State private var sheetSong: Song.ID?
    #endif

    private var filter: RatingFilter { RatingFilter(stored: storedFilter) }

    /// A playlist keeps its own order and every song; the library and the review list take order and filter.
    private var shown: [Song] {
        let found = songs.filter { $0.matches(query, in: scope) }
        guard playlist == nil else { return found }
        return sort.apply(to: found.filter(filter.admits))
    }

    private var chosen: Song? {
        selected.flatMap { id in songs.first { $0.id == id } }
    }

    var body: some View {
        withPicking(withDetails(content))
            .onChange(of: model.revealing, initial: true) { revealIfAsked() }
            .confirmationDialog(
                deleting.map { "„\($0.displayTitle)“ löschen?" } ?? "",
                isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }),
                titleVisibility: .visible, presenting: deleting
            ) { song in
                Button("Löschen", role: .destructive) {
                    Task {
                        do {
                            try await model.deleteSong(song)
                        } catch {
                            deleteProblem = error.localizedDescription
                        }
                    }
                }
            } message: { _ in
                Text("Der Song wird mit seinen Versionen, Stems, Videos und seinem Cover vom Server gelöscht. Das lässt sich nicht rückgängig machen.")
            }
            .alert(
                "Nicht gelöscht",
                isPresented: Binding(get: { deleteProblem != nil }, set: { if !$0 { deleteProblem = nil } })
            ) {
                Button("OK", role: .cancel) {}
            } message: {
                Text(deleteProblem ?? "")
            }
    }

    @ViewBuilder
    private var content: some View {
        Group {
            if let error = model.libraryError, model.songs.isEmpty {
                ContentUnavailableView {
                    Label("Bibliothek nicht geladen", systemImage: "wifi.exclamationmark")
                } description: {
                    Text(error)
                } actions: {
                    Button("Nochmal") { Task { await model.reloadLibrary() } }
                }
            } else if songs.isEmpty {
                ContentUnavailableView(title, systemImage: "music.note", description: Text(empty))
            } else if shown.isEmpty {
                if query.isEmpty {
                    ContentUnavailableView {
                        Label("Keine Songs mit dieser Bewertung", systemImage: "star.slash")
                    } actions: {
                        Button("Alle zeigen") { storedFilter = RatingFilter.all.stored }
                    }
                } else {
                    ContentUnavailableView.search(text: query)
                }
            } else if picking {
                pickList
            } else {
                songList
            }
        }
        .navigationTitle(title)
        .searchable(text: $query, prompt: scope == .lyrics ? "Im Songtext suchen" : "Titel oder Stil")
        .searchScopes($scope) {
            Text("Titel und Stil").tag(Song.SearchScope.titleStyle)
            Text("Songtext").tag(Song.SearchScope.lyrics)
        }
        .refreshable { await model.reloadLibrary() }
        .toolbar {
            ToolbarItemGroup {
                Button {
                    if let first = shown.first(where: \.hasAudio) {
                        player.play(first, in: shown)
                    }
                } label: {
                    Label("Alle abspielen", systemImage: "play.fill")
                }
                .disabled(!shown.contains(where: \.hasAudio))
                if playlist == nil {
                    orderMenu
                    Button {
                        picking ? stopPicking() : startPicking()
                    } label: {
                        Label(picking ? "Auswahl beenden" : "Auswählen",
                              systemImage: picking ? "checkmark.circle.fill" : "checkmark.circle")
                    }
                    .disabled(shown.isEmpty && !picking)
                }
                #if os(macOS)
                Button {
                    details.toggle()
                } label: {
                    Label("Details", systemImage: "info.circle")
                }
                #endif
            }
        }
    }

    /// The Mac selects with a click and plays with a double click, its details beside the list; the iPhone plays with
    /// a tap and opens the details from the row's ⓘ, as the Music app does.
    #if os(macOS)
    private var songList: some View {
        ScrollViewReader { proxy in
            macList.onChange(of: scrollTarget, initial: true) { scroll(proxy) }
        }
    }

    private var macList: some View {
        List(selection: $selected) {
            ForEach(shown) { song in
                SongRow(song: song, playing: player.current?.id == song.id)
                    .tag(song.id)
            }
            .onDelete(perform: playlist == nil ? nil : { remove($0) })
        }
        .contextMenu(forSelectionType: Song.ID.self) { ids in
            if let song = ids.first.flatMap({ model.song($0) }) {
                menu(for: song)
            }
        } primaryAction: { ids in
            if let song = ids.first.flatMap({ model.song($0) }), song.hasAudio {
                player.play(song, in: shown)
            }
        }
        .listStyle(.inset(alternatesRowBackgrounds: true))
    }

    private func withDetails(_ content: some View) -> some View {
        content.inspector(isPresented: $details) {
            Group {
                if let chosen {
                    SongDetail(song: chosen, list: shown)
                        .id(chosen.id)
                } else {
                    ContentUnavailableView(
                        "Kein Song gewählt", systemImage: "music.note",
                        description: Text("Wähle einen Song, um Songtext, Notiz und Versionen zu sehen."))
                }
            }
            .inspectorColumnWidth(min: 280, ideal: 340, max: 520)
        }
    }
    #else
    private var songList: some View {
        ScrollViewReader { proxy in
            phoneList.onChange(of: scrollTarget, initial: true) { scroll(proxy) }
        }
    }

    private var phoneList: some View {
        List {
            ForEach(shown) { song in
                SongRow(song: song, playing: player.current?.id == song.id) {
                    sheetSong = song.id
                }
                .contentShape(Rectangle())
                .onTapGesture {
                    if song.hasAudio {
                        player.play(song, in: shown)
                    }
                }
                .contextMenu { menu(for: song) }
            }
            .onDelete(perform: playlist == nil ? nil : { remove($0) })
        }
        .listStyle(.plain)
    }

    private var sheetShown: Binding<Bool> {
        Binding(get: { sheetSong != nil }, set: { if !$0 { sheetSong = nil } })
    }

    private func withDetails(_ content: some View) -> some View {
        content.sheet(isPresented: sheetShown) {
            if let song = sheetSong.flatMap({ model.song($0) }) {
                NavigationStack {
                    SongDetail(song: song, list: shown)
                        .id(song.id)
                        .navigationTitle(song.displayTitle)
                        .navigationBarTitleDisplayMode(.inline)
                        .toolbar {
                            ToolbarItem(placement: .confirmationAction) {
                                Button("Fertig") { sheetSong = nil }
                            }
                        }
                }
                .presentationDetents([.medium, .large])
            }
        }
    }
    #endif

    // MARK: Choosing several songs to delete together

    /// Every listed song: what the search and the rating filter let through, so "all" means the hits.
    private var allPicked: Bool {
        !shown.isEmpty && shown.allSatisfy { picked.contains($0.id) }
    }

    private var deletion: SongDeletion {
        SongDeletion(picked: picked, library: model.songs)
    }

    private func startPicking() {
        picked = []
        picking = true
    }

    private func stopPicking() {
        picking = false
        picked = []
    }

    private func toggle(_ id: Song.ID) {
        if picked.contains(id) {
            picked.remove(id)
        } else {
            picked.insert(id)
        }
    }

    /// A tap chooses rather than plays; the boxes stand where the cover's play state would draw the eye first.
    private var pickList: some View {
        List {
            ForEach(shown) { song in
                let on = picked.contains(song.id)
                HStack(spacing: 10) {
                    Image(systemName: on ? "checkmark.circle.fill" : "circle")
                        .imageScale(.large)
                        .foregroundStyle(on ? AnyShapeStyle(Theme.accent) : AnyShapeStyle(.secondary))
                    SongRow(song: song, playing: player.current?.id == song.id)
                }
                .contentShape(Rectangle())
                .onTapGesture { toggle(song.id) }
                .accessibilityAddTraits(on ? [.isButton, .isSelected] : .isButton)
            }
        }
        #if os(macOS)
        .listStyle(.inset(alternatesRowBackgrounds: true))
        #else
        .listStyle(.plain)
        #endif
    }

    private var pickBar: some View {
        let deletion = deletion
        return HStack(spacing: 12) {
            Text(verbatim: deletion.count == 0
                ? String(localized: "Songs zum Löschen wählen")
                : String(localized: "\(deletion.count) gewählt · \(deletion.size)"))
                .font(.callout)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            Spacer(minLength: 4)
            if allPicked {
                Button("Keine") { picked = [] }
            } else {
                Button("Alle (\(shown.count))") { picked.formUnion(shown.map(\.id)) }
            }
            Button(role: .destructive) {
                bulk = deletion
            } label: {
                if bulkDeleting {
                    ProgressView().controlSize(.small)
                } else {
                    Label("Löschen", systemImage: "trash")
                }
            }
            .buttonStyle(.borderedProminent)
            .tint(.red)
            .disabled(deletion.count == 0 || bulkDeleting)
        }
        .padding(.horizontal)
        .padding(.vertical, 10)
        .background(.bar)
    }

    private func withPicking(_ content: some View) -> some View {
        content
            .safeAreaInset(edge: .bottom) {
                if picking { pickBar }
            }
            // A song deleted elsewhere is no longer chosen.
            .onChange(of: songs) {
                let listed = Set(songs.map(\.id))
                picked.formIntersection(listed)
            }
            .confirmationDialog(
                bulk?.question ?? "",
                isPresented: Binding(get: { bulk != nil }, set: { if !$0 { bulk = nil } }),
                titleVisibility: .visible, presenting: bulk
            ) { deletion in
                Button(
                    deletion.count == 1
                        ? String(localized: "Löschen") : String(localized: "\(deletion.count) Songs löschen"),
                    role: .destructive
                ) {
                    Task { await deleteChosen(deletion) }
                }
            } message: { deletion in
                Text(deletion.detail)
            }
    }

    private func deleteChosen(_ deletion: SongDeletion) async {
        bulkDeleting = true
        let result = await model.deleteSongs(deletion)
        bulkDeleting = false
        picked.subtract(result.deleted)
        if let problem = result.problem {
            deleteProblem = String(localized: "\(result.failed) von \(deletion.count) Songs blieben: \(problem)")
        } else {
            stopPicking()
        }
    }

    private var orderMenu: some View {
        Menu {
            Picker("Reihenfolge", selection: $sort) {
                ForEach(LibrarySort.allCases) { sort in
                    Text(sort.name).tag(sort)
                }
            }
            Picker("Bewertung", selection: $storedFilter) {
                ForEach(RatingFilter.choices, id: \.self) { filter in
                    Text(filter.name).tag(filter.stored)
                }
            }
        } label: {
            Label(
                "Ordnen und filtern",
                systemImage: filter == .all
                    ? "line.3.horizontal.decrease.circle" : "line.3.horizontal.decrease.circle.fill")
        }
    }

    @ViewBuilder
    private func menu(for song: Song) -> some View {
        Button {
            player.play(song, in: shown)
        } label: {
            Label("Abspielen", systemImage: "play")
        }
        .disabled(!song.hasAudio)
        Button {
            #if os(macOS)
            selected = song.id
            details = true
            #else
            sheetSong = song.id
            #endif
        } label: {
            Label("Details", systemImage: "info.circle")
        }
        Button {
            Task { await model.useInForm(song) }
        } label: {
            Label("Ins Formular übernehmen", systemImage: "square.and.pencil")
        }
        Button {
            Task { await model.useAsAceTemplate(song) }
        } label: {
            Label("Als ACE-Vorlage", systemImage: "waveform.badge.plus")
        }
        if song.rendersFull {
            Button {
                Task { _ = try? await model.renderFull(song) }
            } label: {
                Label("In voller Qualität rendern", systemImage: "waveform")
            }
            .disabled(model.isRendering(song))
        }
        Button {
            Task { await model.useScoreInForm(song) }
        } label: {
            Label("Als Partitur übernehmen", systemImage: "music.quarternote.3")
        }
        .disabled(!song.hasScore)
        Button {
            model.openInLogic(song)
        } label: {
            Label("In Logic öffnen", systemImage: "pianokeys")
        }
        .disabled(!song.hasScore)
        Button {
            model.sharing = song
        } label: {
            Label("Teilen …", systemImage: "square.and.arrow.up")
        }
        .disabled(!song.hasAudio)
        Button {
            model.painting = song
        } label: {
            Label("Cover malen …", systemImage: "paintbrush.pointed")
        }
        Button {
            model.filming = song
        } label: {
            Label("Musikvideo …", systemImage: "film")
        }
        .disabled(!song.hasAudio)
        Button {
            model.singing = song
        } label: {
            Label("Mit Stimme singen …", systemImage: "person.wave.2")
        }
        .disabled(!song.hasAudio)
        Button {
            model.openStems(song)
        } label: {
            Label("Stems erzeugen …", systemImage: "square.3.layers.3d")
        }
        .disabled(!song.hasAudio)
        Menu {
            ForEach(model.playlists, id: \.id) { list in
                Button {
                    Task { await model.toggle(song, in: list) }
                } label: {
                    if list.songIds.contains(song.id) {
                        Label(list.name, systemImage: "checkmark")
                    } else {
                        Text(list.name)
                    }
                }
            }
        } label: {
            Label("Playlist", systemImage: "text.badge.plus")
        }
        Menu {
            ForEach((1...5).reversed(), id: \.self) { stars in
                Button(String(repeating: "★", count: stars)) { Task { await model.rate(song, stars) } }
            }
            Button("Keine Bewertung") { Task { await model.rate(song, nil) } }
        } label: {
            Label("Bewerten", systemImage: "star")
        }
        if let playlist {
            Button(role: .destructive) {
                Task { await model.toggle(song, in: playlist) }
            } label: {
                Label("Aus der Playlist nehmen", systemImage: "minus.circle")
            }
        }
        Divider()
        Button(role: .destructive) {
            deleting = song
        } label: {
            Label("Song löschen …", systemImage: "trash")
        }
    }

    /// Takes a song the player asked to show: the search and rating filter give way if they hide it, the Mac selects
    /// it (its details beside the list), the iPhone opens its details, as nothing stays selected there.
    private func revealIfAsked() {
        guard reveals, let id = model.revealing else { return }
        model.revealing = nil
        guard songs.contains(where: { $0.id == id }) else { return }
        if !shown.contains(where: { $0.id == id }) {
            query = ""
            storedFilter = RatingFilter.all.stored
        }
        #if os(macOS)
        selected = id
        #else
        // The full-screen player is still going away; a sheet asked for meanwhile would not come up.
        Task {
            try? await Task.sleep(for: .milliseconds(500))
            sheetSong = id
        }
        #endif
        scrollTarget = id
    }

    private func scroll(_ proxy: ScrollViewProxy) {
        guard let id = scrollTarget else { return }
        // A frame later, so the list has its rows after a page change or a cleared filter.
        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(100))
            withAnimation { proxy.scrollTo(id, anchor: .center) }
            scrollTarget = nil
        }
    }

    private func remove(_ offsets: IndexSet) {
        guard let playlist else { return }
        let gone = Set(offsets.map { shown[$0].id })
        Task { await model.setSongs(playlist.songIds.filter { !gone.contains($0) }, of: playlist) }
    }
}

struct SongRow: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    let song: Song
    let playing: Bool
    /// The iPhone's ⓘ, which opens the details; the Mac shows them beside the list.
    var info: (() -> Void)?

    var body: some View {
        HStack(spacing: 12) {
            CoverView(song: song)
                .frame(width: 44, height: 44)
                .shadow(color: .black.opacity(0.15), radius: 3, y: 1)
                .overlay {
                    if playing {
                        RoundedRectangle(cornerRadius: 6, style: .continuous)
                            .fill(.black.opacity(0.35))
                        PlayingIndicator(playing: player.isPlaying)
                            .foregroundStyle(.white)
                    }
                }
            VStack(alignment: .leading, spacing: 2) {
                Text(song.displayTitle)
                    .fontWeight(playing ? .semibold : .regular)
                    .foregroundStyle(playing ? Theme.accent : Color.primary)
                    .lineLimit(1)
                Text(song.style)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            if song.note != nil {
                Image(systemName: "text.bubble")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .help("Hat eine Notiz")
            }
            if !song.versions.isEmpty {
                Image(systemName: "person.wave.2")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .help("Hat Versionen mit anderer Stimme")
            }
            if let duration = song.duration {
                Text(duration)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            RatingView(rating: song.rating) { stars in
                Task { await model.rate(song, stars) }
            }
            if let info {
                Button(action: info) {
                    Image(systemName: "info.circle")
                        .imageScale(.large)
                }
                .buttonStyle(.borderless)
                .accessibilityLabel("Details")
            }
        }
        .padding(.vertical, 2)
        .opacity(song.hasAudio ? 1 : 0.5)
    }
}

/// Five stars; tapping the given rating again clears it.
struct RatingView: View {
    let rating: Int?
    var font: Font = .caption
    let rate: (Int?) -> Void

    var body: some View {
        HStack(spacing: 2) {
            ForEach(1...5, id: \.self) { stars in
                Button {
                    rate(rating == stars ? nil : stars)
                } label: {
                    Image(systemName: stars <= (rating ?? 0) ? "star.fill" : "star")
                        .font(font)
                        .foregroundStyle(stars <= (rating ?? 0) ? AnyShapeStyle(.yellow) : AnyShapeStyle(.tertiary))
                }
                .buttonStyle(.plain)
                .accessibilityLabel("\(stars) Sterne")
            }
        }
        .accessibilityElement(children: .contain)
    }
}

extension AppModel {
    /// Deletes the song on the server, with its versions, stems, videos and cover, as the web library does; the
    /// server refuses (409) while its worker is on the song.
    func deleteSong(_ song: Song) async throws {
        _ = try await call("DELETE", address("api/songs", song.run, song.name), accept: [200, 202, 204, 404])
        await reloadLibrary()
    }
}

/// The stars only to read, where a song is picked rather than heard (the Logic page's song list).
struct RatingStars: View {
    let rating: Int?
    var font: Font = .caption2

    var body: some View {
        HStack(spacing: 1) {
            ForEach(1...5, id: \.self) { stars in
                Image(systemName: stars <= (rating ?? 0) ? "star.fill" : "star")
                    .foregroundStyle(stars <= (rating ?? 0) ? AnyShapeStyle(.yellow) : AnyShapeStyle(.tertiary))
            }
        }
        .font(font)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(rating.map { "\($0) Sterne" } ?? "Nicht bewertet")
    }
}

/// The song's own cover, or one drawn from its id like the web page's (`export.ts` hues the gradient by the id).
struct CoverView: View {
    @Environment(AppModel.self) private var model
    let song: Song

    var body: some View {
        Group {
            if let server = model.server, let url = song.coverURL(on: server) {
                AsyncImage(url: url) { image in
                    image.resizable().scaledToFill()
                } placeholder: {
                    drawn
                }
            } else {
                drawn
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
    }

    private var drawn: some View {
        DrawnCover(id: song.id)
    }
}

/// The cover a song without its own gets: a gradient hued by the id, as the web page draws it (`export.ts`).
struct DrawnCover: View {
    let id: String
    /// The note in the middle; the exported cover has the title instead and square corners.
    var symbol = true

    var body: some View {
        let hue = Double(id.unicodeScalars.reduce(0) { ($0 &* 31 &+ Int($1.value)) & 0xFFFF } % 360) / 360
        LinearGradient(
            colors: [
                Color(hue: hue, saturation: 0.6, brightness: 0.75),
                Color(hue: (hue + 0.12).truncatingRemainder(dividingBy: 1), saturation: 0.7, brightness: 0.4),
            ],
            startPoint: .topLeading, endPoint: .bottomTrailing
        )
        .overlay {
            if symbol {
                Image(systemName: "music.note")
                    .foregroundStyle(.white.opacity(0.7))
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: symbol ? 6 : 0, style: .continuous))
    }
}
