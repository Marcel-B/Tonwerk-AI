import SwiftUI
import TonwerkKit

/// The writing page (`WritingPage.vue`): a conversation with a model in LM Studio, for ideas, rhymes, variants and
/// lyrics. Gemma 4 31B writes well and slowly, Qwen 3.6 fast; the model stays loaded a few minutes between answers.
struct WritingView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let session = model.writingPage {
                WritingSessionView(session: session)
            } else {
                ProgressView().onAppear { model.openWriting() }
            }
        }
        .navigationTitle("Schreiben")
    }
}

private struct WritingSessionView: View {
    @Environment(AppModel.self) private var model
    @Bindable var session: WritingSession
    @State private var renaming = false
    @State private var newTitle = ""
    @State private var deleting = false
    @FocusState private var typing: Bool

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            conversation
            Divider()
            input
        }
        .task { await session.load() }
        .refreshable { await session.refresh() }
        .onChange(of: model.liveWriting?.stage) { session.replyChanged() }
        .alert("Gespräch umbenennen", isPresented: $renaming) {
            TextField("Titel", text: $newTitle)
            Button("Umbenennen") { Task { await session.rename(newTitle) } }
            Button("Abbrechen", role: .cancel) {}
        }
        .confirmationDialog("Dieses Gespräch löschen?", isPresented: $deleting, titleVisibility: .visible) {
            Button("Löschen", role: .destructive) { Task { await session.delete() } }
            Button("Behalten", role: .cancel) {}
        }
    }

    // MARK: Header

    private var header: some View {
        HStack(spacing: 10) {
            Menu {
                Button {
                    Task { await session.newChat() }
                } label: {
                    Label("Neues Gespräch", systemImage: "plus")
                }
                if let chats = session.overview?.chats, !chats.isEmpty {
                    Divider()
                    ForEach(chats, id: \.id) { chat in
                        Button(chat.title.isEmpty ? String(localized: "Ohne Titel") : chat.title) {
                            Task { await session.open(chat.id) }
                        }
                    }
                }
            } label: {
                Label(title, systemImage: "text.bubble")
                    .lineLimit(1)
            }
            .fixedSize(horizontal: false, vertical: true)
            Spacer()
            if !session.models.isEmpty {
                Picker("Modell", selection: $session.modelId) {
                    ForEach(session.models, id: \.id) { choice in
                        Text(choice.roleNote.map { "\(choice.name) · \($0)" } ?? choice.name).tag(choice.id)
                    }
                }
                .labelsHidden()
                .frame(maxWidth: 280)
            }
            Menu {
                if session.chat != nil {
                    Button {
                        newTitle = session.chat?.title ?? ""
                        renaming = true
                    } label: {
                        Label("Umbenennen", systemImage: "pencil")
                    }
                    Button(role: .destructive) {
                        deleting = true
                    } label: {
                        Label("Löschen", systemImage: "trash")
                    }
                }
                if let loaded = session.overview?.loaded {
                    Button {
                        Task { await session.release() }
                    } label: {
                        Label("\(loaded) entladen", systemImage: "memorychip")
                    }
                }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .disabled(session.chat == nil && session.overview?.loaded == nil)
        }
        .padding(.horizontal)
        .padding(.vertical, 8)
    }

    private var title: String {
        let name = session.chat?.title ?? ""
        return name.isEmpty ? String(localized: "Gespräch") : name
    }

    // MARK: Conversation

    private var conversation: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 12) {
                    if session.messages.isEmpty && session.reply == nil {
                        Text("Schreib mit dem Modell: Ideen, Titel, Bilder, Reime, Varianten einer Zeile oder ein ganzer Songtext. Gemma 4 31B schreibt langsam und gut, auch Deutsch; Qwen 3.6 ist schnell für Ideen und Reime. Solange das Modell geladen ist, warten Songs; nach drei Minuten ohne Nachricht gibt es den Speicher zurück.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                            .padding(.top, 8)
                    }
                    ForEach(Array(session.messages.enumerated()), id: \.offset) { index, message in
                        bubble(message, last: index == session.messages.count - 1)
                    }
                    if let reply = session.reply, reply.finished != true || reply.stage == "failed" {
                        pending(reply)
                    }
                    if let problem = session.problem {
                        Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                    }
                    Color.clear.frame(height: 1).id("end")
                }
                .padding()
                .frame(maxWidth: 860, alignment: .leading)
                .frame(maxWidth: .infinity)
            }
            .onChange(of: session.messages.count) { proxy.scrollTo("end", anchor: .bottom) }
            .onChange(of: session.reply?.text) { proxy.scrollTo("end", anchor: .bottom) }
        }
    }

    @ViewBuilder
    private func bubble(_ message: Components.Schemas.WritingMessage, last: Bool) -> some View {
        let mine = message.role == "user"
        VStack(alignment: mine ? .trailing : .leading, spacing: 4) {
            Text(message.text)
                .textSelection(.enabled)
                .padding(10)
                .background(
                    mine ? AnyShapeStyle(Theme.accent.opacity(0.18)) : AnyShapeStyle(.quaternary.opacity(0.5)),
                    in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            if !mine {
                HStack(spacing: 14) {
                    if let used = message.model {
                        Text(message.stopped == true ? "\(used) · angehalten" : used)
                    }
                    Button {
                        _ = Clipboard.copy(message.text)
                    } label: {
                        Label("Kopieren", systemImage: "doc.on.doc")
                    }
                    Button {
                        model.useAsLyrics(message.text)
                    } label: {
                        Label("Als Songtext", systemImage: "text.badge.plus")
                    }
                    if last && !session.replying {
                        Button {
                            Task { await session.retry() }
                        } label: {
                            Label("Noch einmal", systemImage: "arrow.clockwise")
                        }
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .buttonStyle(.borderless)
            }
        }
        .frame(maxWidth: .infinity, alignment: mine ? .trailing : .leading)
    }

    @ViewBuilder
    private func pending(_ reply: Components.Schemas.WritingReply) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            if let text = reply.text, !text.isEmpty {
                Text(text)
                    .textSelection(.enabled)
                    .padding(10)
                    .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            }
            HStack(spacing: 8) {
                if reply.finished != true {
                    ProgressView().controlSize(.small)
                }
                Text(WritingSession.stageName(reply))
                    .foregroundStyle(reply.stage == "failed" ? Color.red : Color.secondary)
            }
            .font(.callout)
        }
    }

    // MARK: Input

    private var input: some View {
        VStack(alignment: .leading, spacing: 8) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(WritingSession.starters) { starter in
                        Button(starter.label) {
                            session.text = starter.text + session.text
                            typing = true
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.small)
                    }
                }
            }
            HStack(alignment: .bottom, spacing: 8) {
                TextField("Nachricht", text: $session.text, axis: .vertical)
                    .lineLimit(1...8)
                    .textFieldStyle(.roundedBorder)
                    .focused($typing)
                if session.reply?.finished == false {
                    Button {
                        Task { await session.stop() }
                    } label: {
                        Image(systemName: "stop.circle.fill").font(.title2)
                    }
                    .buttonStyle(.borderless)
                    .help("Anhalten")
                } else {
                    Button {
                        Task { await session.send() }
                    } label: {
                        Image(systemName: "arrow.up.circle.fill").font(.title2)
                    }
                    .buttonStyle(.borderless)
                    .disabled(session.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || session.sending || session.replying)
                    .keyboardShortcut(.return, modifiers: .command)
                    .help("Senden")
                }
            }
        }
        .frame(maxWidth: 860)
        .padding(.horizontal)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity)
        .background(.bar)
    }
}
