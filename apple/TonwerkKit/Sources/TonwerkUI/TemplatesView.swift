import SwiftUI
import TonwerkKit

/// The top of the create page (`TemplatePicker.vue`): a template fills the form, the bookmark saves the form's
/// style and parameters under a name.
struct TemplatePicker: View {
    @Environment(AppModel.self) private var model
    @State private var saving = false
    @State private var notice: String?

    var body: some View {
        let store = model.templateStore
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Menu {
                    if store.templates.isEmpty {
                        Text("Noch keine Vorlagen")
                    }
                    ForEach(store.templates) { template in
                        Button(template.name) {
                            store.apply(template)
                            notice = String(localized: "Vorlage „\(template.name)“ geladen: \(Self.partList(template.settings.parts)).")
                        }
                    }
                    Divider()
                    Button("Alle Vorlagen") { model.page = .templates }
                } label: {
                    Label("Vorlage laden", systemImage: "bookmark")
                }
                Spacer()
                Button {
                    saving = true
                } label: {
                    Label("Als Vorlage speichern", systemImage: "bookmark.fill")
                }
                .labelStyle(.iconOnly)
                .buttonStyle(.borderless)
                .help("Als Vorlage speichern")
            }
            if let notice {
                Text(notice).font(.caption).foregroundStyle(.secondary)
            }
        }
        .task { await store.load() }
        .sheet(isPresented: $saving) {
            SaveTemplateSheet { saved, overwritten in
                notice = overwritten
                    ? String(localized: "Die Vorlage „\(saved.name)“ ist überschrieben.")
                    : String(localized: "Die Vorlage „\(saved.name)“ ist gespeichert.")
            }
        }
    }

    static func partList(_ parts: [TemplateSettings.Part]) -> String {
        parts.map(\.name).joined(separator: ", ")
    }
}

/// Saving the form as a template: a name (the one last loaded is offered, to overwrite it) and the groups to keep.
private struct SaveTemplateSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let saved: (PromptTemplate, Bool) -> Void
    @State private var name = ""
    @State private var parts: Set<TemplateSettings.Part> = [.style, .parameters]
    @State private var busy = false
    @State private var problem: String?

    var body: some View {
        let store = model.templateStore
        let existing = store.named(name)
        NavigationStack {
            Form {
                TextField("Name der Vorlage", text: $name)
                if let existing {
                    Text("Ersetzt die Vorlage „\(existing.name)“.").font(.caption).foregroundStyle(.secondary)
                }
                Section("Was sie enthält") {
                    ForEach([TemplateSettings.Part.style, .seed, .parameters]) { part in
                        Toggle(part.name, isOn: Binding(
                            get: { parts.contains(part) },
                            set: { if $0 { parts.insert(part) } else { parts.remove(part) } }))
                    }
                    Text("Titel, Songtext und Partitur bleiben draußen.").font(.caption).foregroundStyle(.secondary)
                }
                if let problem {
                    Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Als Vorlage speichern")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Abbrechen") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(existing == nil ? "Speichern" : "Überschreiben") { save(overwriting: existing != nil) }
                        .disabled(busy || parts.isEmpty || name.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
        .frame(minWidth: 340, minHeight: 320)
        .onAppear { name = store.lastLoaded?.name ?? "" }
    }

    private func save(overwriting: Bool) {
        busy = true
        Task {
            defer { busy = false }
            do {
                let template = try await model.templateStore.save(name: name, parts: parts)
                saved(template, overwriting)
                dismiss()
            } catch {
                problem = error.localizedDescription
            }
        }
    }
}

/// The templates page (`TemplatesPage.vue`): what each holds, loading, renaming and deleting.
struct TemplatesView: View {
    @Environment(AppModel.self) private var model
    @State private var renaming: PromptTemplate?
    @State private var newName = ""
    @State private var deleting: PromptTemplate?
    @State private var problem: String?

    var body: some View {
        let store = model.templateStore
        List {
            Section {
                Text("Stil und Parameter von Songs, die gut geworden sind, unter einem Namen. Unter „Erstellen“ füllt eine Vorlage das Formular; Titel, Songtext und Partitur bleiben, wie sie sind.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let problem = problem ?? store.problem {
                    Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                } else if store.loaded && store.templates.isEmpty {
                    Text("Noch keine Vorlagen. Unter „Erstellen“ speichert das Lesezeichen neben „Vorlage laden“ Stil und Parameter des Formulars.")
                        .foregroundStyle(.secondary)
                }
            }
            ForEach(store.templates) { template in
                row(template)
            }
        }
        .navigationTitle("Vorlagen")
        .task { await store.load() }
        .refreshable { await store.load() }
        .alert("Vorlage umbenennen", isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })) {
            TextField("Name der Vorlage", text: $newName)
            Button("Abbrechen", role: .cancel) {}
            Button("Umbenennen") {
                guard let template = renaming else { return }
                Task {
                    do {
                        try await store.rename(template, to: newName)
                        problem = nil
                    } catch {
                        problem = error.localizedDescription
                    }
                }
            }
            .disabled(newName.trimmingCharacters(in: .whitespaces).isEmpty)
        }
        .confirmationDialog(
            deleting.map { String(localized: "Die Vorlage „\($0.name)“ löschen? Songs, die damit gemacht wurden, bleiben.") } ?? "",
            isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }),
            titleVisibility: .visible
        ) {
            Button("Löschen", role: .destructive) {
                guard let template = deleting else { return }
                Task {
                    do {
                        try await store.delete(template)
                        problem = nil
                    } catch {
                        problem = error.localizedDescription
                    }
                }
            }
            Button("Behalten", role: .cancel) {}
        }
    }

    private func row(_ template: PromptTemplate) -> some View {
        HStack(alignment: .top, spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                Text(template.name).fontWeight(.medium)
                Text(TemplatePicker.partList(template.settings.parts)).font(.caption).foregroundStyle(.secondary)
                if let style = template.settings.style, !style.isEmpty {
                    Text(style).font(.callout).lineLimit(3)
                }
                let details = template.settings.details
                if !details.isEmpty {
                    Text(details.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
                }
                if let date = try? TonwerkDates().decode(template.updatedAt) {
                    Text(date.formatted(date: .abbreviated, time: .shortened)).font(.caption2).foregroundStyle(.tertiary)
                }
            }
            Spacer()
            Button("Laden") {
                model.templateStore.apply(template)
                model.page = .create
            }
            .buttonStyle(.bordered)
            Menu {
                Button {
                    newName = template.name
                    renaming = template
                } label: {
                    Label("Umbenennen", systemImage: "pencil")
                }
                Button(role: .destructive) {
                    deleting = template
                } label: {
                    Label("Löschen", systemImage: "trash")
                }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuStyle(.button)
            .buttonStyle(.borderless)
            .menuIndicator(.hidden)
            .fixedSize()
        }
        .padding(.vertical, 2)
    }
}
