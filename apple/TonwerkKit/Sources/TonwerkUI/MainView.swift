import SwiftUI
import TonwerkKit

/// The pages in the sidebar (on the iPhone a list to go into): the form, library, queue and each playlist.
enum Page: Hashable {
    case create
    case library
    case review
    case queue
    case logic
    case instruments
    case voices
    case transcribe
    case log
    case storage
    case playlist(Int64)
}

struct MainView: View {
    @Environment(AppModel.self) private var model
    @State private var naming = false
    @State private var newName = ""

    var body: some View {
        // On the Mac the player takes its own row under the window's content, so nothing ends behind it; on the
        // iPhone and iPad the content scrolls under the bar's material, as Apple's own players do.
        #if os(macOS)
        VStack(spacing: 0) {
            split
            PlayerBar()
        }
        .modifier(Dialogs(naming: $naming, newName: $newName))
        #else
        split
            .safeAreaInset(edge: .bottom, spacing: 0) {
                PlayerBar()
            }
            .modifier(Dialogs(naming: $naming, newName: $newName))
        #endif
    }

    private var split: some View {
        @Bindable var model = model
        return NavigationSplitView {
            List(selection: $model.page) {
                Section {
                    Label("Erstellen", systemImage: "wand.and.stars").tag(Page.create)
                    Label("Bibliothek", systemImage: "music.note.list").tag(Page.library)
                    Label("Zum Anhören", systemImage: "star.leadinghalf.filled")
                        .badge(model.unrated.count)
                        .tag(Page.review)
                    Label("Warteschlange", systemImage: "hourglass")
                        .badge(model.queueCount)
                        .tag(Page.queue)
                }
                Section("Werkzeuge") {
                    Label("Logic", systemImage: "pianokeys").tag(Page.logic)
                    Label("Instrumente", systemImage: "slider.vertical.3").tag(Page.instruments)
                    Label("Stimmen", systemImage: "person.wave.2").tag(Page.voices)
                    Label("Transkription", systemImage: "music.quarternote.3").tag(Page.transcribe)
                    Label("Protokoll", systemImage: "doc.text.magnifyingglass").tag(Page.log)
                    Label("Speicher", systemImage: "internaldrive").tag(Page.storage)
                }
                Section("Playlists") {
                    ForEach(model.playlists, id: \.id) { playlist in
                        Label(playlist.name, systemImage: "list.bullet")
                            .badge(model.songs(of: playlist).count)
                            .tag(Page.playlist(playlist.id))
                    }
                    Button {
                        newName = ""
                        naming = true
                    } label: {
                        Label("Neue Playlist", systemImage: "plus")
                    }
                    .buttonStyle(.borderless)
                }
            }
            .navigationTitle("Tonwerk")
            #if os(macOS)
            .navigationSplitViewColumnWidth(min: 190, ideal: 220)
            .safeAreaInset(edge: .bottom) {
                ConnectionLabel()
                    .font(.caption)
                    .lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(10)
            }
            #else
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    NavigationLink {
                        SettingsView().navigationTitle("Einstellungen")
                    } label: {
                        Image(systemName: "gearshape")
                    }
                }
            }
            #endif
        } detail: {
            switch model.page ?? .library {
            case .create:
                CreateView()
            case .library:
                SongList(title: "Bibliothek", songs: model.songs)
            case .review:
                SongList(title: "Zum Anhören", songs: model.unrated, empty: "Alle Songs sind bewertet.")
            case .queue:
                QueueView()
            case .logic:
                LogicView()
            case .instruments:
                InstrumentsView()
            case .voices:
                VoicesView()
            case .transcribe:
                TranscriptionView()
            case .log:
                LogView()
            case .storage:
                StorageView()
            case .playlist(let id):
                if let playlist = model.playlists.first(where: { $0.id == id }) {
                    SongList(
                        title: playlist.name, songs: model.songs(of: playlist), playlist: playlist,
                        empty: "Noch keine Songs. Füge sie in der Bibliothek über das Menü eines Songs hinzu.")
                } else {
                    ContentUnavailableView("Playlist gelöscht", systemImage: "list.bullet")
                }
            }
        }
    }
}

/// The sheets and alerts the whole window shares.
private struct Dialogs: ViewModifier {
    @Environment(AppModel.self) private var model
    @Binding var naming: Bool
    @Binding var newName: String

    func body(content: Content) -> some View {
        @Bindable var model = model
        content
            .exportSheet(for: $model.sharing)
            .paintSheet(for: $model.painting)
            .sheet(item: $model.singing) { song in
                SingSheet(song: song)
            }
            .sheet(item: $model.filming) { song in
                VideoSheet(song: song)
            }
            .alert("Neue Playlist", isPresented: $naming) {
                TextField("Name", text: $newName)
                Button("Anlegen") { Task { await model.createPlaylist(named: newName) } }
                Button("Abbrechen", role: .cancel) {}
            }
    }
}

extension AppModel {
    /// Unrated songs older than a week, as the web page's review filter (`review.ts`, default 7 days) collects them.
    var unrated: [Song] {
        let cutoff = Date().addingTimeInterval(-7 * 24 * 3600)
        return songs.filter { $0.rating == nil && $0.hasAudio && ($0.createdAt.map { $0 < cutoff } ?? false) }
    }
}
