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

## TestFlight (nur für sich selbst)

Voraussetzung ist das Apple Developer Program. Einmal in App Store Connect unter *Apps → +* eine iOS-App mit der
Bundle-ID `de.bvelop.tonwerk.ai` anlegen (Name z. B. „Tonwerk-AI“, die Bundle-ID erscheint dort, sobald Xcode sie
einmal mit dem Team signiert hat, sonst unter developer.apple.com → Identifiers anlegen).

1. In Xcode als Ziel **Any iOS Device (arm64)** wählen, *Product → Archive*.
2. Im Organizer *Distribute App → TestFlight Internal Only*, signieren lässt man Xcode automatisch.
3. Nach der Verarbeitung (einige Minuten, Mail von Apple) in App Store Connect unter *TestFlight* sich selbst als
   internen Tester eintragen und die App über die TestFlight-App aufs iPhone holen. Interne Builds brauchen keine
   Beta-Prüfung und laufen 90 Tage.

Ohne Mac geht es über GitHub: *Actions → TestFlight → Run workflow* archiviert und lädt hoch
(`.github/workflows/testflight.yml`, nur iPhone/iPad). Dafür braucht das Repo dieselben vier Secrets wie Marcel-B/Synth:
`APPLE_TEAM_ID`, `ASC_KEY_ID`, `ASC_ISSUER_ID`, `ASC_KEY_P8`. Die Build-Nummer ist dort 1000 plus die Laufnummer.

Jeder weitere Upload von Hand braucht eine höhere Build-Nummer (`CURRENT_PROJECT_VERSION`, in Xcode *Build* unter General).
Verschlüsselung (`ITSAppUsesNonExemptEncryption = NO`, nur HTTPS) und das Privacy-Manifest (`Tonwerk/PrivacyInfo.xcprivacy`)
sind gesetzt, App Store Connect fragt danach nicht mehr. Der Mac-Build signiert ad hoc und geht so nicht über TestFlight;
er bleibt ein lokaler Build.

## Aufbau

- `Tonwerk/` – nur der Einstieg der App.
- `TonwerkKit/` – Swift-Paket: der generierte Client (`TonwerkAPI`), Adresse, Daten, Ereignisstrom (`TonwerkKit`)
  und alle Seiten samt Player (`TonwerkUI`). `swift build` und `swift test` dort.
- `Support/` – Info.plist-Ergänzungen und die Mac-Sandbox.

Stand 0.1: Bibliothek mit Suche und Sternen, „Zum Anhören“ (unbewertet, älter als eine Woche), Playlists, Warteschlange
live, Player mit Sperrbildschirm, Mediatasten und AirPods. Noch nicht: Erstellen, Werkzeuge, Downloads, Push.
