import CoreAudioKit
import CoreMIDI
import Observation
import SwiftUI

/// The MIDI outputs Core MIDI offers, the native counterpart of the web page's `midiPlayer.ts` and its Web MIDI
/// workarounds: no Firefox port names to guess, and Bluetooth instruments once paired. Listed anew whenever a device
/// comes or goes.
@MainActor
@Observable
final class MidiOutputs {
    struct Output: Identifiable, Hashable {
        let id: MIDIUniqueID
        let endpoint: MIDIEndpointRef
        /// What the device calls the port, which instruments are matched by.
        let name: String
        /// Core MIDI's longer name, device and port, as the Mac's Audio MIDI Setup shows it.
        let displayName: String
    }

    private(set) var outputs: [Output] = []
    private var client = MIDIClientRef()

    init() {
        let status = MIDIClientCreateWithBlock("Tonwerk" as CFString, &client) { [weak self] notification in
            guard notification.pointee.messageID == .msgSetupChanged else { return }
            Task { @MainActor in self?.refresh() }
        }
        if status != noErr {
            client = 0
        }
        refresh()
    }

    func refresh() {
        outputs = (0..<MIDIGetNumberOfDestinations()).compactMap { index in
            let endpoint = MIDIGetDestination(index)
            guard endpoint != 0 else { return nil }
            var id = MIDIUniqueID()
            MIDIObjectGetIntegerProperty(endpoint, kMIDIPropertyUniqueID, &id)
            let name = Self.string(endpoint, kMIDIPropertyName) ?? "MIDI"
            return Output(
                id: id, endpoint: endpoint, name: name,
                displayName: Self.string(endpoint, kMIDIPropertyDisplayName) ?? name)
        }
    }

    /// The output an instrument names. Web MIDI in Chrome passes the port's name, Firefox the display name, and the
    /// instruments were made in either, so both count.
    func output(named port: String) -> Output? {
        let wanted = port.trimmingCharacters(in: .whitespaces)
        return outputs.first { $0.name.trimmingCharacters(in: .whitespaces) == wanted }
            ?? outputs.first { $0.displayName.trimmingCharacters(in: .whitespaces) == wanted }
    }

    private static func string(_ object: MIDIObjectRef, _ property: CFString) -> String? {
        var value: Unmanaged<CFString>?
        guard MIDIObjectGetStringProperty(object, property, &value) == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }
}

// MARK: Bluetooth

#if os(iOS)
/// Apple's own dialog that finds and pairs Bluetooth MIDI instruments; a paired one appears among the outputs.
struct BluetoothMidiView: UIViewControllerRepresentable {
    func makeUIViewController(context: Context) -> UINavigationController {
        UINavigationController(rootViewController: CABTMIDICentralViewController())
    }

    func updateUIViewController(_ controller: UINavigationController, context: Context) {}
}
#else
enum BluetoothMidi {
    /// The Mac's window for Bluetooth MIDI instruments, the same as Audio MIDI Setup opens.
    @MainActor private static var window: CABTLEMIDIWindowController?

    @MainActor
    static func show() {
        let controller = window ?? CABTLEMIDIWindowController()
        window = controller
        controller.showWindow(nil)
        controller.window?.makeKeyAndOrderFront(nil)
    }
}
#endif
