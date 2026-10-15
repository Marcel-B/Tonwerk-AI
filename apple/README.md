# Tonwerk für Mac und iPhone

Native Apps, die die Weboberfläche nach und nach ersetzen. Der Server bleibt unverändert; die Apps sprechen seine API
(`/api/openapi`) über Tailscale.

## Starten (Mac)

1. `apple/Tonwerk.xcodeproj` in Xcode öffnen.
2. Beim ersten Bauen fragt Xcode nach dem Plugin „OpenAPIGenerator“: **Trust & Enable**. Es erzeugt den API-Client.
3. Oben als Ziel **My Mac** wählen, dann ⌘R.
4. Beim ersten Start die Adresse eintragen, wie im Browser: `mac-mini-von-marcel.tail6105c0.ts.net:8443`
   (auf dem Mac mini selbst geht auch `http://127.0.0.1:5090`).

Der Mac-Build wird lokal ad hoc signiert und braucht kein Apple-Konto.

## iPhone

Braucht ein Team (für mehr als 7 Tage Laufzeit, TestFlight, Push und CarPlay das Apple Developer Program). Das Repo
ist öffentlich, deshalb kommt die Team-ID nicht ins Projekt, sondern in `Support/Local.xcconfig` (von git ignoriert):

```
DEVELOPMENT_TEAM = ABCDE12345
```

Die ID steht unter developer.apple.com → Account → Membership. In *Signing & Capabilities* das Team leer lassen, sonst
schreibt Xcode es in `project.pbxproj`. Ziel auf das iPhone stellen, ⌘R.

## Aufbau

- `Tonwerk/` – nur der Einstieg der App.
- `TonwerkKit/` – Swift-Paket: der generierte Client (`TonwerkAPI`), Adresse, Daten, Ereignisstrom (`TonwerkKit`)
  und alle Seiten samt Player (`TonwerkUI`). `swift build` und `swift test` dort.
- `Support/` – Info.plist-Ergänzungen und die Mac-Sandbox.

Stand 0.1: Bibliothek mit Suche und Sternen, „Zum Anhören“ (unbewertet, älter als eine Woche), Playlists, Warteschlange
live, Player mit Sperrbildschirm, Mediatasten und AirPods. Noch nicht: Erstellen, Werkzeuge, Downloads, Push.
