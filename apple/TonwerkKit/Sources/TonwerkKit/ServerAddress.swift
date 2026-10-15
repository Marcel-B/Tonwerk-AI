import Foundation

/// The address typed once on first start. Nothing is built into the apps: the server is the user's own Mac, reached
/// through Tailscale (`mac-mini.tailnet.ts.net:8443`), and whoever is not in that tailnet cannot reach it at all.
public enum ServerAddress {
    /// The server's root from what was typed or pasted: a bare host gets `https://`, and the web page's address
    /// (`…/ui/#/songs/…`) leads to the same server, since the API lives at its root.
    public static func parse(_ text: String) -> URL? {
        var trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        if !trimmed.contains("://") {
            trimmed = "https://" + trimmed
        }
        guard var components = URLComponents(string: trimmed),
              let scheme = components.scheme?.lowercased(), scheme == "https" || scheme == "http",
              let host = components.host, !host.isEmpty
        else { return nil }
        components.scheme = scheme
        components.user = nil
        components.password = nil
        components.path = ""
        components.query = nil
        components.fragment = nil
        return components.url
    }
}
