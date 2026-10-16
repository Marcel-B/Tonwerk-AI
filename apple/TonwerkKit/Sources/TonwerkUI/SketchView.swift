import SwiftUI
import TonwerkKit
import UniformTypeIdentifiers

/// A written song sketch planned by Claude (`SketchPage.vue`): the sketch in, after a few minutes a whole score, and
/// from it a MIDI draft and the score to save and the song for the create form.
struct SketchView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let session = model.sketchPage {
                SketchSessionView(session: session)
            } else {
                ProgressView().onAppear { model.openSketch() }
            }
        }
        .navigationTitle("Ausarbeitung")
    }
}

private struct SketchSessionView: View {
    @Environment(AppModel.self) private var model
    @Bindable var session: SketchSession
    @State private var importing = false
    @State private var saving: URL?
    @State private var deleting: SketchDetail?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Eine schriftliche Ausarbeitung (Form, Tempo, Akkorde, Text, Melodieideen) wird von Claude zu einer vollständigen Partitur: jede Zeile mit Melodie, jeder Takt mit Akkorden. Daraus macht Tonwerk einen MIDI-Entwurf und einen YuE-Song.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let info = session.info {
                    if info.configured {
                        Text("Die Ausarbeitung geht dazu an Anthropic; das kostet je Plan einige Cent bis rund einen Euro (mit Fable gut das Doppelte) und dauert ein paar Minuten.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    } else {
                        Label("Es ist noch kein Anthropic-API-Schlüssel hinterlegt. Er gehört in ~/.config/tonwerk/anthropic.key (eine Zeile) oder in ANTHROPIC_API_KEY.", systemImage: "key")
                            .foregroundStyle(.orange)
                    }
                }
                if let problem = session.problem {
                    Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                }
                form
                list
                if let detail = session.selected {
                    detailView(detail)
                }
            }
            .padding()
            .frame(maxWidth: 860, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .task { await session.load() }
        .onDisappear { session.stop() }
        .refreshable { await session.refresh() }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.plainText, .text, UTType(filenameExtension: "md") ?? .text]) { result in
            guard case .success(let url) = result else { return }
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            if let text = try? String(contentsOf: url, encoding: .utf8) {
                session.text = text
            }
        }
        .fileMover(isPresented: Binding(get: { saving != nil }, set: { if !$0 { saving = nil } }), file: saving) { _ in }
        .confirmationDialog(
            "„\(deleting.map(title) ?? "")“ löschen? Der Plan ist dann weg.",
            isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }),
            titleVisibility: .visible
        ) {
            if let detail = deleting {
                Button("Löschen", role: .destructive) { Task { await session.delete(detail) } }
            }
            Button("Behalten", role: .cancel) {}
        }
    }

    private func title(_ detail: SketchDetail) -> String {
        let name = detail.job.plan?.title ?? detail.job.title ?? ""
        return name.isEmpty ? String(localized: "Ausarbeitung") : name
    }

    // MARK: Form

    private var form: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Ausarbeitung (Markdown oder Text)").font(.subheadline.weight(.semibold))
                Spacer()
                Button {
                    importing = true
                } label: {
                    Label("Datei laden", systemImage: "folder")
                }
                .buttonStyle(.borderless)
            }
            TextEditor(text: $session.text)
                .font(.callout.monospaced())
                .frame(minHeight: 180)
                .scrollContentBackground(.hidden)
                .padding(6)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            TextField("Wünsche für diesen Versuch (optional)", text: $session.wishes,
                      prompt: Text("z. B. Refrainmelodie höher, Strophe ruhiger"))
                .textFieldStyle(.roundedBorder)
            if let info = session.info, info.models.count > 1 {
                Picker("Claude-Modell", selection: $session.modelId) {
                    ForEach(info.models, id: \.id) { choice in
                        Text(choice.id == info.model ? "\(choice.name) (Standard)" : choice.name).tag(choice.id)
                    }
                }
            }
            HStack {
                Button {
                    Task { await session.send() }
                } label: {
                    Label("Mit Claude ausarbeiten", systemImage: "sparkles")
                }
                .buttonStyle(.borderedProminent)
                .disabled(session.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || session.sending
                          || session.info?.configured == false)
                if session.sending {
                    ProgressView().controlSize(.small)
                }
            }
        }
        .card(padding: 12)
    }

    // MARK: List

    private var list: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Ausarbeitungen").font(.headline)
            if session.sketches.isEmpty {
                Text("Noch keine Ausarbeitung.").foregroundStyle(.secondary)
            }
            ForEach(session.sketches, id: \.id) { sketch in
                Button {
                    Task { await session.show(sketch) }
                } label: {
                    HStack(spacing: 10) {
                        Text(sketch.title.isEmpty ? String(localized: "Ausarbeitung") : sketch.title)
                            .fontWeight(.semibold)
                            .lineLimit(1)
                        Spacer()
                        Text(SketchSession.summary(bars: sketch.bars.map(Int.init), seconds: sketch.seconds)
                             ?? sketch.createdAt.formatted(date: .numeric, time: .shortened))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        stageTag(sketch.stage)
                    }
                    .padding(.horizontal, 10)
                    .padding(.vertical, 8)
                    .contentShape(Rectangle())
                    .overlay(
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .strokeBorder(session.selected?.job.id == sketch.id ? Theme.accent : Color.secondary.opacity(0.3)))
                }
                .buttonStyle(.plain)
            }
        }
    }

    private func stageTag(_ stage: String) -> some View {
        Text(SketchSession.stageName(stage))
            .font(.caption.weight(.semibold))
            .foregroundStyle(stage == "done" ? Color.green : stage == "failed" ? Color.red : Color.primary)
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(.quaternary, in: Capsule())
    }

    // MARK: Detail

    private func detailView(_ detail: SketchDetail) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title(detail)).font(.title3.weight(.semibold))
                if let plan = detail.job.plan {
                    let frame = "\(plan.tempo.formatted()) BPM · \(plan.key) · \(plan.beatsPerBar)/4"
                    Text([frame, SketchSession.summary(bars: detail.bars, seconds: detail.seconds)]
                        .compactMap { $0 }.joined(separator: " · "))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            if detail.job.stage == "failed" {
                Label(detail.job.message ?? String(localized: "Fehlgeschlagen"), systemImage: "xmark.octagon")
                    .foregroundStyle(.red)
            } else if detail.job.finished != true {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text(SketchSession.stageName(detail.job.stage ?? "queued")).foregroundStyle(.secondary)
                }
            }

            if let plan = detail.job.plan, detail.request != nil {
                FlowLayout {
                    Button {
                        session.useSong(detail)
                    } label: {
                        Label("Als YuE-Song ins Formular", systemImage: "sparkles")
                    }
                    .buttonStyle(.borderedProminent)
                    Button {
                        Task { await save(detail, midi: true) }
                    } label: {
                        Label("MIDI-Entwurf", systemImage: "arrow.down.circle")
                    }
                    .buttonStyle(.bordered)
                    Button {
                        Task { await save(detail, midi: false) }
                    } label: {
                        Label("Partitur (.abc)", systemImage: "doc")
                    }
                    .buttonStyle(.bordered)
                }

                section("Stil") { Text(plan.style) }
                if !plan.notes.isEmpty {
                    section("Was Claude entschieden hat") {
                        ForEach(Array(plan.notes.enumerated()), id: \.offset) { _, note in
                            Text("• \(note)")
                        }
                    }
                }
                if let warnings = detail.warnings, !warnings.isEmpty {
                    section("Hinweise") {
                        ForEach(Array(warnings.enumerated()), id: \.offset) { _, warning in
                            Text("• \(SketchSession.warning(warning))")
                                .font(.callout)
                                .foregroundStyle(.secondary)
                        }
                    }
                }
                section("Ablauf") {
                    ForEach(Array(plan.sections.enumerated()), id: \.offset) { _, part in
                        VStack(alignment: .leading, spacing: 4) {
                            HStack(alignment: .firstTextBaseline) {
                                Text(part.name).fontWeight(.semibold)
                                Spacer()
                                Text("\(part.bars.count) · \(part.parts.joined(separator: ", "))")
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
                            }
                            Text(SketchSession.chordLine(part))
                                .font(.caption.monospaced())
                            if !part.lyrics.isEmpty {
                                Text(part.lyrics.joined(separator: "\n"))
                                    .font(.callout)
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .padding(10)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                    }
                }
            }

            HStack {
                if detail.job.finished == true {
                    Button {
                        Task {
                            await session.send(
                                detail.job.source ?? "", instruction: detail.job.instruction ?? "", with: detail.job.model)
                        }
                    } label: {
                        Label("Noch einmal ausarbeiten", systemImage: "arrow.clockwise")
                    }
                    .disabled(session.info?.configured == false)
                }
                Button(role: .destructive) {
                    deleting = detail
                } label: {
                    Label("Löschen", systemImage: "trash")
                }
            }
            .buttonStyle(.borderless)
            if let input = detail.job.inputTokens, let output = detail.job.outputTokens {
                Text("\(detail.job.model ?? "") · \(input) Tokens hinein, \(output) heraus")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
        .card(padding: 12)
    }

    private func section<Content: View>(_ heading: LocalizedStringKey, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(heading).font(.subheadline.weight(.semibold))
            content()
        }
    }

    private func save(_ detail: SketchDetail, midi: Bool) async {
        do {
            saving = try await model.downloadSketch(detail.job, midi: midi)
        } catch {
            session.problem = error.localizedDescription
        }
    }
}
