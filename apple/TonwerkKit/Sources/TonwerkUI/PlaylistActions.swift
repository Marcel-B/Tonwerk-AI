import SwiftUI
import TonwerkKit

extension AppModel {
    /// Whether a playlist may be deleted: never the last one, as on the web page.
    var playlistDeletable: Bool { playlists.count > 1 }
}

/// Renaming and deleting a playlist, for the window's dialogs; the deletion asks first, as the library's does.
struct PlaylistDialogs: ViewModifier {
    @Environment(AppModel.self) private var model
    @State private var name = ""
    @State private var problem: String?

    func body(content: Content) -> some View {
        @Bindable var model = model
        content
            .onChange(of: model.renamingPlaylist?.id) { name = model.renamingPlaylist?.name ?? "" }
            .alert(
                "Playlist umbenennen",
                isPresented: Binding(get: { model.renamingPlaylist != nil }, set: { if !$0 { model.renamingPlaylist = nil } }),
                presenting: model.renamingPlaylist
            ) { playlist in
                TextField("Name", text: $name)
                Button("Umbenennen") { Task { await model.renamePlaylist(playlist, to: name) } }
                Button("Abbrechen", role: .cancel) {}
            }
            .confirmationDialog(
                model.deletingPlaylist.map { String(localized: "Playlist „\($0.name)“ löschen?") } ?? "",
                isPresented: Binding(get: { model.deletingPlaylist != nil }, set: { if !$0 { model.deletingPlaylist = nil } }),
                titleVisibility: .visible, presenting: model.deletingPlaylist
            ) { playlist in
                Button("Playlist löschen", role: .destructive) {
                    Task {
                        do {
                            try await model.deletePlaylist(playlist)
                        } catch {
                            problem = error.localizedDescription
                        }
                    }
                }
            } message: { playlist in
                Text("Nur die Liste wird gelöscht, ihre \(playlist.songIds.count) Songs bleiben in der Bibliothek.")
            }
            .alert(
                "Playlist nicht gelöscht",
                isPresented: Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })
            ) {
                Button("OK", role: .cancel) {}
            } message: {
                Text(problem ?? "")
            }
    }
}

/// The playlist's actions, as a menu in its page's toolbar and as the context menu of its row in the sidebar.
struct PlaylistActions: View {
    @Environment(AppModel.self) private var model
    let playlist: Components.Schemas.PlaylistInfo

    var body: some View {
        Button {
            model.renamingPlaylist = playlist
        } label: {
            Label("Umbenennen …", systemImage: "pencil")
        }
        Button(role: .destructive) {
            model.deletingPlaylist = playlist
        } label: {
            Label(model.playlistDeletable ? "Löschen …" : "Löschen (letzte Playlist)", systemImage: "trash")
        }
        .disabled(!model.playlistDeletable)
    }
}
