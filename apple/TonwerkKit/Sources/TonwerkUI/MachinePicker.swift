import SwiftUI
import TonwerkKit

/// Which Mac a job runs on, offered only when the server has a helper besides itself: "Automatisch" (whichever is
/// free), this Mac, or a helper. A Mac that cannot be reached or lacks the engine is marked, not hidden, so a choice
/// kept from before stays visible.
struct MachinePicker: View {
    @Environment(AppModel.self) private var model
    @Binding var selection: String
    /// The server's engine name: lyrics, speech, images, ace, voices, stems, singing.
    let engine: String

    var body: some View {
        if model.helpersOffered {
            Picker("Rechner", selection: $selection) {
                Text("Automatisch").tag("")
                ForEach(ordered, id: \.id) { machine in
                    Text(label(machine)).tag(machine.id)
                }
                if !selection.isEmpty && !model.machines.contains(where: { $0.id == selection }) {
                    Text("\(selection) · nicht mehr da").tag(selection)
                }
            }
        }
    }

    /// This Mac first, then the helpers in the server's order.
    private var ordered: [Components.Schemas.MachineInfo] {
        model.machines.filter(\.local) + model.machines.filter { !$0.local }
    }

    private func label(_ machine: Components.Schemas.MachineInfo) -> String {
        var name = machine.name
        if !machine.reachable {
            name += String(localized: " · nicht erreichbar")
        } else if !machine.engines.contains(engine) {
            name += String(localized: " · ohne \(Self.engineName(engine))")
        }
        return name
    }

    static func engineName(_ engine: String) -> String {
        switch engine {
        case "lyrics": String(localized: "Textmodell")
        case "speech": String(localized: "Sprachmodelle")
        case "images": String(localized: "Bildmodell")
        case "ace": String(localized: "ACE-Step")
        case "voices": String(localized: "Stimmen")
        case "stems": String(localized: "Stem-Trennung")
        case "singing": String(localized: "Gesang")
        default: engine
        }
    }
}
