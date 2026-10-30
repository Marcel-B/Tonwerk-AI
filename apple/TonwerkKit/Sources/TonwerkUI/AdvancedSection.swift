import SwiftUI
import TonwerkKit

/// The parameters beside the song's own fields, as the web page's `AdvancedParameters.vue`. Collapsed at first, since
/// a normal song needs none of them; the switch sends them or the defaults, keeping the values either way.
struct AdvancedSection: View {
    @Environment(AppModel.self) private var model
    @AppStorage("advancedOpen") private var open = false
    @State private var loras: Components.Schemas.LoraList?

    var body: some View {
        @Bindable var model = model
        Section {
            DisclosureGroup(isExpanded: $open) {
                VStack(alignment: .leading, spacing: 12) {
                    Toggle("Erweiterte Parameter verwenden", isOn: $model.form.useAdvanced)
                    if !model.form.useAdvanced {
                        Text("Ausgeschaltet: Der Song wird mit den Standardwerten erzeugt, die Werte hier bleiben stehen.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Group {
                        planning
                        steps
                        engines
                        length
                        lora
                        SamplingFields(
                            title: "Sampling: Song",
                            intro: "Wie die Song-Tokens gezogen werden, also Klang, Arrangement und Gesang. Ein Auftrag mit geändertem Sampling wird nicht mit anderen zusammen komponiert.",
                            sampling: $model.form.semanticSampling,
                            defaults: .semanticDefaults)
                        SamplingFields(
                            title: "Sampling: Partitur",
                            intro: plansScore
                                ? "Wie die Partitur mit Melodie und Akkorden geschrieben wird."
                                : "Gerade ohne Wirkung: Ohne Planung oder mit eigener Partitur schreibt YuE2 keine Partitur.",
                            sampling: $model.form.abcSampling,
                            defaults: .abcDefaults)
                    }
                    .disabled(!model.form.useAdvanced)
                    Button(model.form.advancedChanged ? "Auf Standardwerte zurücksetzen" : "Alles auf Standardwerten") {
                        model.form = model.form.resettingAdvanced()
                    }
                    .disabled(!model.form.advancedChanged)
                }
                .padding(.top, 4)
            } label: {
                HStack {
                    Label {
                        Text("Erweiterte Parameter")
                    } icon: {
                        Image(systemName: "slider.horizontal.3").foregroundStyle(Theme.accent)
                    }
                    if !model.form.useAdvanced {
                        Text("aus").foregroundStyle(.secondary)
                    } else if model.form.advancedChanged {
                        Text("angepasst").foregroundStyle(.orange)
                    }
                }
            }
        }
        .task(id: open) {
            // Read each time the section opens, so a LoRA trained meanwhile shows up.
            if open {
                loras = await model.loras()
            }
        }
    }

    // The worker writes a score unless planning is off (an instrumental turns it back on) or one is supplied.
    private var plansScore: Bool {
        (model.form.planning != .off || model.form.instrumental) && !model.form.usesScore
    }

    private var planning: some View {
        @Bindable var model = model
        return LabeledField(hint: "Ob YuE2 vor dem Audio eine Partitur schreibt, an der sich der Song orientiert.") {
            Picker("Planung", selection: $model.form.planning) {
                Text("Melodie und Akkorde (Standard)").tag(CreateForm.Planning.full)
                Text("Nur Melodie").tag(CreateForm.Planning.melody)
                Text("Keine").tag(CreateForm.Planning.off)
            }
        }
    }

    /// One field for the steps of the chosen quality; each keeps its own value.
    private var steps: some View {
        @Bindable var model = model
        let full = model.form.full
        return LabeledField(
            hint: full
                ? "Solver-Schritte der Klangsynthese bei voller Qualität, 1–64 (Standard 32)."
                : "Solver-Schritte der Klangsynthese eines Entwurfs, 1–32 (Standard 8)."
        ) {
            if full {
                Stepper(value: $model.form.fullSteps, in: 1...64) {
                    LabeledContent("Schritte (Voll)", value: "\(model.form.fullSteps)")
                }
            } else {
                Stepper(value: $model.form.draftSteps, in: 1...32) {
                    LabeledContent("Schritte (Entwurf)", value: "\(model.form.draftSteps)")
                }
            }
        }
    }

    private var engines: some View {
        @Bindable var model = model
        return LabeledField(hint: "Wo die Klangsynthese läuft. Automatisch: Entwurf auf der GPU, voll auf der Neural Engine.") {
            Picker("Rechenwerke", selection: $model.form.engines) {
                Text("Automatisch (Standard)").tag("")
                Text("Nur GPU").tag("gpu")
                Text("GPU und Neural Engine").tag("gpu+ane")
            }
        }
    }

    private var length: some View {
        @Bindable var model = model
        return LabeledField(hint: "Obergrenze für die Songlänge; sonst endet der Song, wenn der Text gesungen ist. Über 6:00 ist Neuland für das Modell.") {
            Picker("Maximale Länge", selection: $model.form.maxSeconds) {
                ForEach(CreateForm.lengthChoices, id: \.self) { seconds in
                    let clock = Song.clock(Double(seconds))
                    Text(seconds == CreateForm.maxSongSeconds ? "\(clock) (Standard)" : "\(clock)").tag(seconds)
                }
            }
        }
    }

    private var lora: some View {
        @Bindable var model = model
        let available = loras?.loras ?? []
        let chosen = available.first { $0.name == model.form.lora }
        return LabeledField(hint: loraHint(chosen, available: available)) {
            VStack(alignment: .leading) {
                Picker("Klang-LoRA", selection: $model.form.lora) {
                    Text("keine (Standard)").tag("")
                    ForEach(available, id: \.name) { lora in
                        Text(lora.name).tag(lora.name)
                    }
                    // A LoRA taken over from a song, or deleted since, stays visible.
                    if !model.form.lora.isEmpty && chosen == nil {
                        Text("\(model.form.lora) (nicht mehr da)").tag(model.form.lora)
                    }
                }
                if !model.form.lora.isEmpty {
                    HStack {
                        Text("Stärke")
                        Slider(value: $model.form.loraStrength, in: 0...3, step: 0.05)
                        Text(model.form.loraStrength, format: .number.precision(.fractionLength(2)))
                            .monospacedDigit()
                    }
                }
            }
        }
    }

    private func loraHint(_ chosen: Components.Schemas.LoraInfo?, available: [Components.Schemas.LoraInfo]) -> String {
        guard let chosen else {
            return loras != nil && available.isEmpty
                ? String(localized: "Noch keine LoRA da. deploy/train-lora.sh trainiert eine aus eigenen Songs.")
                : String(localized: "Eine selbst trainierte Klangfarbe. Sie ändert Instrumente und Mix, nicht die Komposition.")
        }
        var parts: [String] = []
        if let word = chosen.triggerWord {
            parts.append(String(localized: "Triggerwort „\(word)“: gehört in den Stil."))
        }
        if let songs = chosen.songs, let minutes = chosen.minutes {
            parts.append(String(localized: "Trainiert auf \(songs) Songs (\(Int(minutes.rounded())) min)."))
        }
        return parts.isEmpty ? String(localized: "Stärke 1 wirkt wie trainiert, ab etwa 2 kann der Klang verzerren.") : parts.joined(separator: " ")
    }
}

/// A control with its hint below.
private struct LabeledField<Content: View>: View {
    let hint: LocalizedStringKey
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            content
            Text(hint)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

/// One of the model's two sampling settings, with the model's value beside each field.
private struct SamplingFields: View {
    let title: LocalizedStringKey
    let intro: LocalizedStringKey
    @Binding var sampling: CreateForm.Sampling
    let defaults: CreateForm.Sampling

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(title).font(.headline)
                Spacer()
                Button("Zurücksetzen") { sampling = defaults }
                    .buttonStyle(.borderless)
                    .disabled(sampling == defaults)
            }
            Text(intro)
                .font(.caption)
                .foregroundStyle(.secondary)
            field("Temperatur", value: $sampling.temperature, standard: defaults.temperature)
            field("top_p", value: $sampling.topP, standard: defaults.topP)
            field("top_k", value: $sampling.topK, standard: defaults.topK)
            field("Wiederholungsstrafe", value: $sampling.repetitionPenalty, standard: defaults.repetitionPenalty)
            field("Strafenfenster", value: $sampling.penaltyWindow, standard: defaults.penaltyWindow)
        }
    }

    private func field(_ label: LocalizedStringKey, value: Binding<Double>, standard: Double) -> some View {
        LabeledContent {
            HStack {
                TextField(label, value: value, format: .number)
                    .labelsHidden()
                    .multilineTextAlignment(.trailing)
                    .frame(maxWidth: 90)
                Text("Standard \(standard.formatted())")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .frame(minWidth: 90, alignment: .leading)
            }
        } label: {
            Text(label)
        }
    }

    private func field(_ label: LocalizedStringKey, value: Binding<Int>, standard: Int) -> some View {
        LabeledContent {
            HStack {
                TextField(label, value: value, format: .number)
                    .labelsHidden()
                    .multilineTextAlignment(.trailing)
                    .frame(maxWidth: 90)
                Text("Standard \(standard)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .frame(minWidth: 90, alignment: .leading)
            }
        } label: {
            Text(label)
        }
    }
}

extension AppModel {
    /// The LoRAs in the server's folder, or nil when it does not answer.
    func loras() async -> Components.Schemas.LoraList? {
        try? await tonwerk?.api.listLoras().ok.body.json
    }
}
