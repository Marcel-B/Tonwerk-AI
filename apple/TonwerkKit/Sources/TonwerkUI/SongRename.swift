import SwiftUI
import TonwerkKit

extension View {
    /// The dialog that renames a song's run, opened from the song's menu and the pencil by the title in its details.
    /// A dialog rather than an editable title, since a text field in the header neither looks editable nor says when it
    /// has saved.
    func renamingSong(_ song: Binding<Song?>) -> some View {
        modifier(SongRenameDialog(song: song))
    }
}

private struct SongRenameDialog: ViewModifier {
    @Environment(AppModel.self) private var model
    @Binding var song: Song?
    @State private var newTitle = ""
    @State private var problem: String?

    func body(content: Content) -> some View {
        content
            .onChange(of: song?.id, initial: true) {
                newTitle = song?.title ?? ""
            }
            .alert(
                "Song umbenennen",
                isPresented: Binding(get: { song != nil }, set: { if !$0 { song = nil } }),
                presenting: song
            ) { song in
                TextField("Titel", text: $newTitle)
                Button("Abbrechen", role: .cancel) {}
                Button("Umbenennen") { rename(song) }
            } message: { song in
                Text(message(for: song))
            }
            .alert(
                "Nicht umbenannt",
                isPresented: Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })
            ) {
                Button("OK", role: .cancel) {}
            } message: {
                Text(problem ?? "")
            }
    }

    private func message(for song: Song) -> String {
        var lines = [String]()
        if song.number != nil {
            lines.append(String(localized: "Der Titel gilt für alle Songs dieses Durchlaufs."))
        }
        lines.append(String(localized: "Leer lassen für den ursprünglichen Titel „\(song.originalTitle)“."))
        return lines.joined(separator: " ")
    }

    private func rename(_ song: Song) {
        let title = newTitle
        guard title.trimmingCharacters(in: .whitespacesAndNewlines) != song.title else { return }
        Task {
            do {
                try await model.rename(song, to: title)
            } catch {
                problem = error.localizedDescription
            }
        }
    }
}
