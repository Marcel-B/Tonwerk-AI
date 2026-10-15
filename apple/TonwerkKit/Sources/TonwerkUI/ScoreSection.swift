import SwiftUI
import TonwerkKit
import UniformTypeIdentifiers

/// The song's own score, as the web page's `ScoreField.vue`: a score decides melody, chords and form. The switch sends
/// it or keeps it unsent; − and + and the key picker rewrite it, since YuE2 sings the "Vocal" voice as written.
struct ScoreSection: View {
    @Environment(AppModel.self) private var model
    @State private var importing = false
    @State private var message: String?
    @State private var failed = false

    var body: some View {
        @Bindable var model = model
        Section {
            Toggle("Partitur verwenden", isOn: $model.form.useAbc)
            if !model.form.useAbc && hasScore {
                Text("Ausgeschaltet: Die Partitur bleibt stehen, wird aber nicht mitgeschickt; YuE2 plant den Song selbst.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            TextEditor(text: $model.form.abc)
                .font(.body.monospaced())
                .frame(minHeight: hasScore ? 180 : 60)
                .scrollContentBackground(.hidden)
                .opacity(model.form.useAbc ? 1 : 0.6)
            if hasScore {
                HStack {
                    Button {
                        model.form.abc = AbcScore.transpose(model.form.abc, by: -1).abc
                    } label: {
                        Image(systemName: "minus")
                    }
                    .help("Einen Halbton tiefer")
                    Button {
                        model.form.abc = AbcScore.transpose(model.form.abc, by: 1).abc
                    } label: {
                        Image(systemName: "plus")
                    }
                    .help("Einen Halbton höher")
                    Picker("Tonart", selection: key) {
                        if AbcScore.key(of: model.form.abc) == nil {
                            Text("–").tag("")
                        }
                        ForEach(AbcScore.keyChoices, id: \.self) { key in
                            Text(AbcScore.keyLabel(key)).tag(key)
                        }
                    }
                    .fixedSize()
                    Spacer()
                    if let range = AbcScore.vocalRange(model.form.abc) {
                        Text("Gesang \(range.low)–\(range.high)").foregroundStyle(.secondary)
                    }
                }
            }
            HStack {
                Button("Beispiel einsetzen") { model.form.useScore(AbcScore.example) }
                Button("Aus MIDI-Datei …") { importing = true }
                Spacer()
                if hasScore {
                    Button("Partitur leeren", role: .destructive) { model.form.abc = "" }
                }
            }
            .buttonStyle(.borderless)
            if let message {
                Label(message, systemImage: failed ? "exclamationmark.triangle" : "info.circle")
                    .foregroundStyle(failed ? Color.red : Color.secondary)
            }
        } header: {
            Text("Partitur")
        } footer: {
            Text("Ersetzt die Planung durch eine eigene Partitur: Melodie, Akkorde, Tempo und Form stehen dann fest. Eine Tonart im anderen Tongeschlecht schreibt Terz, Sexte und Septime um.")
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.midi]) { result in
            guard case .success(let url) = result else { return }
            Task { await load(url) }
        }
    }

    private var hasScore: Bool {
        !model.form.abc.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// The score's key; picking another rewrites the score on the shorter way.
    private var key: Binding<String> {
        Binding(
            get: { AbcScore.key(of: model.form.abc) ?? "" },
            set: { target in
                guard !target.isEmpty else { return }
                model.form.abc = AbcScore.transpose(model.form.abc, to: target).abc
            })
    }

    private func load(_ url: URL) async {
        message = nil
        do {
            let score = try await model.scoreFromMidi(url)
            model.form.useScore(score.abc)
            failed = false
            message = score.warnings.isEmpty ? nil : score.warnings.joined(separator: " ")
        } catch {
            failed = true
            message = error.localizedDescription
        }
    }
}

extension AppModel {
    /// A MIDI file (typically a song edited in Logic) as YuE2's score, through `POST /api/midi/abc`. By hand rather
    /// than through the generated client, whose multipart types are more ceremony than one file needs.
    func scoreFromMidi(_ file: URL) async throws -> Components.Schemas.MidiScore {
        guard let server = tonwerk?.server else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        let scoped = file.startAccessingSecurityScopedResource()
        defer {
            if scoped {
                file.stopAccessingSecurityScopedResource()
            }
        }
        let data = try Data(contentsOf: file)
        let boundary = "Tonwerk-\(UUID().uuidString)"
        var request = URLRequest(url: server.appending(path: "api/midi/abc"))
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        var body = Data()
        let name = file.lastPathComponent.replacingOccurrences(of: "\"", with: "")
        body.append(Data(
            "--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"\(name)\"\r\nContent-Type: audio/midi\r\n\r\n"
                .utf8))
        body.append(data)
        body.append(Data("\r\n--\(boundary)--\r\n".utf8))
        let (answer, response) = try await URLSession.shared.upload(for: request, from: body)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else {
            let problem = try? JSONDecoder().decode(Components.Schemas.ProblemDetails.self, from: answer)
            throw ServerError(problem: problem?.detail)
        }
        return try JSONDecoder().decode(Components.Schemas.MidiScore.self, from: answer)
    }

    /// The score YuE2 wrote for a song (its score.abc), or nil.
    func ownScore(of song: Song) async -> String? {
        guard let server = tonwerk?.server,
              let answer = try? await URLSession.shared.data(
                  from: server.appending(path: "api/songs/\(song.run)/\(song.name)/score")),
              (answer.1 as? HTTPURLResponse)?.statusCode == 200
        else { return nil }
        return String(decoding: answer.0, as: UTF8.self)
    }

    /// Only the song's score into the form, the rest stays: "Als Partitur übernehmen".
    func useScoreInForm(_ song: Song) async {
        guard let score = await ownScore(of: song) else { return }
        form.useScore(score)
        saveForm()
        page = .create
    }
}
