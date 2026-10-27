# Tonwerk-AI

Eine Web-Oberfläche für [YuE Studio](https://github.com/multimodal-art-projection/YuE), die sich vom Handy oder Laptop aus bedienen lässt, zum Beispiel über Tailscale. Songs erzeugen, den Fortschritt live verfolgen, Entwürfe in voller Qualität rendern, anhören und herunterladen.

Tonwerk bringt kein eigenes Modell mit. Es startet den Worker von YuE Studio (`src/tools/yue2_worker.py`) mit der Python-Umgebung, die die App installiert hat, und spricht ihn über sein JSON-Zeilen-Protokoll auf stdin/stdout an. Die Songs landen deshalb im selben Ordner wie die der App (`~/Music/YuE Studio`), und beide sehen dieselbe Bibliothek.

Tonwerk-AI hieß früher Tonwerk und davor YuE UI; „Tonwerk“ allein ist die App mit den Plugins (Repository Synth). Im Text unten steht oft noch Tonwerk. Repository, Projektnamen (`YueUI.Api`), der LaunchAgent `de.bvelop.yueui`, der Datenordner `~/Library/Application Support/YuE UI` und `yueui.db` tragen weiter den alten Namen, damit Deploy, Auto-Deploy und die vorhandenen Daten weiterlaufen.

Davor sitzt eine kleine Erweiterung aus diesem Repository (`src/YueUI.Api/Worker/yueui_worker.py`). Sie lädt den Worker als Python-Modul und ergänzt das `generate`-Kommando um Parameter, die YuE Studio selbst nicht anbietet: Sampling für Partitur und Song (temperature, top_p, top_k, repetition_penalty, penalty_window), die Schrittzahl bei voller Qualität und Songs bis 10 Minuten statt 6. Sie greift dafür an einigen Stellen in den Worker ein und prüft beim Start, ob die noch so aussehen wie erwartet. Hat ein Update von YuE Studio den Worker verändert, läuft er unverändert weiter. Die Oberfläche weist dann darauf hin, dass diese Parameter keine Wirkung haben, der Grund steht im Protokoll. `cfg_scale` fehlt bewusst: Der Worker komponiert Songs in Batches, und die Batch-Decoder von YuE2 können keine CFG.

```
Browser ──(Tailscale, HTTPS)──▶ tailscale serve ──▶ YueUI.Api 127.0.0.1:5090 ──stdin/stdout──▶ yue2_worker.py
          ◀── Server-Sent Events: Warteschlange, Fortschritt, Protokoll
```

## Voraussetzungen

- ein Mac mit Apple Silicon, macOS 14 oder neuer, mindestens 16 GB Speicher (24 GB und mehr sind angenehm)
- YuE2 mit den Engines von YuE Studio: entweder die App **YuE Studio** (die Modelle einmal über die App laden) oder `deploy/setup-mac.sh`, das ohne die App auskommt (siehe unten)
- .NET 10 SDK, Node.js 22.12+
- für den Fernzugriff: Tailscale

## Einrichtung auf einem neuen Mac

Auf einem Mac, auf dem außer macOS nichts installiert ist, richtet ein Skript alles ein:

```sh
xcode-select --install                 # Command Line Tools (git, clang), einmal
git clone https://github.com/Marcel-B/Tonwerk-AI.git ~/repos/Tonwerk-AI
~/repos/Tonwerk-AI/deploy/setup-mac.sh # --transcription, --voices, --speech, --images, --midi, --montage, --ace, --whisper, --singing oder --all für mehr
```

Es prüft den Mac (Apple Silicon, macOS 14+, Speicher, rund 20 GB frei) und installiert, was fehlt: Homebrew, uv, ffmpeg, Node.js und das .NET 10 SDK. Danach holt es YuE2 ohne die App, nämlich als Git-Checkout von [YuE Studio](https://github.com/tonywestonuk/YuE-Studio) in der Version, gegen die Tonwerks Erweiterung des Workers geprüft ist (`ENGINE_REF`, derzeit v0.4.0). Es baut die Brücke zur Neural Engine, legt die Python-Umgebung an und lädt die Modelle (etwa 7 GB). Am Ende veröffentlicht es Tonwerk mit `deploy/install.sh` und startet den Worker einmal zur Probe. Mit `--transcription` kommt SheetSage2 dazu (etwa 2 GB), mit `--speech` das Sprachlabor, mit `--images` die Bildmodelle für gemalte Cover, mit `--midi` Basic Pitch für Audio zu MIDI, mit `--montage` die Fotovideos, mit `--ace` ACE-Step als zweites Songmodell, mit `--singing` SoulX-Singer für die Seite **Singen**.

Die Schritte entsprechen denen des Installers der App und landen in denselben Ordnern (`~/Library/Application Support/YuE Studio`, Songs in `~/Music/YuE Studio`). Tonwerk braucht deshalb keine andere Konfiguration, und eine später installierte App findet die Modelle. Öffnet man die App, installiert sie ihre eigene Kopie über den Checkout; das funktioniert, nur die Git-Historie ist dann weg. Umgekehrt lässt das Skript eine Installation der App unangetastet, solange es nicht mit `--engine-from-source` läuft.

Das Skript kann beliebig oft laufen: Was schon da ist, überspringt es. Mit einem anderen `ENGINE_REF` (zum Beispiel `ENGINE_REF=v0.5.0 deploy/setup-mac.sh`) wechselt es die Version von YuE2. Danach sagt die letzte Zeile, ob die Erweiterung von Tonwerk noch passt. Mit `--voices` richtet es auch die Stem-Trennung (mlx-audio-separator) und Seed-VC für andere Stimmen ein, je in einer eigenen Umgebung unter `~/Library/Application Support/YuE UI/engines`; findet es dort nichts, aber die Installationen von StemMyWav (`~/repos/StemMyWav/.venv`) und ChangeMyVoice (`~/mlx-vc`, `~/seed-vc-ref`), trägt es diese in `appsettings.Production.json` ein, statt alles neu zu laden. Nicht eingerichtet werden Tailscale und LM Studio für die Songtexte.

### Updates

Zum Schluss schaltet `setup-mac.sh` die Updates ein (abschalten mit `--no-auto-update`). Ein LaunchAgent ruft alle 5 Minuten `deploy/update.sh` auf. Gibt es auf `main` neue Commits, wartet das Skript, bis Tonwerk nichts mehr rechnet (`GET /api/busy`; wartende Aufträge überstehen den Neustart). Dann sichert es Datenbank, Push-Schlüssel und `appsettings.Production.json` nach `~/Backups/YueUI` und holt die Commits. Danach lässt es `setup-mac.sh --update` mit den Optionen des letzten Laufs laufen. So kommen mit Tonwerk auch die Versionen, die das Skript festlegt: ein neues `ENGINE_REF` für YuE2, `SEED_VC_REF` und `SEPARATOR_PACKAGE` für die Stimmen; das Sprachlabor nur, wenn sich `install-speech.sh` geändert hat. Ein fehlgeschlagener Commit wird erst mit dem nächsten wieder versucht.

Was nicht über das Repository kommt, wird nicht automatisch geholt. Erscheint eine neue Version von YuE Studio, steht sie einmal im Log. `ENGINE_REF` wird erst angehoben, wenn die Erweiterung des Workers gegen die neue Version geprüft ist.

```sh
deploy/update.sh status     # an oder aus, zuletzt deployter Commit, Ende des Logs (~/Library/Logs/tonwerk-update.log)
deploy/update.sh on 10      # alle 10 Minuten; off schaltet ab
deploy/update.sh            # eine Runde von Hand
```

Der LaunchAgent heißt wie der von `yue watch` (Repository scripts), damit nie beide laufen.

## Entwicklung

```sh
dotnet run --project src/YueUI.Api     # API auf 127.0.0.1:5091, SpaProxy startet Vite auf 127.0.0.1:5174/ui/
dotnet test                            # Tests mit einem simulierten Worker
```

Nur das Frontend, in `src/YueUI.Api/ClientApp`: `npm run dev`, `npm run build`, `npm run type-check`.

## Dauerbetrieb und Fernzugriff

```sh
deploy/install.sh                      # veröffentlicht nach ~/Library/Application Support/YueUI und startet einen LaunchAgent
tailscale serve --bg --https=8443 5090 # https://<mac-name>.<tailnet>.ts.net:8443/ → 127.0.0.1:5090
```

Der Server lauscht nur auf `127.0.0.1`. Erreichbar wird er erst über `tailscale serve`, und das nur für Geräte im eigenen Tailnet, mit HTTPS-Zertifikat. Die Oberfläche hat kein eigenes Login; der Schutz ist das Tailnet. Nicht mit `tailscale funnel` ins Internet stellen.

Ein eigener HTTPS-Port (8443), weil `tailscale serve` auf 443 schon einen anderen Dienst weiterleiten kann und `--bg 5090` diesen ersetzen würde. Ein Unterpfad (`--set-path`) funktioniert nicht, da die App mit den festen Pfaden `/ui` und `/api` arbeitet. Abschalten: `tailscale serve --https=8443 off`.

`deploy/install.sh` nach Änderungen einfach erneut ausführen. `deploy/uninstall.sh` entfernt den LaunchAgent wieder, die Songs bleiben erhalten.

Auf dem iPhone lässt sich die Seite über „Teilen → Zum Home-Bildschirm“ wie eine App ablegen.

## Seiten, Player und Playlists

Die Menüleiste oben wechselt zwischen **Erstellen** (Formular, erweiterte Parameter), **Titel** (die Bibliothek) und **Playlist**; unter **Werkzeuge** stehen **Transkription** (SheetSage2), **Stimmen** (nur mit Stem-Trennung oder Seed-VC, siehe unten), **Logic**, **Begleitstimmen** und **Sprachlabor** (siehe unten). Auf dem Handy steckt das hinter dem Menüknopf. Die Seite steht in der Adresse (`/ui/#/songs`), die Zurück-Taste und ein Lesezeichen funktionieren also. Rechts führt die Sanduhr zur **Warteschlange** (was gerade rechnet und was wartet, mit Protokoll); solange etwas rechnet oder wartet, steht die Anzahl daneben, auch auf dem Handy. Das Zahnrad daneben schaltet Benachrichtigungen und die Sprache um, in der App auf dem Home-Bildschirm lädt es auch neu. Auf **Erstellen** zeigt dann eine Zeile, welches Modell gerade den Speicher hat.

In der Warteschlange zeigt jeder Song seine Schritte als waagerechte Zeitleiste: Warten, Partitur, Tokens, Synthese und Audio, mit der Dauer jedes erledigten Schritts. Der Kreis des laufenden Schritts füllt sich mit dessen Fortschritt, darunter steht, was der Worker gerade meldet. Ein neu gerenderter Entwurf beginnt gleich bei der Synthese. Fertige Songs klappen auf eine Zeile mit der Gesamtdauer zusammen; **Schritte** öffnet die Zeitleiste wieder.

Songs, Neuberechnungen und Textentwürfe werden immer angenommen. Der Mac hat nur Speicher für ein großes Modell zur Zeit (YuE2, das Textmodell oder Stem-Trennung und Seed-VC); was gerade keinen Platz hat, steht oben in der Warteschlange unter **Wartet** und startet der Reihe nach, sobald der Speicher frei ist. Eine Ausnahme bündelt nach Modell: Rechnet YuE2 gerade, gehen neue Songs und Neuberechnungen an einem wartenden Textentwurf vorbei direkt an den Worker, der sie mit den laufenden bündelt; so wird YuE2 nicht für den Entwurf entladen und danach neu geladen. Das gilt nur, bis der Entwurf vorn 20 Minuten gewartet hat (`Queue:BundleWindow`); dann warten neue Songs hinter ihm. Ebenso hält eine Fassung, die schon so lange auf den Speicher wartet, neue Songs zurück, bis sie dran war. Mit den Pfeilen ändert sich die Reihenfolge, mit dem Kreuz fliegt ein Auftrag wieder heraus. Wartende Aufträge liegen in `yueui.db` und überstehen einen Neustart oder ein Deploy. Ein Textentwurf, der warten muss, landet trotzdem im Formular, sobald er fertig ist (auch nach dem Neuladen, mit Benachrichtigung).

Abgespielt wird in einem Player am unteren Rand, der beim Seitenwechsel weiterläuft. Ein Song aus der Bibliothek spielt danach die folgenden Songs der Bibliothek, einer aus einer Playlist die folgenden dieser Playlist. Titel, Cover (ohne eigenes das gezeichnete), Vor/Zurück und ein Fortschrittsbalken zum Spulen erscheinen auch auf dem Sperrbildschirm.

Neben den Sternen im Player zeigt ein kleiner Analyzer das Spektrum des laufenden Songs. Ein Tipp darauf öffnet zwei Schalter: den Analyzer selbst und **Hintergrund zur Musik**, bei dem auf jeder Seite hinter den Karten drei weiche Lichter mit Bass, Mitten und Höhen pulsieren (standardmäßig aus; bei der Systemeinstellung „Bewegung reduzieren“ bewegt sich nichts). Beides merkt sich der Browser. Für den Analyzer läuft der Ton über Web Audio; wer das auf dem iPhone nicht will (etwa wenn die Wiedergabe im Hintergrund stockt), schaltet ihn aus und lädt die Seite neu.

Ein Tipp auf Titel oder Cover im Player öffnet ihn **über den ganzen Bildschirm**, wie bei Spotify oder Apple Music: das Cover groß vor einer weichgezeichneten Kopie seiner selbst (Songs ohne eigenes Cover zeigen das gezeichnete), Bewertung, Playlist-Knopf, ein Fortschrittsbalken zum Spulen mit verstrichener und verbleibender Zeit und große Knöpfe. Darunter drei Schalter, die sich der Browser merkt: **Text** zeigt den Songtext mit seinen Abschnitten statt des großen Covers (am breiten Bildschirm daneben), bereinigt: Regieanweisungen in runden Klammern wie „(Screamy vocals, 808 bass)“ blendet er aus, Abschnitte wie `[Verse]` stehen in eigener Zeile, und lange Absätze ohne Zeilenumbrüche bricht er an Satzenden um, bei englischem Text auch vor einem Großbuchstaben nach einem klein geschriebenen Wort (geschätzt, der gespeicherte Text bleibt, wie er ist); enthält der Text Anweisungen, erscheint der Schalter **Anweisungen**, der sie gedimmt in eigener Zeile zeigt (in der App die Masken), **Analyzer** ein großes Spektrum in der Farbe des Covers, **Effekte** drei Lichter in dieser Farbe, die mit Bass, Mitten und Höhen pulsieren, und das Cover schlägt leicht mit dem Bass (bei „Bewegung reduzieren“ bleibt beides still). Analyzer und Effekte leiten den Ton über Web Audio, auch wenn der kleine Analyzer aus ist, aber nur, solange der große Player offen ist. Der Pfeil oben, Herunterziehen, die Zurück-Geste oder Escape verkleinern ihn wieder; die Musik läuft dabei weiter. Der Text läuft nicht mit, YuE2 liefert keine Zeitmarken für die Zeilen.

Auf der Playlist-Seite wählt das Feld oben, welche Playlist sie zeigt; daneben legen Plus, Stift und Papierkorb eine neue an, benennen die gezeigte um oder löschen sie (die Songs selbst bleiben, die letzte Playlist lässt sich nicht löschen). Welche Playlist gezeigt wird, merkt sich jeder Browser selbst. Die Reihenfolge lässt sich dort ändern.

Das Plus neben einem Song setzt ihn ans Ende der Playlist, der Haken nimmt ihn wieder heraus; denselben Knopf hat der Player für den Song, der gerade läuft. Gibt es mehrere Playlists, öffnet der Knopf eine Liste, in der jeder Tipp den Song in eine Playlist legt oder herausnimmt; der Haken am Knopf heißt dann, dass der Song in mindestens einer steckt. Die Playlists liegen auf dem Server in `~/Library/Application Support/YuE UI/yueui.db` (SQLite), Handy und Mac sehen also dieselben. Gelöschte Songs fallen von selbst heraus.

Der Pfeil neben einem Lauf übernimmt Titel, Stil und Text als **Vorlage** ins Formular. **Alles übernehmen** beim Song (auf dem Handy im Menü „…“, im Player und in der Playlist ebenso) trägt alles ein, womit genau dieser Song erzeugt wurde: Titel, Stil, Text, Seed, Partitur samt Planung, Qualität, Länge und die erweiterten Parameter. „Erzeugen“ macht ihn dann noch einmal, oder man ändert vorher den Seed oder einzelne Werte. Hatte der Song keine eigene Partitur, kommt die mit, die YuE2 für ihn geschrieben hat, aber mit ausgeschaltetem Haken bei **Eigene Partitur**: So entsteht beim Erzeugen derselbe Song, und eingeschaltet lässt sich auf ihr aufbauen, etwa in einer anderen Tonart (mit Partitur wird es auch beim selben Seed ein anderer Song). **Als Partitur übernehmen** setzt dagegen nur die Partitur samt Planung und lässt den Rest des Formulars stehen. Auch die **erweiterten Parameter** haben einen Haken: ausgeschaltet gehen ihre Standardwerte hinaus, die eingetragenen Werte bleiben stehen. Der Seed steht deshalb neben der Anzahl, außerhalb der erweiterten Parameter.

Der Stift neben einem Lauf gibt ihm einen neuen Titel. Er gilt für Bibliothek, Player, Playlist, Warteschlange und die Dateinamen beim Herunterladen; der Ordner behält seinen Namen, damit Playlist, Links und YuE Studio den Song weiter finden. Der neue Titel liegt ebenfalls in `yueui.db`, ein leeres Feld stellt den ursprünglichen wieder her.

Jeder Song lässt sich mit 1 bis 5 Sternen bewerten, in der Bibliothek und im Player für den Song, der gerade läuft. Ein Tipp auf denselben Stern nimmt die Bewertung wieder weg. Die Bewertung gehört zum einzelnen Song, nicht zum Lauf, und liegt in `yueui.db`. Die Auswahl neben der Suche zeigt nur Songs ab einer Bewertung (oder nur die mit fünf Sternen); was dann in der Liste steht, spielt der Player auch nacheinander ab. Die zweite Auswahl sortiert die Bibliothek: nach Datum (neueste oder älteste zuerst), nach Bewertung oder nach Dauer (längste oder kürzeste zuerst). Ein Lauf bleibt dabei zusammen und rückt nach seinem besten Song ein, der dann innerhalb des Laufs vorne steht. Die Reihenfolge merkt sich jeder Browser selbst.

Zu jedem Song lässt sich eine Notiz schreiben: Die Sprechblase neben FLAC und ABC klappt ein Textfeld auf und wieder zu. Gespeichert wird von selbst, kurz nachdem man aufhört zu tippen, beim Verlassen des Feldes und wenn die App in den Hintergrund geht. Hat ein Song eine Notiz, ist die Sprechblase grün gefüllt. Ein geleertes Feld löscht die Notiz. Notizen liegen wie die Bewertungen in `yueui.db` und verschwinden mit dem Song.

**Abspielen unterwegs.** Der Player spielt nicht die FLAC (rund 1 Mbit/s), sondern eine AAC-Kopie mit 192 kbit/s, etwa ein Sechstel davon: Der Mac hängt an einem Anschluss mit langsamem Upload, und über Mobilfunk hat die FLAC oft lange geladen. Die Kopie entsteht mit `afconvert` (sonst `ffmpeg`), sobald ein Song oder eine Fassung fertig ist, bei älteren Songs beim ersten Abspielen. Sie liegt in `stream/` neben der Datenbank (die einer Fassung neben deren FLAC in `versions/`), wird nach einem Neu-Rendern neu gemacht und geht mit dem Song. Wie ein Export trägt sie Titel, Album, Nummer, Songtext, Stil samt Seed und das eigene Cover des Songs (Fassungen mit der Stimme im Titel); ändern sich Titel, Cover oder Text, bekommt sie beim nächsten Abspielen neue Tags, ohne neu kodiert zu werden. Songs ohne eigenes Cover haben keins in der Datei, das gezeichnete gibt es nur im Browser. Herunterladen, Exportieren und die Logic-Seite nehmen weiter die FLAC (Downloads und Exporte mit denselben Tags, siehe unten); ohne Encoder spielt auch der Player sie.


**Offline hören:** Die Wolke neben dem Namen einer Playlist legt ihre Songs auf dieses Gerät, etwa vor einer Fahrt ohne Netz. Gespeichert wird die AAC-Kopie des Players mit Cover (ein Song etwa 4 bis 6 MB); die Zeile über der Liste sagt, wie viele Songs schon da sind und wie viel Platz sie brauchen, ein Haken markiert jeden gespeicherten Song. Gespeicherte Songs kommen auch mit Verbindung vom Gerät statt vom Mac. Kommt ein Song neu gerendert oder fliegt er aus der Playlist, gleicht Tonwerk das beim nächsten Öffnen mit Verbindung ab. Ohne Verbindung zeigt die App die zuletzt geladene Bibliothek und Playlist. Das gilt je Gerät und Browser: Die App auf dem Home-Bildschirm des iPhones hat ihren eigenen Speicher, getrennt von Safari.

**Teilen / Exportieren** beim Song (auf dem Handy im Menü „…“, auch im Menü von Player und Playlist) ist ein Knopf für beides: Erst wählst du das Format, dann wird die Datei erstellt, und danach speicherst du sie oder schickst sie über das Teilen-Menü des Handys an wen du willst. **Klein** ist eine AAC mit 128 kbit/s (etwa ein Zehntel der FLAC) für Messenger, dazu MP3 (320 kbit/s, braucht `ffmpeg`), M4A (AAC, 256 kbit/s) oder FLAC für die Musiksammlung; das zuletzt gewählte Format merkt sich der Browser. Erstellt wird mit `afconvert`, das zu macOS gehört (anderswo mit `ffmpeg`). Browser ohne Teilen-Menü für Dateien bieten nur Speichern an. Titel, Album (der Songtitel), Jahr, Nummer innerhalb des Laufs, Songtext und Stil samt Seed (als Kommentar) stehen in den Tags des Formats (ID3v2.3, iTunes-Atome, Vorbis-Kommentare). Interpret und Genre fragt der Dialog; der Interpret bleibt pro Browser gespeichert, das Genre schlägt er aus dem Stil vor. **Cover** gibt es an genau einer Stelle: **Cover wählen** beim Song nimmt ein Foto, schneidet es quadratisch zu und speichert es beim Song. Die Bibliothek zeigt es neben dem Song, der Player und der Sperrbildschirm beim Abspielen, und jede Datei, die Tonwerk verlässt, trägt es mitsamt Songtext: Teilen / Exportieren, FLAC-Download (auch einer Fassung) und die Kopie des Players. **Cover entfernen** nimmt es wieder weg. Ein Song ohne eigenes Cover bekommt nur bei Teilen / Exportieren eins, das der Browser aus Songtitel und einem Farbverlauf zeichnet (Farben aus der Song-ID); der Export-Dialog zeigt, welches hineinkommt. Der Songordner von YuE Studio bleibt unberührt: Downloads sind getaggte Kopien. Die Bilder liegen in `covers/` neben der Datenbank, der Songordner bleibt unberührt. 

**In Tonregal übernehmen** (Rechtsklick auf einen Song in der Bibliothek, oder im Menü „…“ von Player und Playlist) schickt den Song an [Tonregal](https://github.com/Marcel-B/Tonregal), die Musiksammlung auf oi-o-mat. Der Dialog ist der von Teilen / Exportieren ohne Formatwahl: Tonregal bekommt immer die FLAC mit denselben Tags und demselben Cover, dazu Tempo und Tonart, wie Tonwerk sie gehört hat, und legt sie unter `Interpret/Jahr - Album/` ab; der Interpret ist deshalb Pflicht. Der Dialog wartet ein paar Sekunden und sagt, wo der Song gelandet ist. Ein zweites Übernehmen desselben Songs legt nichts doppelt an. Der Menüpunkt erscheint erst, wenn Adresse und API-Schlüssel eingetragen sind, in `~/.config/tonwerk/tonregal.env` auf dem Mac mit Tonwerk:

```
TONREGAL_URL=http://oi-o-mat.tail6105c0.ts.net:5100
TONREGAL_API_KEY=<derselbe Schlüssel wie unter Tonregal:Ingest:Keys:tonwerk-ai in Tonregals appsettings.Production.json>
```

Die Datei wird bei jeder Übernahme neu gelesen; die Seite zeigt den Menüpunkt nach dem nächsten Neuladen.

**Lautheit:** Alles, was Tonwerk zum Anhören verlässt, kommt auf −14 LUFS mit höchstens −1 dBTP Spitze: Teilen / Exportieren (auch FLAC), die Kopie des Players und die Musikvideos. Das ist der Pegel, auf den YouTube, Spotify und Apple Music lautere Songs ohnehin herunterdrehen, und so klingen alle Songs gleich laut. `ffmpeg` misst jede Datei einmal (`loudnorm`, die Messung bleibt in `yueui.db`) und ändert dann nur die Lautstärke des ganzen Songs, die Dynamik bleibt; nur wo die Spitzen sonst zu hoch würden, greift sein Begrenzer. FLAC-Download, Stems und die Logic-Seite behalten das Original zum Weiterarbeiten. Abschalten oder ein anderes Ziel: `Loudness:Enabled`, `Loudness:Target`, `Loudness:TruePeak` in `appsettings.Production.json`; die Kopien des Players werden danach einmal neu gemacht.

**Auswertung** (unter Werkzeuge) zeigt, womit die bewerteten Songs gemacht wurden, gegen ihre Sterne: Stil-Bausteine, Modell (YuE2 oder ACE-Step), Qualität, LoRA, Schritte, Länge und ob instrumental. Gezählt wird ab zwei Songs; damit ein einzelner Glückstreffer nicht vorn landet, rechnet die Reihenfolge zu jedem Wert drei Songs mit Durchschnittsbewertung dazu. Die Bausteine über dem Schnitt bietet „Erstellen“ bei den Bausteinen im Reiter **Bewährt** an.

**Musikvideo** beim Song (auf dem Handy im Menü „…“, auch im Menü von Player und Playlist) macht aus dem Song ein Video zum Hochladen, ohne gefilmte Bilder: das Cover vor einer weichgezeichneten, abgedunkelten Kopie seiner selbst (mit **Cover zeigen** aus bleibt nur diese Kopie als Hintergrund), auf Wunsch der Titel darunter (er blendet am Anfang ein, mit dem Interpreten aus dem Export-Dialog als zweiter Zeile), dazu unter **Analyzer** **Balken** nach Tonhöhe, gerechnet wie der Analyzer im Player (dieselben Bänder bis 16 kHz und dieselbe Dezibel-Skala, sodass auch die Höhen sichtbar sind; sie steigen sofort und fallen langsam zurück), eine **Welle** oder **Aus**, beide in einer Farbe aus dem Cover (seinem kräftigsten Farbton, hell genug für den dunklen Hintergrund; ein graues Cover behält Tonwerks Grün), und als Bewegung dahinter **Partikel**, die langsam aufsteigen und mit dem Bass heller werden, eine **Plasmakugel**, deren Blitze aus der Mitte zucken (zwölfmal in der Sekunde eine neue Form) und mit dem Bass aufleuchten, oder **Keine**. Mit Cover zucken die Blitze hinter ihm hervor, ohne Cover laufen sie von einem glühenden Kern bis zum Bildrand. Formate: **YouTube 16:9** (1920×1080) und **Shorts/Reels 9:16** (1080×1920, alles oberhalb des unteren Fünftels, das die Apps mit Text und Knöpfen belegen). Beides in 30 fps als H.264 (High, CRF 18) mit AAC 320 kbit/s bei 48 kHz und dem Index vorn, also so, wie YouTube, Instagram und TikTok es empfehlen; das Video blendet am Anfang auf und am Ende ab. Der Dialog zeigt vorher ein Standbild (Balken und Welle darin nur angedeutet). Song ohne eigenes Cover bekommen das gezeichnete wie beim Export. Gerechnet wird auf dem Mac mit `ffmpeg`, im Hintergrund und eins nach dem anderen; die Videos stehen dann in der Warteschlange unter **Musikvideos** und melden sich per Benachrichtigung. Sie brauchen keinen großen Speicher und warten deshalb nicht auf YuE2 oder die Stimmen, machen diese aber etwas langsamer. Fertige Videos listet derselbe Dialog zum Ansehen, Speichern, Teilen und Löschen. **Speichern** liefert das Video in voller Qualität für YouTube (ein Fotovideo mit vielen Schnitten kann einige hundert MB groß sein). **Teilen** liefert eine kleine Fassung für Messenger: 720p, höchstens 2,5 Mbit/s, ein Song von vier Minuten bleibt also unter 80 MB. Am Mac-Browser ohne Teilen-Menü wird sie gespeichert. Sie entsteht beim ersten Teilen, das deshalb bei einem langen Video etwa eine Minute dauert, und bleibt dann neben dem Video liegen. Pro Song und Format gibt es eins: ist ein neues fertig, ersetzt es das bisherige im selben Format (bis dahin bleibt das alte anzusehen), YouTube- und Shorts-Fassung bleiben nebeneinander. Songs mit Video tragen in der Bibliothek neben Länge und Qualität ein Kamera-Symbol, das den Dialog öffnet. Die Standbilder (der Browser zeichnet sie als PNG) und das MP4 liegen in `videos/` neben der Datenbank und gehen mit dem Song; in die Datensicherung kommen sie nicht, sie lassen sich jederzeit neu machen. Shorts dürfen höchstens drei Minuten lang sein; ein längerer Song wird auf YouTube ein normales Video.

Unter **Bild** wählt der Dialog statt **Cover** auch **Fotos & Videos**: Dann nimmt der Mac eigene Fotos und Clips aus einem Ordner und schneidet sie auf die Beats des Songs, wie es die Apps von DJI oder Insta360 tun. Jedes Video bekommt einen Ordner unter `~/Pictures/Tonwerk` auf dem Mac (`Montage:MediaRoot`), etwa `~/Pictures/Tonwerk/Hund`; der Dialog listet die Ordner mit der Zahl ihrer Fotos und Clips. Gelesen werden JPEG, PNG, WebP und HEIC (die Fotos des iPhones) sowie MP4 und MOV, auch in Unterordnern. Aus der Fotos-App kommt ein Album dorthin mit Ablage → Exportieren → Unbearbeitetes Original. So läuft es ab:

1. **Musik:** librosa sucht die Beats, den ersten Schlag jedes Takts, die Lautstärke je Beat und die Wechsel zwischen den Teilen des Songs. Geschnitten wird auf dem ersten Schlag eines Takts: in leisen Teilen alle zwei Takte, dazwischen jeden Takt, wo es laut ist (meist im Refrain) jeden halben Takt, und immer dort, wo ein neuer Teil beginnt. Keine Einstellung bleibt länger als sechs Sekunden oder kürzer als sechs Zehntel stehen.
2. **Bilder:** Jedes Foto und Stichproben aus jedem Clip (alle paar Sekunden) werden nach Schärfe und Belichtung bewertet, schwarze und verwackelte Bilder fallen nach hinten, fast gleiche (Serienbilder) bleiben nur einmal. Mit einem **Motiv** („Hund am Strand“) zählt vor allem, wie gut ein Bild dazu passt: Das entscheidet CLIP (`xlm-roberta-base-ViT-B-32`, versteht Deutsch und Englisch, beim ersten Mal etwa 1,5 GB Download, danach in `~/Library/Application Support/YuE UI/montage/models`). Aus einem Clip kommen seine besten Stellen, höchstens eine je vier Sekunden.
3. **Schnitt:** Die besten Bilder kommen in der Reihenfolge, in der sie aufgenommen wurden (Aufnahmedatum aus EXIF und den Clips, sonst das Dateidatum). Sind es weniger als Schnitte, kommen sie erneut, mit anderem Zoom. Fotos zoomen langsam hinein oder heraus (Ken Burns), wo es laut ist etwas mehr. Ein Hochformatfoto im Querformat (und umgekehrt) wird nicht beschnitten, sondern steht ganz vor einer weichgezeichneten Kopie seiner selbst. Clips laufen stumm ab der gewählten Stelle; HDR-Clips des iPhones (HLG) werden auf normale Farben umgerechnet.

Analyzer, Titel und Cover kommen wie beim Cover-Video darüber, auf Wunsch; zum Lesen sind die Bilder unten und an den Rändern etwas abgedunkelt. Die Bewegung (Partikel, Plasmakugel) gibt es hier nicht, die Bilder bewegen sich selbst. Ein Fotovideo ersetzt nur ein älteres Fotovideo aus demselben Ordner im selben Format, das Cover-Video bleibt daneben. Für einige hundert Fotos und ein Dutzend Clips braucht die Auswahl einige Minuten, der Schnitt einige Sekunden je Einstellung, dann folgt das eigentliche Video wie beim Cover. Die Zwischenstände (Standbilder, Einstellungen) werden danach gelöscht. Eingerichtet wird es mit `deploy/setup-mac.sh --montage` (oder `deploy/install-montage.sh`, eine eigene Python-Umgebung mit librosa, Pillow, pillow-heif, PyTorch und open_clip); ohne sie sagt der Dialog, was fehlt. Die Fotomediathek direkt (Alben und Schlagwörter über osxphotos) ist noch nicht angebunden.

Das Suchfeld über der Bibliothek sucht wahlweise in **Titel & Stil** oder im **Text**. Getrennt, weil fast jeder Songtext Allerweltswörter wie „Nacht“ enthält und die wenigen Treffer im Titel sonst untergingen. Mehrere Wörter müssen alle vorkommen, in beliebiger Reihenfolge; Groß- und Kleinschreibung und Akzente zählen nicht („traume“ findet „Träume“). Was in Anführungszeichen steht, muss genau so vorkommen, etwa eine erinnerte Zeile. Bei der Textsuche stehen die passenden Zeilen mit markierten Treffern unter dem Stil. Der Player spielt nach einem Song die weiteren Treffer.

## Benachrichtigungen

**Benachrichtigungen einschalten** im Zahnrad-Menü oben rechts meldet per Web Push, wenn ein Song fertig ist oder fehlschlägt, eine Transkription endet oder ein Songtext-Entwurf steht, auch bei gesperrtem Handy. Abgebrochene Songs bleiben still. Auf iPhone und iPad geht das nur in der App auf dem Home-Bildschirm (ab iOS 16.4) und nur über HTTPS, also über `tailscale serve`; im normalen Safari-Tab erklärt der Eintrag das. Beim Einschalten kommt eine Test-Nachricht. Jedes Gerät schaltet für sich ein, die Texte kommen in der Sprache, die dort eingestellt ist.

**Song per Kurzbefehl oder Siri:** `POST /api/quick` macht aus ein paar Wörtern in einem Aufruf einen Song. Erst schreibt das Textmodell den Songtext (wie „Text schreiben“ im Formular, Deutsch, wenn nichts anderes gesagt ist), danach kommt der Song von selbst in die Warteschlange; die Benachrichtigung meldet den fertigen Song (der Textentwurf meldet sich nur, wenn er scheitert). Der Stil ist der des neuesten Songs, wenn keiner mitkommt. So wird daraus ein Kurzbefehl auf dem iPhone (Tailscale muss verbunden sein):

1. In der App **Kurzbefehle** einen neuen Kurzbefehl anlegen, z. B. „Tonwerk-Song“.
2. Aktion **Text diktieren** (Sprache Deutsch).
3. Aktion **Inhalte von URL abrufen** mit der URL `https://<mac-name>.<tailnet>.ts.net:8443/api/quick`, Methode **POST**, Anfragetext **JSON** und dem Feld `keywords` (Text) = **Diktierter Text**. Wahlweise weitere Felder: `style` (Text), `language` (`de` oder `en`), `batch` (Zahl, 1 bis 10), `quality` (`draft` oder `full`), `title` (sonst der Anfang der Stichwörter).
4. Wahlweise **Mitteilung anzeigen** mit „Song ist unterwegs“.

Danach reicht „Hey Siri, Tonwerk-Song“, dann sagen, worum es gehen soll. Die Antwort ist `202` mit `draftId`, `stage`, `title` und `style`, ein fehlendes oder falsches Feld ein `400` mit der Erklärung.

Der Server legt beim ersten Mal ein VAPID-Schlüsselpaar an und speichert es mit den Abonnements in `~/Library/Application Support/YuE UI/push.json` (nur für den eigenen Benutzer lesbar). Diese Datei nicht löschen: Mit einem neuen Schlüssel kommt nichts mehr an, bis jedes Gerät die Benachrichtigungen einmal neu einschaltet. Die Nachrichten gehen über die Push-Dienste von Apple, Google oder Mozilla, der Mac braucht dafür Internet.

## Tempo und Tonart

Tonwerk hört jeden Song einmal auf Tempo und Tonart an (librosa: Beat-Tracking und Krumhansl-Tonartprofile über die Chroma) und zeigt beides beim Song, etwa „92 BPM · F♯-Moll“. Die Bibliothek filtert nach langsam (unter 90 BPM), mittel, schnell (ab 120 BPM), Dur und Moll und sortiert nach Tempo. Nach „Alles übernehmen“ steht das gehörte Tempo als erster Baustein unter Stil → Tempo; es kommt nicht von selbst in den Stil, weil der Song sonst anders würde.

Neue Songs werden gehört, sobald sie fertig sind, ältere nach und nach, aber nur solange YuE2 und die anderen großen Modelle ruhen. Tonwerk nimmt dafür das Python der Fotovideos (`--montage`) oder von Audio zu MIDI (`--midi`), beide haben librosa; eine eigene Einrichtung gibt es nicht. Ohne beides zeigt die Bibliothek, was die Partitur (`Q:`, `K:`) oder ACE-Step verlangt hat, mit dem Hinweis „laut Partitur“, und hört nach, sobald eine der beiden Umgebungen da ist. Die Partitur gibt auch die Oktave des Tempos vor, damit ein Song nicht mit halbem oder doppeltem Tempo gehört wird. Abschalten: `Analysis:Auto` auf `false`.

## Speicherplatz

Jeder Song belegt mit FLAC, Tokens und Zwischendateien einiges an Platz. Die Bibliothek zeigt deshalb, was jeder Lauf belegt und wie viel auf dem Datenträger noch frei ist. Über den Papierkorb lassen sich einzelne Songs, ganze Läufe und Transkriptionen löschen; mit dem letzten Song eines Laufs verschwindet auch sein Ordner. Gelöscht wird nach einer Rückfrage endgültig und nicht in den Papierkorb von macOS, denn dort würde der Platz erst beim Leeren frei. Songs, an denen der Worker von Tonwerk noch arbeitet, lassen sich erst nach dem Abbrechen löschen; was YuE Studio gerade erzeugt, erkennt Tonwerk nicht.

### Auf eine externe Platte verlegen

Modelle, Songs und was Tonwerk sonst an großen Ordnern anlegt, lassen sich auf eine externe Platte verlegen. `deploy/move-to-volume.sh` ohne Angaben zeigt, was sich verlegen lässt, wie groß es ist und wo es liegt: `yue2` (die Modelle von YuE2, auch die von YuE Studio), `ane`, `songs`, `lmstudio`, `speech`, `images`, `montage` (die CLIP-Modelle der Fotovideos), `acestep` (Modelle von ACE-Step), `acesongs` (Songs von ACE-Step), `singing` (die Gewichte von SoulX-Singer), `separator`, `seedvc`, `versions`, `stems` und `videos`.

Ein Teil, den es noch nicht gibt, weil das Feature erst später installiert wird oder seine Modelle erst beim ersten Gebrauch lädt, lässt sich vorab verlegen: Das Skript legt dann einen leeren Ordner auf der Platte an und verlinkt ihn, sodass die Downloads gleich dort landen. Die Python-Umgebungen der Werkzeuge (`env`, je ein bis zwei GB) bleiben auf der internen Platte; Audio zu MIDI bringt sein kleines Modell darin mit und legt sonst nichts Großes an.

```sh
deploy/move-to-volume.sh                              # Übersicht
deploy/move-to-volume.sh /Volumes/SSD yue2 lmstudio   # nach /Volumes/SSD/Tonwerk/<Teil>
deploy/move-to-volume.sh --back yue2                  # zurück auf die interne Platte
```

Das Skript wartet nicht, sondern bricht ab, solange Tonwerk rechnet oder YuE Studio offen ist. Sonst hält es Tonwerk und die Updates an, kopiert, vergleicht die Kopie mit dem Original und legt erst dann am alten Ort einen symbolischen Link an. Danach startet es Tonwerk wieder. Einstellungen ändern sich nicht, und Updates, YuE Studio, LM Studio und die Sicherung finden alles am gewohnten Ort.

Damit es klappt:

- **Festplattenvollzugriff für `dotnet`.** macOS lässt einen LaunchAgent nicht ohne Weiteres auf externe Platten, er bekommt nur „Operation not permitted“. Einmal am Mac unter Systemeinstellungen → Datenschutz & Sicherheit → Festplattenvollzugriff das Programm hinzufügen, das `realpath "$(command -v dotnet)"` nennt, dann Tonwerk neu starten (`launchctl kickstart -k gui/$(id -u)/de.bvelop.yueui`).
- **APFS.** NTFS kann macOS nur lesen, exFAT kennt keine symbolischen Links, die der Modell-Cache braucht. Das Skript prüft das.
- **Eine SSD für Modelle.** Jedes Modell wird bei jeder Benutzung neu geladen; für Songs reicht auch eine Festplatte.
- **Nicht in einen Nextcloud-Ordner.** Sonst lädt der Client Gigabytes an Modellen hoch.

Externe Platten werden beim Anmelden eingehängt, zusammen mit dem Start von Tonwerk. Fehlt die Platte, fehlt nur, was auf ihr liegt, bis sie wieder da ist.

## Zwei Macs: ein zweiter rechnet mit

Ein zweiter Mac mit Tonwerk kann Aufträge übernehmen, während der erste YuE2 oder ein anderes großes Modell im Speicher hat: Textentwürfe mit LM Studio, Sprachlabor und „Sprache säubern“, Cover malen, ACE-Step, Singen, Stimmen (Fassungen und „Stimmenspur tauschen“) und Stems. YuE2 selbst, Transkription, Videos und Audio zu MIDI bleiben auf dem eigenen Mac. Jeder Mac hält sich an seine eigene Regel „ein großes Modell zur Zeit“; ein Auftrag auf dem Helfer belegt hier nichts und läuft deshalb neben YuE2.

Bei jedem dieser Aufträge lässt sich der Rechner wählen: **Automatisch** (der eigene Mac, wenn sein Speicher frei ist, sonst ein freier Helfer, der das Werkzeug hat), der eigene Mac oder ein bestimmter Helfer. Die Auswahl erscheint nur, wenn ein Helfer eingetragen ist. Am Auftrag steht danach, wo er lief. Dateien gehen hin und zurück über die Verbindung; die Ergebnisse liegen wie immer auf dem eigenen Mac, der Helfer behält nichts.

Einrichtung:

1. Auf dem Helfer Tonwerk installieren (`deploy/setup-mac.sh` mit den Werkzeugen, die er rechnen soll, z. B. `--speech --images --voices`; YuE2 braucht er nicht) und in seiner `appsettings.Production.json` `"Machines": { "Serve": true, "Name": "Alter Mac" }` eintragen.
2. Einen gemeinsamen Schlüssel erzeugen und auf **beiden** Macs in `~/.config/tonwerk/helper.key` legen: `openssl rand -hex 32 > ~/.config/tonwerk/helper.key` und die Datei auf den anderen Mac kopieren.
3. Den Helfer über Tailscale erreichbar machen, wie den eigenen Mac (`tailscale serve --bg --https=8443 http://127.0.0.1:5090`). Eine LAN-Adresse geht nicht, macOS lässt den LaunchAgent nicht ins lokale Netz.
4. Auf dem Haupt-Mac den Helfer eintragen:

```json
"Machines": {
  "Name": "Neuer Mac",
  "Helpers": [ { "Id": "alt", "Name": "Alter Mac", "Url": "https://mac-alt.tailxxxx.ts.net:8443/" } ]
}
```

Nach einem Neustart fragt Tonwerk den Helfer alle fünf Sekunden, ob er frei ist und was er kann; `GET /api/machines` und das Protokoll (Quelle `server`) zeigen, ob er erreichbar ist. Ein Helfer ohne `Serve` antwortet auf seine Helfer-Adressen mit `404`, ein falscher Schlüssel mit `401`.

## YuE Studio und Tonwerk gleichzeitig

Beide haben einen eigenen Worker und damit ein eigenes Modell im Speicher. Rechnen beide gleichzeitig, kann der Speicher knapp werden. Die Oberfläche zeigt deshalb einen Hinweis, solange die App geöffnet ist. **Worker beenden** in der Warteschlange gibt den Speicher von Tonwerk sofort frei. Ansonsten entlädt der Worker das Modell nach zehn Minuten Leerlauf von selbst.

## Vorlagen

Ist ein Song gut geworden, lässt sich das Formular als Vorlage speichern: Das Lesezeichen neben **Vorlage laden** auf „Neuer Song“ fragt nach einem Namen und danach, was hinein soll: **Stil** (mit „Instrumental“), **Seed**, **Parameter** (Qualität, Anzahl, Planung, Schritte, Länge, Rechenwerke und beide Sampling-Gruppen samt Temperatur) und, wo Stimmen eingerichtet sind, die **Stimme** für danach. Titel, Songtext und Partitur gehören nie dazu. Trägt man den Namen einer vorhandenen Vorlage ein (oder speichert direkt nach dem Laden einer), wird sie überschrieben. Eine geladene Vorlage ändert nur, was sie enthält; eine Vorlage nur mit Stil lässt Seed und Parameter stehen. Mit einer eigenen Partitur im Formular bleibt deren Planung.

**Werkzeuge → Vorlagen** listet alle mit Stil und den wichtigsten Werten, zum Laden, Umbenennen und Löschen. Vorlagen liegen in `yueui.db` und kommen damit in die nächtliche Sicherung.

## Stil aus Bausteinen

Unter dem Stil-Feld öffnet **Bausteine** eine Auswahl nach Reitern: Sprache, Genre, Stimme (männlich, weiblich, Duett, Chor, Kinderstimme), Klangfarbe, Instrumente, Stimmung, Gestaltung (Melodie, Phrasierung, Aussprache) und Tempo. Antippen setzt einen Baustein in den Stil, nochmal Antippen nimmt ihn heraus; selbst geschriebene Begriffe bleiben stehen. Sprache, Stimme und Tempo gibt es nur einmal pro Song, eine neue Wahl ersetzt die alte (beim Tempo auch ein von Hand geschriebenes wie `95 BPM`). Die Sprache kommt nach vorn und das Tempo ans Ende, wie in den Beispielen von YuE2.

YuE2 hat keine Tag-Liste; seine [Anleitung](https://github.com/multimodal-art-projection/YuE/blob/main/docs/generation.md) nennt Genre, Instrumente, Stimme, Sprache und Tempo, und seine Beispiele beschreiben sie in kurzen Wendungen („expressive female voice, acoustic piano, rounded bass and light drums, 88 BPM“). Die einzelnen Wörter stammen aus der Liste der 200 häufigsten Tags des Vorgängers [YuE v1](https://github.com/multimodal-art-projection/YuE/tree/YuE-v1#prompt-engineering-guide) (`top_200_tags.json`), schlichte englische Begriffe, die YuE2 genauso liest; die Wendungen aus YuE2s Beispielen stehen unter Instrumente und Gestaltung. Duett und Chor stehen in keiner Liste; wer welche Zeile singt, lässt sich damit nicht festlegen.

## Eigener Klang mit LoRA

Eine LoRA ist eine kleine Zusatzdatei zu YuE2, trainiert auf eigene Songs, zum Beispiel einen Ordner voller Dubstep. Sie lernt den Klang (Instrumente, Mix, Klangfarbe) und wirkt auf die Klangsynthese; Melodie, Akkorde und Form plant YuE2 wie immer aus Stil und Text. Trainiert wird auf dem Mac selbst, über SSH:

```sh
deploy/train-lora.sh ~/dubstep dubstep                 # Ordner mit Songs, Triggerwort (zugleich der Name)
deploy/train-lora.sh --background ~/dubstep dubstep    # läuft weiter, wenn die SSH-Verbindung abreißt
```

Der Ordner enthält 5 bis 30 Songs im selben Stil (MP3, WAV, FLAC, M4A …). Eine gleichnamige `.txt` neben einem Song beschreibt ihn genauer („wobble bass, half-time drums, 140 bpm“); das Triggerwort steht vor jeder Beschreibung und gehört später in den Stil. 1500 Schritte (Standard, `--steps` ändert das) dauern je nach Mac etwa ein bis zwei Stunden; alle 500 Schritte landet ein Zwischenstand (`dubstep-step500`) neben der LoRA, zum Vergleichen. Strg-C beendet das Training und behält, was bis dahin gelernt ist. Das Training braucht rund 6 GB und verweigert den Start, solange Tonwerk arbeitet; Songs laufen daneben weiter, Stimmen und Sprachlabor besser nicht.

Fertige LoRAs landen in `~/Library/Application Support/YuE UI/loras` und stehen im Formular unter **Erweitert → Klang-LoRA**, mit einer Stärke von 0 bis 3. LoRAs von Hugging Face im Format von ComfyUI passen auch, wenn sie nur den Klangteil ändern: die `.safetensors`-Datei in den Ordner legen oder über `POST /api/loras` hochladen.

Grenzen: YuE2 gibt kein Werkzeug heraus, das aus fremder Musik die Song-Tokens gewinnt, die die Klangsynthese beim Generieren sieht. Trainiert wird deshalb im Modus, den YuE2 für Training ohne sie kennt (nur der Text-Prompt ist sichtbar). Beim Generieren wirkt eine LoRA dadurch oft etwas schwächer; eine Stärke um 1,5 gleicht das aus. Songs mit verschiedenen LoRAs warten bei der Synthese aufeinander, weil nur eine zur Zeit in den Gewichten stecken kann, und ein Song mit LoRA geht nicht aufs iPhone. Lizenz: Die Gewichte von YuE2 stehen unter CC BY-NC 4.0, aber Einzelpersonen dürfen erzeugte Songs ausdrücklich verkaufen und vermarkten ([MODEL_LICENSE](https://github.com/multimodal-art-projection/YuE/blob/main/MODEL_LICENSE)). Eine LoRA ist eine Bearbeitung der Gewichte: Sie selbst darf nicht verkauft und nur mit Nennung von YuE2 weitergegeben werden. Ob Songs, die mit einer LoRA entstanden sind, unter dieselbe Erlaubnis fallen, sagt die Lizenz nicht ausdrücklich. Die Rechte an den Trainingssongs bleiben bei ihren Urhebern.

## Songtext mit Zeitmarken

Tonwerk weiß, welche Zeile gerade gesungen wird: Whisper (`mlx-whisper` mit large-v3 turbo, etwa 1,6 GB) hört sich jeden neuen Song einmal an, am liebsten die Gesangsspur aus den Stems, wenn es welche gibt, sonst den ganzen Song. Den Text kennt Tonwerk schon, Whisper sagt nur, wann welches Wort kommt; die Zeilen werden dann auf die gehörten Wörter gelegt, auch wenn Whisper sich verhört oder Wörter auslässt, und ein wiederholter Refrain nimmt nie die Zeiten des ersten. Zeilen ohne gehörtes Wort teilen sich die Lücke zwischen ihren Nachbarn.

- **Vollbild-Player:** Der Text läuft mit, die gesungene Zeile ist hell, die anderen gedimmt, und die Ansicht scrollt mit, außer man hat gerade selbst gescrollt. Ein Tipp auf eine Zeile springt dorthin. Ältere Songs werden beim ersten Öffnen im Vollbild angehört.
- **Musikvideo:** **Songtext einblenden** zeigt die gesungene Zeile groß im Band über dem Analyzer. Daneben gibt es die Zeiten als Untertiteldatei (`.srt`), die YouTube beim Hochladen zum Video nimmt.

- **Bibliothek:** Wie viel vom Text Whisper verstanden hat, steht als „Text 78 %“ beim Song, unter 50 % orange. Aus dem ganzen Song gehört fällt der Wert niedriger aus als aus der Gesangsspur; nach dem Trennen in Stems hört Whisper den Song beim nächsten Anfordern genauer. Der Filter „Text undeutlich“ und die Sortierung „Undeutlichster Text“ finden genuschelte Songs.

Ein Song braucht auf dem Mac deutlich unter einer Minute; Whisper wartet, solange ein anderes großes Modell den Speicher hat. Einrichtung: `deploy/setup-mac.sh --whisper` (oder die gespeicherten Optionen mit `--whisper` dazu), das Modell lädt beim ersten Song. Abschalten des automatischen Anhörens: `Timing:Auto` auf `false`.

## Transkription mit SheetSage2

Unter **Transkription** lässt sich eine Aufnahme hochladen (jedes Format, das macOS lesen kann, bis 300 MB). SheetSage2 macht daraus eine Melodie-Partitur im ABC-Format ohne Akkorde. **Als Partitur übernehmen** setzt sie als eigene Partitur ins Formular und stellt die Planung auf „Nur Melodie“, die Grundlage für ein Cover mit neuem Stil und Text. Jede Transkription landet als Ordner in `~/Music/YuE Studio/transcriptions` (Partitur, MIDI-Spuren, Analyse); die Liste zeigt auch die, die in YuE Studio entstanden sind.

SheetSage2 braucht eine eigene Python-Umgebung und etwa 2 GB Modelle. Installiert wird beides mit `deploy/setup-mac.sh --transcription` oder in der App YuE Studio: dort einmal **Transcribe recording** öffnen und **Install transcription support** wählen. Transkribiert wird auf der CPU, das dauert einige Minuten, immer eine Aufnahme zur Zeit. Weitere Aufnahmen warten in der Warteschlange, ebenso eine, während ein Textentwurf, eine Stimme oder das Sprachlabor den Speicher belegt; Songs laufen neben einer Transkription weiter. Umgekehrt warten Textentwurf, Stimme und Sprachlabor, bis die Transkription fertig ist, weil sie sonst den Worker samt Transkription beenden würden.

## Audio zu MIDI mit Basic Pitch

Unter der Transkription macht **Audio zu MIDI** aus einer Aufnahme eine MIDI-Datei, mit [Basic Pitch](https://github.com/spotify/basic-pitch) von Spotify. Anders als SheetSage2 schreibt es keine Partitur auf den Schlag, sondern hört jede Note mit Einsatz und Länge, so wie sie gesungen wurde. Am besten geht das mit einer Gesangsspur ohne Begleitung, etwa dem trockenen Gesang aus den Stems: Dort steht neben jeder Spur ein Knopf **Als MIDI**, der die Spur im Tempo ihres Songs (aus `score.abc`) umwandelt. Hall und Begleitung würden sonst zu Noten.

- **Einstimmig** (Standard): immer nur eine Note, wie eine Stimme singt. Basic Pitch hört mehrstimmig und meldet sonst Obertöne als eigene leise Noten.
- **Aufs 16tel-Raster**: Einsätze und Enden auf Sechzehntel des Tempos. Bei einer hochgeladenen Aufnahme muss dafür das Tempo angegeben sein; sonst wird die Datei mit 120 bpm geschrieben.
- **Pitch-Bends**: Gleiter und Vibrato als Pitch-Bends (nur einstimmig).

Die Spur heißt `Vocal`, eine Instrumentenspur aus den Stems nach ihrem Stem (`Bass`, `Drums`, …). Ein Bass-Stem wird immer einstimmig und nur im Tonbereich eines Basses (30–400 Hz) gehört. Beim Drum-Stem hört Basic Pitch keine Tonhöhen; dort sucht Tonwerk selbst die Schläge und ordnet sie nach Frequenzband: Kick, Snare und geschlossene Hi-Hat auf den General-MIDI-Noten 36, 38 und 42. Das ist eine Skizze des Grundbeats: Toms und Becken landen beim ähnlichsten Instrument, und eine Hi-Hat genau auf der Snare geht in ihr auf. Das Ergebnis lässt sich speichern oder direkt an die **Begleitstimmen** oder die **Logic-Seite** geben, die es als hochgeladene Melodie nehmen. Vibrato und Gleiter zerfallen manchmal in kurze Nachbarnoten, die man in Logic aufräumt.

Auf der **Logic-Seite** können Bass und Schlagzeug eines Songs auch direkt aus seinen Stems kommen, statt aus den Akkorden erzeugt zu werden: Unter **Aus den Stems als MIDI** gibt es dafür je ein Häkchen, sobald der Song Stems eines Modells hat, das Bass und Schlagzeug trennt (BS-RoFormer-SW, `htdemucs`, `htdemucs-ft`, `htdemucs-6s`, …; die Gesangsmodelle machen nur Gesang und Instrumental). Die Spuren landen dann im Tempo und auf dem 16tel-Raster der Partitur in Vorschau, MIDI-Datei, MusicXML und Logic-Projekt, als `Bass` und als `Drums` (oder `Kick`, `Snare`, `HiHat` mit getrennten Schlagzeugspuren, auf den Noten einer Drum-Machine, wenn eine zugeordnet ist). Gesang und Instrument kommen weiter aus der Partitur. Jeder Stem wird nur beim ersten Mal umgewandelt, danach merkt sich der Server die Datei.

Basic Pitch ist klein (ein paar MB) und läuft über ONNX Runtime auf dem Prozessor, neben YuE2 und ohne Warteschlange; ein Song dauert Sekunden. Einrichten einmal auf dem Mac mit `deploy/install-midi.sh` oder `deploy/setup-mac.sh $(cat "$HOME/Library/Application Support/YuE UI/setup-options") --midi`; die Umgebung liegt unter `~/Library/Application Support/YuE UI/midi/`.

## Songtext entwerfen mit LM Studio

YuE2 singt Texte, schreibt aber selbst keine. Über dem Songtext-Feld steht deshalb **Worum geht es?**: Stichwörter oder ein Satz genügen, **Text entwerfen** lässt ein Sprachmodell in [LM Studio](https://lmstudio.ai) auf dem Mac daraus einen Songtext im Format von YuE2 schreiben (Abschnitte wie `[Verse]` und `[Chorus]`, vier Zeilen je Abschnitt, gleichmäßige Silben). **EN** oder **DE** daneben wählt, ob er englisch oder deutsch wird; die Abschnittsmarken bleiben englisch, weil YuE2 sie so liest. Damit YuE2 einen deutschen Text auch deutsch ausspricht, gehört `German` an den Anfang des Stils (Bausteine → Sprache). Der Stil aus dem Formular geht mit, damit Stimmung und Tempo passen. Ein vorhandener Text wird erst nach einer Rückfrage ersetzt. Der Entwurf läuft auf dem Mac weiter, auch wenn das Handy zwischendurch sperrt oder die Seite neu lädt; der Text landet danach im Feld.

Passt ein Entwurf fast, muss er nicht neu gewürfelt werden: Unter dem Songtext steht ein Feld für eine kurze Anweisung wie „Refrain eingängiger“ oder „zweite Strophe trauriger“. Der Knopf daneben schickt den Text, wie er gerade im Feld steht (auch von Hand geändert), mit dieser Anweisung an dasselbe Sprachmodell; es soll nur ändern, worum es gebeten wurde, und den Rest Wort für Wort lassen. Das Ergebnis ersetzt den Text ohne Rückfrage, **Rückgängig** holt den vorherigen zurück, solange niemand am neuen etwas geändert hat.

Statt oder neben den Stichwörtern kann ein **Foto** die Vorlage sein: Die Kamera neben dem Feld nimmt eins auf oder holt es aus der Mediathek. Der Entwurf erzählt dann von dem, was darauf zu sehen ist, Stimmung, Ort, eine mögliche Geschichte; Stichwörter lenken, worauf er achtet. Der Browser verkleinert das Foto vorher auf 1024 Pixel (JPEG, auch aus HEIC), es wird nirgends gespeichert und gilt nur bis zum Neuladen der Seite. Das Modell muss Bilder lesen können, wie Gemma 4; in der Modellauswahl steht bei solchen „sieht Fotos“, und mit einem, das LM Studio als blind meldet, bleibt der Entwurf-Knopf aus.

Darunter lässt sich das Sprachmodell wählen: Die Liste zeigt alle Modelle, die in LM Studio heruntergeladen sind (ohne Embedding-Modelle), mit ihrer Größe, das eingestellte `Lyrics:Model` als „Standard“. Die Größe entspricht etwa dem Speicher, den das Modell braucht. Die Wahl merkt sich der Browser wie den Rest des Formulars; wird das Modell in LM Studio gelöscht, gilt wieder der Standard. Mit nur einem Modell bleibt die Auswahl ausgeblendet.

Zwei Modelle für einen Mac mit 32 GB sind in der Liste eigens vermerkt, sobald sie in LM Studio heruntergeladen sind: **Gemma 4 31B** als „Qualität, finale Texte“ (dicht, rund 20 GB, schreibt langsam, etwa 8 Tokens je Sekunde, aber gut, auch auf Deutsch) und **Qwen 3.6 35B A3B** als „schnell: Ideen, Reime, Varianten“ (rund 23 GB, rechnet je Token nur 3B, etwa 60 Tokens je Sekunde). Erkannt werden sie an einem Teil ihres Schlüssels (`gemma-4-31b`, `qwen3.6-35b-a3b`), jede Quantisierung zählt; die Liste steht in `Lyrics:Roles`. Auf einem kleineren Mac steht dabei „braucht 32 GB“ (der Speicher kommt vom System oder aus `Lyrics:SystemMemoryGb`), und ein Entwurf mit einem dieser Modelle geht bei automatischer Wahl des Macs nie an einen Helfer, nur von Hand.

LM Studio muss dafür nicht geöffnet sein: Antwortet sein Server nicht, startet Tonwerk ihn mit `~/.lmstudio/bin/lms daemon up` und `lms server start`. Tonwerk lädt das Modell (Standard: `google/gemma-4-26b-a4b-qat`) erst für die Anfrage, mit einem Kontext von 30000 Tokens, und entlädt es gleich danach wieder; ein Modell, das in LM Studio schon geladen ist, nutzt es mit und lässt es geladen. Dieses Modell (rund 15 GB) hat einen Mac mit 24 GB zusammen mit den üblichen offenen Apps schon bis zum Einfrieren in den Swap getrieben; vor einem Entwurf also möglichst viel schließen, oder das kleine `google/gemma-4-e4b` eintragen. Weigert sich LM Studio wegen zu wenig Speicher (seine Schutzgrenze „insufficient system resources“, etwa bei dichten Modellen wie Qwen 27B), entlädt Tonwerk zuerst andere noch geladene Modelle und versucht es dann mit halbem Kontext, bis hinunter zu `Lyrics:MinContextLength`; erst dann zeigt die Oberfläche seine Meldung. Die Schutzgrenze selbst bleibt an. Außerdem gilt: Solange YuE2 rechnet, gibt es keinen Entwurf; ein ruhender Worker wird vorher beendet; und solange ein Entwurf entsteht, startet kein Song. Der erste Entwurf dauert durch das Laden des Modells etwas länger.

Liegt ein API-Schlüssel von Anthropic bereit (derselbe wie für die Ausarbeitung, `~/.config/tonwerk/anthropic.key`), stehen am Ende der Modellauswahl auch die Claude-Modelle mit dem Vermerk „Cloud“: Opus 5.5, Fable 5.1 (das stärkste, teuerste), Sonnet 5.5 und Haiku 5.5 (schnell und billig). Claude bekommt dieselben Regeln wie die lokalen Modelle, kann Fotos lesen und überarbeiten, schreibt aber bei Anthropic: Der Entwurf wartet auf nichts und hält nichts auf, auch nicht einen Song, den YuE2 gerade rechnet, und er geht auch ohne laufendes LM Studio. Dafür verlassen Stichwörter, Stil, Foto und Text den Mac, und jeder Entwurf kostet ein wenig nach der Preisliste von Anthropic. Der Standard bleibt das lokale Modell; welche Claude-Modelle angeboten werden, steht in `Lyrics:ClaudeModels`.

## Schreiben mit einem lokalen Modell

**Werkzeuge → Schreiben** (in der App „Schreiben“) ist ein Gespräch mit einem Textmodell aus LM Studio: Titelideen, Reime und Fast-Reime, Varianten einer Zeile, Bilder für ein Thema, und auf Wunsch ein ganzer Songtext im Format von YuE2. Knöpfe für solche Anfänge stehen unter einem leeren Gespräch. Für schnelles Hin und Her passt **Qwen 3.6 35B A3B**, für den fertigen Text **Gemma 4 31B**; beide nur auf einem Mac mit 32 GB, die Wahl gilt für die nächste Antwort und merkt sich der Browser bzw. die App. Jede Antwort hat **Kopieren**, **Als Songtext** (ins Formular der Erstellen-Seite) und an der letzten **Noch einmal**. **Nachdenken** neben dem Modell sagt, wie lange ein denkendes Modell vor der Antwort überlegt: „aus“ oder „wenig“ für schnelle Ideen und Reime, „viel“ für einen sorgfältigen Text; „wie in LM Studio“ lässt es bei der Einstellung dort. Ein Modell, das sich so nicht steuern lässt, antwortet wie in LM Studio eingestellt.

Die Gespräche liegen auf dem Server (`yueui.db`, also auch im Backup), eine Antwort wächst live und läuft weiter, wenn das Handy sperrt oder die Seite neu lädt; **Anhalten** behält, was schon da ist. Es schreibt immer nur eine Antwort zugleich. Das Modell beansprucht den Speicher wie ein Textentwurf: Solange YuE2 rechnet, wartet die Antwort („wartet auf den Speicher“), und solange es schreibt, startet kein Song. Damit nicht jede Antwort das Modell neu lädt, bleibt es nach einer Antwort `Writing:KeepLoaded` (3 Minuten) geladen, nur kürzer, wenn ein Song wartet; **… entladen** gibt den Speicher sofort frei. Ein langes Gespräch wird vorn gekürzt, damit es in den Kontext passt (`Writing:ContextLength`, 32768). Nicht mit echtem LM Studio getestet.

## Ausarbeitung mit Claude oder einem lokalen Modell

Eine geschriebene Song-Ausarbeitung (Aufbau, Akkorde, Text, Ideen für die Melodie, auch als Markdown-Datei) macht **Werkzeuge → Ausarbeitung** zu einem Song. Am verlässlichsten schafft das Claude (Anthropic API). Das Modell wählt die Seite unter **Modell**: Opus 5.5 (Standard), Fable 5.1 (denkt gründlicher, kostet gut das Doppelte) oder Sonnet 5.5 (halb so teuer); der Browser merkt sich die Wahl, „Noch einmal“ nimmt das Modell des Versuchs. Darunter stehen die Modelle aus LM Studio („Auf dem Mac“, Gemma 4 31B und Qwen 3.6 zuerst): Sie bekommen dieselben Regeln und denselben Aufbau als strukturierte Ausgabe, kosten nichts und brauchen keinen Schlüssel, sind aber langsam (ein Song von hundert Takten sind rund 20000 Tokens, mit Gemma 4 31B fast eine Stunde, mit Qwen 3.6 einige Minuten; Grenze `Sketch:LocalTimeout`, 90 Minuten, Kontext `Sketch:LocalContextLength`, 32768) und verzählen sich öfter bei Takten und Silben, was Tonwerk dann korrigiert oder unter dem Plan meldet. Ein lokaler Plan wartet wie ein Textentwurf auf den Speicher („wartet auf den Speicher“), und solange er schreibt, startet kein Song. Claude antwortet mit einem Songplan: Tempo, Tonart, Stil, Abschnitte mit ihren Instrumenten und je Takt Akkorde, Gesangsmelodie und Silben. Alles Weitere rechnet Tonwerk selbst, immer gleich aus demselben Plan:

- **Als YuE-Song** füllt das Formular mit Stil, Songtext (mit `[Verse]`, `[Chorus]` …) und der Partitur im Dialekt von YuE2, Planung „voll“ und passender Tokenzahl; Partitur und Gesang stimmen damit Takt für Takt.
- **MIDI-Entwurf** lädt eine MIDI-Datei mit Gesang, Instrumentallinie, Klavier, Pad, Bass und Schlagzeug, die abschnittsweise kommen und gehen, wie der Plan es sagt.
- **Partitur** lädt die `score.abc`, **Logic-Seite** öffnet sie dort.

Ein Feld für Wünsche („Refrain höher“, „Bridge nur Klavier“) geht mit; **Neu ausarbeiten** schickt dieselbe Ausarbeitung noch einmal. Hinweise (ein Takt zu lang, ein Akkord, den YuE2 nicht kennt und der vereinfacht wurde, eine Zeile mit deutlich mehr Silben als Noten) stehen unter dem Plan. Ein Plan dauert einige Minuten und läuft auf dem Mac weiter, wenn das Handy sperrt; eine Benachrichtigung meldet, wenn er fertig ist.

Dafür braucht Tonwerk einen API-Schlüssel von Anthropic (console.anthropic.com), am einfachsten in `~/.config/tonwerk/anthropic.key` (eine Zeile, `chmod 600`), sonst in `ANTHROPIC_API_KEY` oder `Sketch:ApiKey`; die Datei wird bei jedem Plan gelesen, ein Neustart ist nicht nötig. Jeder Plan kostet nach der Preisliste von Anthropic; die Seite zeigt die verbrauchten Tokens (ein Song von hundert Takten braucht samt Denkphase grob 20000 bis 40000 Ausgabe-Tokens). Die Ausarbeitung verlässt dafür den Mac, Songs und Aufnahmen nicht.

## Als Logic-Projekt laden

Die Bibliothek zeigt bei jedem Song mit Partitur einen Knopf **Auf der Logic-Seite öffnen**. Er öffnet die Seite **Logic** mit diesem Song und wandelt ihn gleich um, sodass die Vorschau da ist und sich vor dem Herunterladen noch alles einstellen lässt. Tonwerk baut das Projekt selbst, mit der Bibliothek `YueToLogic.Core` (früher der eigene Dienst [yue-to-logic-pro](https://github.com/Marcel-B/yue-to-logic-pro)); der Browser muss die FLAC also nicht erst herunter- und wieder hochladen. Zurück kommt ein ZIP mit dem `.logicx`-Projekt: Audio auf der ersten Spur, Gesang, Instrument und Akkorde als MIDI, Tempo, Takt und Abschnitte aus der Partitur. Hinweise (etwa eine Partitur, die länger ist als das Audio) zeigt die Seite nach dem Download an; wird aus einem Song kein Projekt (eine Partitur, die sich nicht lesen lässt, Audio ohne 48 kHz), steht dort der Grund.

Hat der Song schon Stems mit Gesang (getrennt unter **Werkzeuge → Stimmen**), wählt die Logic-Seite unter **Stems im Logic-Projekt** die neuesten davon aus: Gesang und trockener Gesang kommen dann als eigene Audiospuren (48 kHz, 24 Bit WAV) neben den Mix ins Projekt, wie früher auf der Seite von yue-to-logic-pro. Andere Stems (Drums, Bass, …) haben im Logic-Template keine Spur. **Stems erzeugen** führt zur Stimmen-Seite, mit dem Song schon ausgewählt.

**Die Seite Logic.** Sie nimmt einen Song aus der Bibliothek (Knopf beim Song, oder im Menü auswählen) oder eine eigene `score.abc`, auf Wunsch mit `audio.flac`. Dort lassen sich Spuren dazuerzeugen (Akkorde mit Muster und Umkehrung, Bass, Schlagzeug, Leittöne, Verdopplung), Oktaven, Groove (Swing, Humanize), Einzähler und eine Region je Abschnitt einstellen; Einstellungen lassen sich als Voreinstellung speichern. Die Vorschau zeigt die Noten als Pianoroll und spielt sie ab, in Chrome oder Firefox auf dem Mac auch über die angeschlossenen MIDI-Geräte (Web MIDI gibt es in Chromium-Browsern und Firefox, nicht in Safari, und nur über HTTPS; Firefox fragt beim ersten Mal nach und installiert dafür ein kleines Berechtigungs-Add-on). Auf der Seite **Instrumente** (unter Werkzeuge) bekommt jedes Gerät einen Namen für seinen MIDI-Ausgang und Kanal, ein Drumcomputer dazu die Noten seiner Trommeln; auf der Seite Logic kann jede Spur eines davon spielen. Die MIDI-Datei, die Vorschau und das Logic-Projekt legen die Spur dann auf dessen Kanal, und im Projekt geht sie über Logics External Instrument an das Gerät, sofern die Vorlage den Ausgang kennt (CME WIDI, Scarlett 8i6, MIDI4x4 Out 1–4, M-VAVE FM-1). Instrumente, Zuordnungen und Voreinstellungen liegen in `yueui.db`, Handy und Mac sehen also dieselben.

**Sitzungen.** Die Seite Logic merkt sich die Arbeit an einem Song selbst: Sobald er umgewandelt ist, entsteht eine Sitzung, und jede Änderung wird eine Sekunde später gespeichert. Sie hält die Parameter (auch die Begleitstimmen), den Dateinamen, die gewählten Stems, Bass und Schlagzeug aus den Stems, Stumm und Solo sowie Klang, Instrument und Mixer jeder Spur dieses Songs; bei einer eigenen `score.abc` oder MIDI-Datei auch die Datei samt `audio.flac`, die dafür auf dem Server liegen. Öffnet man den Song wieder, in der Bibliothek, einem anderen Browser oder der App, geht es mit seiner neuesten Sitzung weiter; nach einem Neuladen öffnet die Seite die zuletzt benutzte. Oben auf der Seite stehen alle Sitzungen zum Öffnen, die offene lässt sich dort umbenennen und löschen (die Seite bleibt dann, wie sie ist). Klänge, Instrumente und Mixer gelten weiter je Spurname für alle Songs; eine Sitzung legt die ihres Songs beim Öffnen wieder darüber. Die App hat nicht alle Parameter der Webseite (etwa Swing oder Akkordumkehrung); sie lässt die, die sie nicht kennt, in der Sitzung stehen. Die Sitzungen liegen in `yueui.db`, die Dateien in `logic-sessions/`, beides geht in die Sicherung; ein gelöschter Song nimmt seine Sitzungen mit.

**Vorschau mit Aufnahme.** Hat der Song eine `audio.flac`, zeigt die Vorschau über den Spuren ihre Wellenform, am Takt ausgerichtet (mit Einzähler beginnt sie nach ihm), und spielt sie beim Abspielen mit; so hört und sieht man vor dem Export, ob Partitur und Aufnahme zusammenpassen. Ein Haken schaltet sie ab (dann wird die FLAC gar nicht erst geladen), ein Regler stellt ihre Lautstärke; beides merkt sich der Browser. Darunter zeigt ein großer Analyzer das Spektrum von allem, was im Browser klingt (Synthesizer und Aufnahme; was an MIDI-Geräte geht, hört der Browser nicht). Der Haken **Analyzer** blendet ihn aus; den Hintergrund zur Musik schaltet der Player, er bewegt sich dann auch zur Vorschau.

**MusicXML.** **MusicXML herunterladen** unter der Umwandlung liefert die Partitur mit denselben Einstellungen als `.musicxml` für MuseScore, Dorico oder eine Noten-App auf dem iPad: eine Notenzeile je Spur, die Akkordsymbole, Abschnitte und das Tempo über der ersten. Schlagzeug bleibt draußen; Noten, die über den Taktstrich oder in andere hineinreichen, werden übergebunden, die Notenprogramme ordnen sie beim Öffnen neu.

**Die Seite Instrumente.** Hier liegen die MIDI-Instrumente des Studios und die gespeicherten Browser-Klänge (Analog und FM): anlegen, bearbeiten, unter einem Namen sichern, löschen. Eine kleine Klaviatur über zwei Oktaven spielt den Klang im Editor, während er sich ändert, oder in Chrome und Firefox ein MIDI-Instrument (▶ in seiner Zeile); am Computer auch mit den Tasten A bis K wie Logics Musical Typing, Z und X wechseln die Oktave. Welche Spur was spielt, wird weiter auf der Seite Logic gewählt. Der Editor sieht aus wie das Logic-Plugin: Module mit Drehreglern (nach oben oder rechts ziehen, mit Umschalt feiner, oder Pfeiltasten), Wellenformen als Symbole, über jeder Hüllkurve ihr Verlauf, und beim FM-Synthesizer ein Bild des Algorithmus, in dem ein Tipp auf einen Operator seine Regler zeigt. Über den Reglern zeigt ein Oszilloskop die Wellenform dessen, was gerade klingt (die Klaviatur), sodass man Pulsbreite, Filter oder den Index eines Modulators auch sieht. Unter **Effekte** hat jeder Klang ein Delay (Anteil, Zeit, Feedback, Klangfarbe der Wiederholungen) und einen Hall (Anteil, Nachhall); sie gehören zum Klang, wirken in der Vorschau auf seine ganze Spur und klingen nach dem Loslassen aus. Bei 0 % sind sie aus. Ins Logic-Projekt kommen sie nicht, wie der ganze Browser-Klang.

**Klang im Browser.** In der Tabelle „Ausgänge je Spur“ der Seite Logic wählt jede Spur in einem Feld, was sie spielt: den Standardklang ihrer Art (beim Schlagzeug das Schlagzeug im Browser), einen gespeicherten Browser-Klang oder, in Chrome und Firefox, ein MIDI-Instrument; Ausgang und Kanal kommen vom Instrument und werden nicht mehr von Hand gewählt. In Safari stehen keine MIDI-Instrumente zur Wahl (ein schon zugeordnetes bleibt stehen und klingt dort im Browser). Die Klänge selbst entstehen auf der Seite **Instrumente**: der analoge Synthesizer mit zwei Oszillatoren (Wellenform, Oktave, Verstimmung, Pegel; beim Puls auch Pulsbreite und PWM aus LFO und den beiden Hüllkurven) und Rauschen, Filter (Tiefpass, Hochpass, Bandpass mit Frequenz, Resonanz, Hüllkurve, Keytracking), je einer ADSR-Hüllkurve für Lautstärke und Filter und einem LFO; oder ein **FM**-Synthesizer wie Yamahas DX- und TX-Geräte: vier Operatoren mit je Frequenzverhältnis, Verstimmung, Pegel, Anschlagsdynamik und eigener Hüllkurve, acht Algorithmen, Feedback auf Operator 4 und ein LFO auf Tonhöhe, Modulationstiefe oder Lautstärke. Eine Spur folgt ihrem gespeicherten Klang: wer ihn dort ändert, hört das auf der Seite Logic sofort, auch während der Song läuft. Die Wahl liegt je Spur (nach ihrem Namen, also für jeden Song) in `yueui.db`. Der Synthesizer ist nur zum Anhören im Browser; MIDI-Datei und Logic-Projekt bleiben davon unberührt.

**Mixer.** Unter der Tabelle steht ein Mixer mit einem Kanalzug je Spur: Lautstärke (bis +6 dB), Panorama, Mute, Solo und eine Pegelanzeige, dazu der Master. Lautstärke und Panorama gelten nur für den Ton im Browser und werden sofort hörbar; Mute und Solo entscheiden, welche Spuren überhaupt spielen, auch auf MIDI-Geräten. Ein Klick auf den Wert unter dem Regler setzt ihn zurück, einer auf die rot leuchtende Pegelspitze löscht sie. Lautstärken und Panorama liegen in `yueui.db`, Solo gilt nur bis zum Neuladen.

**Instrumente aus yue-to-logic-pro übernehmen.** Solange der alte Server noch läuft, holt dieser Befehl (etwa auf dem Proxmox-Host, der beide erreicht) seine Instrumente, Zuordnungen und Voreinstellungen und gibt sie Tonwerk; zweimal ausgeführt ändert er nichts:

```sh
Y=http://192.168.2.73:8080/api
{ printf '{"instruments":'; curl -s $Y/instruments; printf ',"assignments":'; curl -s $Y/instruments/assignments; printf ',"presets":'; curl -s $Y/presets; printf '}'; } > ytl.json
curl -s -X POST -H 'Content-Type: application/json' --data @ytl.json https://<mac>.<tailnet>.ts.net:8443/api/logic/import
```

**Begleitstimmen.** Das Werkzeug **Begleitstimmen** (unter Werkzeuge) nimmt eine Melodie als MIDI-Datei (oder `score.abc`, oder einen Song der Bibliothek) und gibt eine MIDI-Datei mit einer Spur je Stimme zurück, nach einer Vorschau zum Anhören; mit der Logic-Vorlage hat es nichts zu tun. Dieselben Stimmen gibt es unter „Begleitstimmen“ auf der Seite Logic, zusammen mit allen anderen Optionen. Es entstehen aus der Gesangsmelodie weitere Stimmen, jede auf einer eigenen Spur: Terz darüber, Terz darunter, Sexte darunter (parallel in der Tonleiter, also mal große, mal kleine Terz; wo die Melodie auf einem Akkordton steht und die Parallele aus dem Akkord fiele, nimmt die Stimme den nächsten Akkordton) sowie Alt, Tenor und Bass eines vierstimmigen Chorsatzes mit der Melodie als Sopran (Akkordtöne darunter, möglichst kleine Schritte, Terz nicht weglassen, keine Quint- und Oktavparallelen) und ein **Bordun** (Drone): der Grundton der Tonart unter jeder Phrase, im Rhythmus der Melodie oder als „Bordun liegend“ gehalten. Alle anderen singen im Rhythmus der Melodie. Die Akkorde kommen aus der Partitur; hat sie keine, rät Tonwerk sie je halbem Takt aus der Melodie. Die Tonart ist die der Partitur, solange die Melodie dazu passt, sonst die, nach der die Melodie klingt, oder die im Feld „Tonart“ gewählte. „Nur im Refrain“ beschränkt das auf Abschnitte namens `chorus` oder `Refrain`. Statt einer `score.abc` lässt sich auch eine **MIDI-Datei** hochladen, etwa eine Melodie aus Logic (eine unbenannte Spur gilt als Gesang); nach „Konvertieren“ lädt der Knopf der MIDI-Datei die Stimmen herunter, und die Vorschau spielt sie. Im Logic-Projekt hat jede Stimme ihre eigene Spur (`Harmony 3rd up` bis `Harmony Drone held`). Gesungen werden sie (noch) nicht.

**Zurück aus Logic.** Wer den Song in Logic weiterbearbeitet hat (Melodie, Akkorde, Tempo), wählt dort alle MIDI-Regionen aus und exportiert sie als MIDI-Datei (*Ablage → Exportieren → Auswahl als MIDI-Datei*); die Spurnamen `Vocal`, `Ins` und `Chords` müssen bleiben, an ihnen erkennt Tonwerk die Stimmen. Der Knopf **MIDI aus Logic als Partitur** beim Song macht daraus wieder eine `score.abc`; die steht dann mit Stil, Text und Seed des Songs im Formular, bereit für einen neuen Song. Dasselbe geht ohne Song über den Knopf neben dem Partiturfeld unter „Erweiterte Parameter“, etwa für eine Melodie, die in Logic entstanden ist. Spuren, die sich nicht zuordnen lassen, nennt die Oberfläche als Hinweis.

**Tonart ändern.** Ist der Gesang zu hoch oder zu tief, verschieben **−** und **+** unter dem Partiturfeld die ganze Partitur um je einen Halbton: Melodie, Begleitung, Akkordsymbole und Tonart (`K:`), in der neuen Tonart neu notiert. Daneben lässt sich die Tonart auch direkt wählen; die Partitur wandert dann auf dem kürzeren Weg dorthin (höchstens sechs Halbtöne tiefer oder fünf höher), − und + gehen von da aus weiter. Die Auswahl hat alle Dur- und Molltonarten: Wer von D-Dur nach d-Moll (oder umgekehrt) wechselt, bekommt die Noten umgeschrieben, Terz, Sexte und Septime einen Halbton tiefer (natürliches Moll), die Akkorde passend dazu (C wird Cm, G7 wird Gm7). Das ist eine einfache Näherung, klingt aber nach Moll. Der Umfang der Stimme `Vocal` steht dabei (etwa „Gesang A3–A4“). Ein Song mit anderer Tonart klingt auch mit gleichem Seed anders, denn die Partitur ist Teil der Eingabe. YuE2 singt diese Stimme in der notierten Lage; wie tief eine Stimme klingen kann, hängt aber auch vom Stil ab („male vocal“, „deep voice“). Das Transponieren passiert im Browser und braucht keinen Server.

## Mit anderer Stimme singen

YuE2 singt mit einer Stimme, die es selbst wählt. Mit Stem-Trennung (mlx-audio-separator) und [Seed-VC](https://github.com/Plachtaa/seed-vc), die Tonwerk selbst startet, singt es einen fertigen Song mit einer Referenzstimme neu:

1. mlx-audio-separator trennt `audio.flac` in trockenen Gesang, Hall und Instrumental.
2. Seed-VC singt den trockenen Gesang mit der gewählten Stimme neu, auf Wunsch eine Oktave höher oder tiefer.
3. ffmpeg mischt die neue Stimme auf den Pegel der alten zurück unter das Instrumental, auf Wunsch mit dem Hall des Originals.

Das Ergebnis ist eine **Fassung** des Songs: eine FLAC im Datenordner von Tonwerk (`versions/` neben `yueui.db`), nicht im Song-Ordner von YuE Studio. Die Bibliothek zeigt die Fassungen unter dem Song, zum Abspielen, Herunterladen und Löschen; wird der Song gelöscht, gehen seine Fassungen mit. Gestartet wird sie über den Knopf **Mit Stimme singen** beim Song (Stimme, Tonlage, Stärke der Stimme, Qualität, Hall). Ist sie fertig, kommt eine Benachrichtigung.

Soll ein neuer Song gleich mit einer Stimme gesungen werden, wählt man sie schon im Formular unter **Danach mit Stimme singen** (mit Tonlage, Stärke und Qualität; der Hall des Originals bleibt wie im Dialog). Jeder Song des Laufs kommt dann, sobald er fertig ist, von selbst als Fassung in die Reihe; die Warteschlange zeigt die Stimme am Auftrag und am Song. Eine Neuberechnung (etwa ein Entwurf in voller Qualität) macht keine zweite Fassung.

Die Seite **Stimmen** zeigt die Referenzstimmen: anhören, löschen und neue Aufnahmen hochladen. Am besten 10 bis 25 Sekunden trockener Gesang ohne Musik; Tonwerk behält höchstens 25 Sekunden ab dem gewählten Anfang, kürzer als 3 Sekunden nimmt es nicht. Die Stimmen liegen in `voices/` neben der Datenbank; die Sammlung von ChangeMyVoice übernimmt Tonwerk einmal mit ihren Kennungen, solange dort noch eine erreichbar ist (danach gelöschte bleiben gelöscht). Darunter stehen die Fassungen, die gerade entstehen.

**Stimmenspur tauschen** singt eine eigene Aufnahme mit einer gespeicherten Stimme neu: Datei hochladen (WAV, MP3, FLAC, M4A, OGG, bis 300 MB), Stimme wählen, sagen, ob die Datei **nur Gesang** ist (Standard; geht direkt an Seed-VC) oder ein **ganzer Song** (dann wird der Gesang erst abgetrennt und die neue Stimme wieder unter das Instrumental gemischt, wahlweise mit dem Hall des Originals; braucht die Stem-Trennung), dazu Tonlage, Stärke und Qualität wie bei den Fassungen, **Stimme tauschen**. Das wartet wie eine Fassung auf den Speicher und meldet sich per Benachrichtigung. Danach lassen sich Ergebnis und Original abwechselnd anhören (die Zeit läuft beim Wechsel weiter) und das Ergebnis als FLAC herunterladen. Upload und Ergebnis liegen in `swaps/` neben der Datenbank und gehen mit ins Backup.

Darunter trennt **Stems** einen Song in seine Spuren: Song und Modell wählen (die 27 Modelle aus dem Katalog von StemMyWav; jedes lädt beim ersten Mal herunter), auf Wunsch den Hall vom Gesang trennen, **Trennen**. Das wartet wie eine Fassung, bis YuE2 und das Textmodell den Speicher freigeben, und meldet sich per Benachrichtigung. Jede Spur lässt sich mit ihrer Wellenform anhören (ein Tipp in die Wellenform springt dorthin, beim Wechsel zu einer anderen Spur desselben Songs läuft die Zeit weiter) und als FLAC herunterladen. Die Dateien liegen in `stems/` neben der Datenbank, nicht im Songordner, und gehen mit dem Song. In der Bibliothek führt **In Stems trennen** beim Song direkt dorthin.

Statt eines Songs lässt sich unter **Eigene Datei** auch eine Aufnahme von woanders trennen (WAV, MP3, FLAC, M4A, alles, was ffmpeg liest). Sie wartet in der Warteschlange wie ein Song und wird gelöscht, sobald ihre Spuren gespeichert sind. Für **Als MIDI** hat eine eigene Datei keine Partitur mit Tempo; das gibt man unter **Tempo eigener Dateien** an, sonst wird sie mit 120 bpm und ohne Raster geschrieben.

Ein Song braucht grob 10 bis 20 Minuten. Der Mac hat nicht genug Speicher für YuE2, Stem-Trennung und Seed-VC zugleich, deshalb geht immer nur eine Fassung, und erst wenn YuE2 nichts rechnet und kein Textentwurf läuft; ein untätiger YuE-Worker wird vorher beendet. Solange eine Fassung entsteht, warten neue Songs, Neuberechnungen und Textentwürfe in der Warteschlange.

**Einrichtung.** `deploy/setup-mac.sh --voices` (siehe oben). Solange die Programme fehlen, nutzt Tonwerk wie bisher die APIs von ChangeMyVoice und StemMyWav, falls eingerichtet: ChangeMyVoice braucht einen eigenen Schlüssel für Tonwerk (`scripts/neuer-zugang.sh` im ChangeMyVoice-Repository) und muss Anfragen von der Adresse zulassen, von der Tonwerk kommt (bei `Voice:BaseUrl` über die Tailscale-Adresse des Macs ist es diese). Den Schlüssel liest Tonwerk aus `Voice:ApiKey` oder einer Datei (`Voice:ApiKeyFile`). Der Schlüssel für StemMyWav liegt schon in `~/.config/stemmywav/mac-api-key`, dort sucht Tonwerk ihn von selbst. Die Tonlage braucht ChangeMyVoice mit Halbton-Versatz (`setup-inference.sh --with-f0`). Ohne Seed-VC und ChangeMyVoice gibt es keinen Knopf zum Singen, ohne Trennung und Stimmen auch die Seite nicht.

## Sprachlabor

YuE2 kann nur singen. Das **Sprachlabor** probiert aus, welches lokale Sprachmodell (Text-to-Speech über [mlx-audio](https://github.com/Blaizzy/mlx-audio)) einen Text natürlich spricht, etwa für einen Podcast:

1. Unter **Stimme** einen Satz vorlesen und aufnehmen (oder eine Datei wählen) und speichern. Der Wortlaut steht im Feld darüber, denn mehrere Modelle brauchen ihn. Der Server schneidet die Stille davor und danach ab und behält höchstens 30 Sekunden; 5 bis 15 sind am besten.
2. Einen Text eingeben, ein oder mehrere Modelle ankreuzen und **Sprechen**. Jedes Modell spricht den Text nacheinander mit der gewählten Stimme oder, mit **Stimme des Modells**, mit einer eigenen.
3. Die Ergebnisse stehen darunter zum Anhören und Herunterladen (WAV), mit Ladezeit, Sprechzeit und Speicherbedarf zum Vergleichen.

Angeboten werden Chatterbox Multilingual v3 (MIT), Qwen3-TTS 1.7B (Apache 2.0), Higgs Audio v2 (Apache 2.0), Higgs Audio v3 (nur für Forschung und nicht-kommerzielle Nutzung; Podcasts erlaubt, wenn Boson AI genannt wird) und MOSS-TTS Local v1.5 (Apache 2.0). Higgs Audio v3 steht zweimal in der Liste: einmal mit den Werten seiner Modellkarte (Temperatur 0.8, top-k 50) und einmal vorsichtiger (0.7, top-k 20), wobei jedes Stück eines langen Textes neben der Aufnahme das vorige als zweite Referenz bekommt, um nebeneinander zu hören, welches weniger sirrt und die Stimme ruhiger hält. Alle können Deutsch und klonen Stimmen. Die Liste lässt sich unter `Speech:Models` ersetzen (je Modell `id`, `label`, `repo`, `langCode`, `options`, `chunkCharacters` usw., siehe `Speech/SpeechModels.cs`). Jedes Modell sampelt mit den Werten, die seine Entwickler empfehlen, sofern `options` keine anderen nennt.

Chatterbox und die beiden Higgs-Modelle sprechen am Stück höchstens etwa 40 Sekunden und kommen bei langen Absätzen ins Rauschen. Ihnen gibt das Labor einen längeren Text in Stücken von höchstens 300 Zeichen, an Satzenden getrennt, und fügt die Stücke mit einer kurzen Pause zusammen. Eine Zeile ohne Satzzeichen am Ende, etwa eine Überschrift, bekommt dabei einen Punkt, sonst lassen die Modelle sie aus. Ohne gewählte Stimme klonen die Stücke nach dem ersten dessen Stimme, damit nicht jedes Stück anders klingt.

Ein Modell wird für jedes Ergebnis geladen und danach wieder freigegeben. Es wartet wie eine Fassung, bis YuE2, das Textmodell und Stimmumwandlungen den Speicher freigeben, und so lange warten diese. Die Seite **Warteschlange** listet laufende und wartende Sprachtests unter **Sprachlabor** (mit Abbrechen) und zeigt dann eine vierte Kachel **Sprache**. Beim ersten Mal lädt sich ein Modell herunter (3 bis 9 GB), das dauert.

### Sprache säubern

Unten im Sprachlabor nimmt **Sprache säubern** einer Aufnahme von Sprache (Vortrag, Interview, Sprachnotiz, auch ein Video, dann nur der Ton) Rauschen und Störgeräusche. Datei wählen, Modell wählen, **Säubern**. Das Ergebnis steht darunter zum Anhören und Herunterladen (WAV, 48 kHz mono, `<Name>-clean.wav`); **Original anhören** spielt die Aufnahme, wie sie war. Zur Wahl stehen zwei Modelle zur Sprachverbesserung aus mlx-audio:

- **MossFormer2 SE 48K** (Apache 2.0, aus Alibabas ClearerVoice-Studio, etwa 250 MB): nimmt Rauschen und viel vom Raumklang gründlich weg.
- **DeepFilterNet 3** (MIT/Apache 2.0, wenige MB): klein, schnell und sanft; nimmt gleichmäßiges Rauschen weg und lässt die Stimme mit ihrem Hall.

Eine Aufnahme darf bis zu 2 Stunden lang und 1 GB groß sein. Sie wartet in derselben Reihe wie die Sprachtests und belegt solange den Speicher; der Fortschritt steht in Prozent dabei und in der Warteschlange. Andere Kandidaten der Recherche (Oktober 2026) blieben draußen: Resemble Enhance erfindet beim Säubern Wörter hinzu, NVIDIAs RE-USE ist nicht kommerziell und braucht CUDA, Sidon gibt es nur als CoreML in einem Swift-Paket. Die Gesangstrennung der Stems ist für Musik trainiert und lässt Sprache dumpfer. Die Liste lässt sich unter `Speech:CleanModels` ersetzen. Auf dem Mac noch nicht gehört.

**Einrichtung.** Das Labor braucht eine eigene Python-Umgebung mit mlx-audio, getrennt von der von YuE Studio. Dazu kommt PyTorch (etwa 1 GB), das nur Higgs Audio v3 braucht: Es liest seinen Audio-Codec über PyTorch. Fehlt ein Paket, sagt der Sprachtest das und nennt das Skript, das es nachinstalliert. Einmal auf dem Mac im Repository ausführen, nachdem `deploy/install.sh` gelaufen ist:

```sh
deploy/install-speech.sh          # Umgebung anlegen bzw. mlx-audio aktualisieren und Fehlendes nachinstallieren
deploy/install-speech.sh --test   # danach jedes Modell einen Testsatz sprechen lassen; lädt alle herunter (etwa 28 GB)
```

Umgebung und Modelle liegen unter `~/Library/Application Support/YuE UI/speech/` (`env/`, `models/`), Aufnahmen und Ergebnisse daneben in `speech/voices/` und `speech/takes/`. Das Skript nimmt `uv`, wenn es installiert ist, sonst ein Python ab 3.10 (das von macOS ist 3.9, dann `brew install python@3.12`). Für die Aufnahmen braucht der Server `ffmpeg`.

## Singen mit SoulX-Singer

Noten und Text rein, Gesang raus: Die Seite **Singen** (nur in der App, unter **Werkzeuge**) lässt eine Melodie von [SoulX-Singer](https://github.com/Soul-AILab/SoulX-Singer) (Apache 2.0) mit Worten singen, ohne Begleitung.

- **Noten-Editor:** Die Melodie liegt in einer Pianorolle. Ein Tipp auf eine freie Stelle setzt eine Note (auf das gewählte Raster), Ziehen verschiebt sie, ihr rechter Rand ändert die Länge; Tempo, Raster und Zoom stehen darüber, **Rückgängig** und **Vorhören** (mit Tonwerks Synthesizer) auch. Eine gewählte Note bekommt unten ihre Silbe; tippt man mehrere Wörter, verteilen sie sich auf die folgenden Noten. Über jeder Note steht, wie SoulX-Singer das Wort ausspricht (ARPAbet aus dem Wörterbuch).
- **Gezogene Noten (Melisma):** **Gezogen** (oder ein `-` als Silbe) singt die Silbe davor auf dieser Note weiter, nur mit neuer Tonhöhe; so läuft eine Silbe über mehrere Noten, im Editor als verbundene Noten mit „–“. SoulX-Singer kennt dafür einen eigenen Notentyp. Eine Note ohne Silbe bleibt still. Die gezeichnete Tonhöhenkurve zwischen den Noten (Vibrato, Gleiten) macht das Modell selbst, sie lässt sich nicht zeichnen.
- **Melodie laden:** eine MIDI-Datei, eine `score.abc`, ein Song der Bibliothek mit Partitur oder ein früheres Ergebnis (**In den Editor**). Aus einer MIDI-Datei nimmt der Server die Spur, die nach Gesang heißt (`Vocal`, `Lead`, `Melodie`, `Gesang` …), sonst die mit Liedtext-Ereignissen, sonst die mit den meisten Noten; Schlagzeug (Kanal 10) und die Begleitstimmen (`Harmony …`) nie. Aus einer Partitur die Gesangsstimme. Liedtext-Ereignisse landen auf ihren Noten (`-`, `+`, `~`, `_` als gezogene Noten, wie Vocaloid, Synthesizer V oder ACE Studio sie schreiben). **Als MIDI-Datei sichern** schreibt die Melodie mit ihren Silben als Liedtext, für Logic oder zum späteren Laden.
- **Worte verteilen:** **Auf die Noten verteilen** legt einen Text neu auf alle Noten: ein Wort auf so viele Noten, wie es nach dem Wörterbuch Silben hat. Marken wie `[Verse]` werden übersprungen, Bindestriche legen die Silben selbst fest (`to-mor-row`), ein allein stehendes `-` zieht das vorige Wort über die nächste Note. Ein Bibliothekssong ohne Liedtext-Ereignisse bekommt so beim Laden seinen Songtext. Übrige Wörter werden gezählt.
- **Stimme:** SoulX-Singers zwei Beispielstimmen (Englisch, Mandarin) und eigene Stimmen (siehe unten). Das Ergebnis lässt sich außerdem über **Mit anderer Stimme singen** umfärben.
- **Tonhöhe:** wie geschrieben, um bis zu zwei Oktaven verschoben oder automatisch in die Lage der Stimme.

Die Aussprache kommt aus dem englischen Wörterbuch (CMUdict über g2p_en). Deutsche Texte werden gesungen, aber mit englischer Aussprache; Umlaute werden umschrieben. Pausen über 0,2 s werden zu Stille, kürzere verlängern die Note davor, wie SoulX-Singers eigene MIDI-Übernahme es macht.

**Eigene Stimmen.** Unter **Eigene Stimmen** wird eine Aufnahme des eigenen Gesangs (in der App aufgenommen, bis 30 s, oder eine WAV, M4A, MP3 … gewählt) mit dem gesungenen Text zu einer Stimme. SoulX-Singer klont eine Stimme aus wenigen Sekunden Gesang, braucht dazu aber, was darin wann gesungen wird. Der Server schneidet die Stille ab, hört die Noten mit pYIN heraus (librosa, aus SoulX-Singers Umgebung), legt die Silben des Textes darauf (mehr Noten als Silben: die Note mit dem kleinsten Schritt zur vorigen wird gezogen) und behält die erste Phrase von mindestens 3 und höchstens 12 Sekunden als Stimme (WAV und SoulX-Metadaten in `singing/voices/` neben der Datenbank, in der Nextcloud-Sicherung). Die Antwort sagt, welcher Text behalten wurde und ob Silben ohne Note übrig blieben. Das Modell wird dafür nicht geladen; der Server muss SoulX-Singer aber installiert haben. Singt ein anderer Mac, geht die Stimme mit dem Auftrag dorthin.

So singt man am besten ein (nach SoulX-Singers Paper und Beispielen, die 5 bis 10 s lang sind; trainiert wurde auf 2 bis 16 s, als Vorlage dient dort der erste Satz):

- eine Phrase von 6 bis 12 Sekunden am Stück, in der Lage und dem Stil, in dem die Stimme später singen soll; die Stimme übernimmt Klangfarbe und Art;
- trockener, ruhiger Raum ohne Hall, ohne Musik im Hintergrund, gleicher Abstand zum Mikrofon (etwa eine Handbreit), nicht übersteuern;
- gerade Töne, eine Silbe pro Ton, wenig Vibrato und keine langen Schleifer, Silben deutlich absetzen, damit die Noten richtig erkannt werden;
- ein englischer Text, genau so eingetippt, wie er gesungen wird. Vorgeschlagen ist „Twinkle, twinkle, little star, how I wonder what you are“ (gemeinfrei, eine Silbe pro Ton); jeder andere geht auch.

Ein Ergebnis wartet wie die Sprachtests, bis YuE2 und die anderen großen Modelle den Speicher freigeben (das Modell braucht mit PyTorch auf Metal einige GB), und so lange warten diese. Die **Warteschlange** zeigt es unter **Singen** und mit einer Kachel **Gesang**; eine Benachrichtigung meldet fertige. Ergebnisse sind WAV (24 kHz mono) in `singing/` neben der Datenbank.

**Einrichtung.** Einmal auf dem Mac im Repository, nach `deploy/install.sh` (oder über das Einrichtungsskript mit `--singing`, dann pflegen Updates es mit):

```sh
deploy/install-singing.sh   # SoulX-Singer (Commit fest im Skript), PyTorch und Pakete, Gewichte (etwa 2,8 GB), CMUdict
```

Alles liegt unter `~/Library/Application Support/YuE UI/singing/` (`env/`, `src/`, `models/`, `nltk_data/`). Im Linux-Container mit zufälligen Gewichten auf der CPU getestet, nicht auf dem Mac und nicht mit den echten Gewichten.

## Datensicherung in die Nextcloud

Tonwerk sichert sich jede Nacht um 3 Uhr selbst in eine Nextcloud (über WebDAV, also auch in jeden anderen WebDAV-Speicher), nach dem Vorbild von module-o-mat. Hochgeladen werden:

- `tonwerk-mon.zip` bis `tonwerk-sun.zip`: eine Kopie von `yueui.db` (Playlists, Titel, Bewertungen, Warteschlange, Instrumente, Klänge, …) und `push.json`, je Wochentag eine, die Nextcloud hat also die letzten sieben Tage. Die Datenbank wird mit SQLites eigener Sicherung kopiert, auch wenn Tonwerk gerade schreibt.
- die Dateien daneben, jede nur einmal und danach nur, wenn sie sich ändert: die Songs (`songs/<Lauf>/songN/` mit FLAC, Partitur und `request.json`), Fassungen (`versions/`), Stems (`stems/`), getauschte Stimmenspuren (`swaps/`), Cover (`covers/`), die Stimmen und Ergebnisse des Sprachlabors (`speech/voices/`, `speech/takes/`) und Transkriptionen (`transcriptions/`). Was schon oben ist, merkt sich Tonwerk in `backup.json` neben `yueui.db`. Die erste Sicherung dauert deshalb lange, die folgenden nur so lange wie die neuen Songs.

In der Nextcloud wird nichts gelöscht: Ein versehentlich gelöschter Song liegt dort weiter. Nicht gesichert werden die Streaming-Kopien (entstehen neu aus der FLAC), Python und Modelle des Sprachlabors (lassen sich neu laden) und Songs, an denen der Worker gerade rechnet; die nimmt die nächste Sicherung mit.

**Einrichten.** In der Nextcloud unter *Einstellungen → Sicherheit → Geräte & Sitzungen* ein App-Passwort anlegen und die WebDAV-Adresse ablesen (*Dateien → Dateieinstellungen → WebDAV*). Adresse, Benutzer und Passwort stehen nirgends im Repository und nicht in den appsettings, sondern in einer `.env`-Datei auf dem Mac, mit denselben Namen wie bei module-o-mat: `~/.config/tonwerk/nextcloud.env`

```sh
NEXTCLOUD_WEBDAV_URL=https://cloud.example.de/remote.php/dav/files/marcel/Tonwerk
NEXTCLOUD_USERNAME=marcel
NEXTCLOUD_APP_PASSWORD=xxxxx-xxxxx-xxxxx-xxxxx-xxxxx
```

Die Datei sollte nur der eigene Benutzer lesen dürfen (`chmod 600 ~/.config/tonwerk/nextcloud.env`). Tonwerk liest sie bei jeder Sicherung neu, ein Neustart ist nicht nötig. Gleichnamige Umgebungsvariablen gehen der Datei vor.

Der Ordner `Tonwerk` entsteht von selbst, der darüber muss existieren. Eine Adresse im Heimnetz (`192.168.…`) erreicht der LaunchAgent nicht (macOS fragt dafür nach „Lokales Netzwerk“, was über SSH nicht geht); die Tailscale-Adresse oder die öffentliche Adresse der Nextcloud funktionieren.

Danach zeigt das Zahnrad-Menü, wann zuletzt gesichert wurde, und **Jetzt in die Nextcloud sichern** startet eine Sicherung sofort. Schlägt eine fehl, kommt eine Benachrichtigung; der Eintrag im Menü sagt dann warum. Eine fehlgeschlagene nächtliche Sicherung versucht es nach 30 Minuten noch einmal. Hält die Nextcloud eine Datei noch gesperrt (HTTP 423, etwa nach einem Upload, den ein Neustart abgebrochen hat; die Sperre fällt nach spätestens einer Stunde), lässt Tonwerk nur diese Datei für die nächste Sicherung liegen und sichert den Rest weiter.

**Zurückholen.** Tonwerk stoppen (`launchctl bootout gui/$(id -u)/de.bvelop.yueui` oder `deploy/uninstall.sh`), `yueui.db` und `push.json` aus dem ZIP nach `~/Library/Application Support/YuE UI/` legen, die Ordner `versions`, `stems`, `swaps`, `covers` und `speech` ebenfalls dorthin, den Inhalt von `songs/` und `transcriptions/` nach `~/Music/YuE Studio/`, dann wieder starten (`deploy/install.sh`).

## Protokoll

**Werkzeuge → Protokoll** zeigt, was Tonwerk gemeldet hat, ohne SSH auf dem Mac: Server, YuE2-Worker (auch seine Python-Ausgabe), Warteschlange, Songtexte, Stimmen und Stems (mit der Ausgabe von Separator und Seed-VC, wenn sie scheitern), Sprachlabor, Export, Logic, Backup, Benachrichtigungen und die Updates aus `deploy/update.sh`. Die Seite filtert nach Quelle, Stufe (anfangs nur Warnungen und Fehler) und Text; mehrzeilige Meldungen und Stacktraces klappen unter **Details** auf.

**Für Claude kopieren** legt einen Bericht in die Zwischenablage: Version mit Commit, zuletzt deployter Commit, Zustand des Workers, welche Engines installiert sind, was gerade läuft, und die Warnungen und Fehler der letzten 24 Stunden mit den Zeilen davor. Er enthält keine Einstellungen, Schlüssel oder Adressen. In einen Chat eingefügt, reicht das meistens, um einem Fehler nachzugehen.

Die Zeilen liegen als JSON, eine Datei pro Tag (`tonwerk-JJJJ-MM-TT.jsonl`), unter `~/Library/Application Support/YuE UI/logs` und werden nach 14 Tagen gelöscht. Wird eine Tagesdatei größer als 50 MB, kommen nur noch Warnungen und Fehler hinein. Das Update-Log bleibt, wo `update.sh` es schreibt, und wird mitgelesen. `yueui.log` (die Konsole des LaunchAgents) gibt es weiterhin.

## Monitoring mit Grafana

Für Verläufe über Tage und Wochen (wie lange ein Song dauert, was fehlschlägt, wer den Speicher hält, wie voll die Platte wird) liefert Tonwerk Messwerte und Protokoll an einen Monitoring-Stack aus Prometheus, Loki und Grafana. Der läuft **nicht auf dem Mac**, dessen Speicher den Modellen gehört, sondern in einem Container auf einer anderen Maschine, etwa einem LXC auf dem Proxmox, über Tailscale verbunden. Vorlage und Anleitung: [`deploy/monitoring/`](deploy/monitoring/README.md).

- **Messwerte:** `GET /metrics` im Format von Prometheus, ohne eigene Einrichtung (ab `Monitoring:Metrics` = `false` aus). Prometheus holt sie über `tailscale serve` von `https://<Mac>.<tailnet>.ts.net:8443/metrics`. Darin: Zustand des Workers, welches Modell den Speicher hält, Warteschlange, laufende und wartende Aufträge je Art, fertige Aufträge nach Ergebnis (`done`, `failed`, `cancelled`) mit ihrer Arbeitszeit ohne die Wartezeit, Protokollzeilen nach Quelle und Stufe, freier Platz und der Speicher des Server-Prozesses. Die Zähler beginnen nach jedem Neustart bei null; Prometheus rechnet das heraus.
- **Protokoll:** Mit `Monitoring:Loki:Url` schickt Tonwerk jede Zeile des Protokolls (ab `info`, siehe `Monitoring:Loki:MinLevel`) gebündelt an Loki, mit den Labels `app`, `host`, `source` und `level`. Ist Loki nicht erreichbar, wartet Tonwerk ein paar Versuche lang und verwirft dann; auf dem Mac bleibt alles in den Tagesdateien. Das Log von `update.sh` geht nicht mit.

In `~/Library/Application Support/YueUI/app/appsettings.Production.json` auf dem Mac (Tailscale-Name oder 100.x-Adresse; eine Adresse im Heimnetz erreicht der LaunchAgent nicht):

```json
{
  "Monitoring": {
    "Loki": { "Url": "http://monitoring:3100", "Host": "mac-mini" }
  }
}
```

Danach Tonwerk neu starten (`launchctl kickstart -k gui/$(id -u)/de.bvelop.yueui`).

## Cover malen

In den Songs malt **Cover malen** (im „…“-Menü des Songs) ein Cover mit einem Bildmodell auf dem Mac, ohne ComfyUI: FLUX.2 Klein, Z-Image Turbo und Krea 2 Turbo über [mflux](https://github.com/mflux-community/mflux) (MLX), FLUX 3 Image Klein über [flux3-mlx](https://github.com/felix-ab/flux3-mlx) (MLX, mit dem Prompt-Upsampler von Black Forest Labs, den mflux nicht hat):

1. Die Bildbeschreibung ist aus Titel und Stil vorgeschlagen und frei änderbar, am besten auf Englisch. **Titel ins Bild schreiben** bittet das Modell, den Titel einmal ins Bild zu setzen; sonst kommt der Titel im Vorschlag gar nicht vor. Einen Negativ-Prompt kennen die Modelle nicht, und „no text“ oder „no logo“ in der Beschreibung bringen Text und Logos eher hinein, weil das Modell malt, was genannt wird. Deshalb beschreibt der Vorschlag nur, was zu sehen sein soll (ein Gemälde statt „Albumcover“), ohne Tempo- und Gesangsangaben aus dem Stil.
2. Modell wählen und **Malen**. Jedes Bild ist ein Vorschlag mit eigenem Seed, quadratisch mit 1024 Pixeln, und steht darunter.
3. **Als Cover** macht einen Vorschlag zum Cover des Songs, wie ein gewähltes Foto: in der Bibliothek, im Player, in jedem Export und im Musikvideo. Die Vorschläge bleiben, bis man sie löscht, so lässt sich zurückwechseln.

| Modell | Lizenz | Download | Speicher beim Malen |
|---|---|---|---|
| FLUX.2 Klein 4B (Standard, 8 Bit) | Apache 2.0, auch für veröffentlichte Songs | etwa 15 GB | etwa 9 GB |
| FLUX.2 Klein 9B (4 Bit) | FLUX Non-Commercial, nur privat | etwa 32 GB | etwa 10 GB |
| Z-Image Turbo (8 Bit, 9 Schritte) | Apache 2.0, auch für veröffentlichte Songs | etwa 21 GB | etwa 11 GB |
| Krea 2 Turbo (8 Bit, 8 Schritte), nur ab 32 GB | Krea 2 Community, kommerziell bis 1 Mio. $ Umsatz | etwa 35 GB | etwa 22 GB |
| FLUX 3 Klein (8 Bit, 16 Schritte, mit Upsampler), nur ab 32 GB | FLUX Kommunity, kommerziell bis 5 Mio. $ Umsatz | etwa 15 GB | etwa 12 GB |

Die Speicherwerte sind geschätzt, nicht gemessen. Krea 2 und FLUX 3 bietet nur ein Mac mit mindestens 32 GB an (`Images:SystemMemoryGb` übersteuert, was der Mac meldet); ein Helfer mit weniger bekommt sie auch automatisch nie. Z-Image Turbo, Krea 2 und FLUX 3 können kurze Wörter ins Bild schreiben (**Titel ins Bild schreiben**), Klein nicht. Krea 2s Lizenz verlangt einen Inhaltsfilter, die von FLUX 3 und FLUX.2 Klein 9B einen Filter oder eine Prüfung jedes Bildes vor der Veröffentlichung, dazu einen Hinweis auf KI, wo ein Gesetz ihn verlangt: Jedes Bild dieser drei geht durch den NSFW-Klassifikator [Falconsai/nsfw_image_detection](https://huggingface.co/Falconsai/nsfw_image_detection), ein angeschlagenes wird nicht behalten. FLUX 3 Klein läuft immer mit dem Upsampler, der die Beschreibung ausformuliert (so verlangt es Black Forest Labs); was er schrieb, steht unter dem Bild. Ein Bild wartet wie ein Song, bis YuE2, das Textmodell, Stimmumwandlungen und das Sprachlabor den Speicher freigeben, und so lange warten diese. Die **Warteschlange** listet Bilder in Arbeit unter **Cover**. Das Modell wird für jedes Bild geladen und danach freigegeben; beim ersten Bild lädt es sich herunter.

**Einrichtung.** Einmal auf dem Mac im Repository, nach `deploy/install.sh`:

```sh
deploy/install-images.sh   # je eine Python-Umgebung mit mflux und mit flux3-mlx (Versionen fest im Skript)
```

Oder mit den bisherigen Optionen über das Einrichtungsskript, damit Updates die Umgebung mitpflegen: `deploy/setup-mac.sh $(cat "$HOME/Library/Application Support/YuE UI/setup-options") --images`. Umgebung und Modelle liegen unter `~/Library/Application Support/YuE UI/images/`, die Vorschläge neben der Datenbank in `images/` (nicht in der Sicherung, das übernommene Cover schon).

Klein 9B und Krea 2 liegen auf Hugging Face hinter ihren Lizenzen: auf [huggingface.co/black-forest-labs/FLUX.2-klein-9B](https://huggingface.co/black-forest-labs/FLUX.2-klein-9B) bzw. [huggingface.co/krea/Krea-2-Turbo](https://huggingface.co/krea/Krea-2-Turbo) annehmen, ein Lese-Token für das Konto anlegen und auf dem Mac in `~/.config/tonwerk/huggingface.token` speichern. Die anderen brauchen das nicht. `deploy/move-to-volume.sh images` verlegt die Modelle aller fünf, sie liegen im selben Hugging-Face-Cache. Die Gewichte, die ComfyUI schon geladen hat, nutzt mflux nicht; es lädt sie im Format von Hugging Face neu.

## Songs mit ACE-Step

Neben YuE2 macht [ACE-Step 1.5](https://github.com/ace-step/ACE-Step-1.5) Songs, über [acestep.cpp](https://github.com/ServeurpersoCom/acestep.cpp) (GGML mit Metal) und die quantisierten Modelle von [Serveurperso/ACE-Step-1.5-GGUF](https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF), MIT-lizenziert. Es hat eine eigene Seite unter **Werkzeuge → ACE-Step**, weil seine Parameter andere sind:

- **Aufgabe:** *Neuer Song*, *Cover* (ein Song der Bibliothek im neuen Stil, mit *Nähe zum Original*) oder *Neu malen* (ein Abschnitt eines Songs in Sekunden wird neu erzeugt).
- **Beschreibung** statt Tags: Genre, Instrumente, Stimme und Stimmung in freien Worten, am besten auf Englisch; die Bausteine passen auch hier.
- **Text** ist bei einem neuen Song optional: leer schreibt das Sprachmodell (LM) ihn selbst. Abschnitte wie `[Verse]` und `[Chorus]` versteht ACE-Step. Die Sprache des Gesangs lässt sich festlegen (50 Sprachen, Deutsch dabei).
- **Länge, Tempo, Tonart, Takt** legt man fest oder überlässt sie dem LM; bei Cover und Neu malen kommen sie vom Ausgangssong.
- **Varianten** (1 bis 4) macht aus einem Auftrag mehrere Songs mit aufeinanderfolgenden Seeds.
- **Höhen ausgleichen** (für neue Songs an): Der Klang-Decoder von ACE-Step verliert die hohen Töne (über 16 kHz rund 8 dB), die Songs klingen dumpf. Der Schalter hebt sie ab 12 kHz sanft an (+6 dB). Unabhängig davon schneidet Tonwerk keine Spitzen mehr ab: Jeder Song wird als Ganzes auf knapp unter Vollaussteuerung gebracht.
- **Aus Aufnahme füllen** (oben auf der Seite): Summen, pfeifen oder singen (bis zu einer Minute), eine Datei oder einen Song der Bibliothek wählen. ACE-Step hört zu und füllt Beschreibung, Text, Tempo, Tonart, Takt und Länge aus; die Melodie selbst wird nicht übernommen. Das läuft sofort, solange kein anderes großes Modell den Speicher hat, sonst kommt die Bitte, es gleich noch einmal zu versuchen. Gespeichert wird nichts.
- **Erweiterte Parameter:** LM und DiT (aus dem Modellordner), Schritte, Guidance, Shift, Solver, Temperatur, CFG, Top p/k und negativer Prompt des LM, „Beschreibung ausbauen“ und ein Adapter (LoRA) mit Stärke. Leer gelassen gilt, was das Modell vorsieht.

Die Songs landen in derselben Bibliothek, mit einem „ACE“-Schild, und lassen sich abspielen, bewerten, teilen und löschen wie alle anderen; eine Partitur (`score.abc`) gibt es nicht, also auch keinen Logic-Export daraus und kein Rendern. **Alles übernehmen** füllt bei einem ACE-Song die ACE-Seite, mit dem Text, den das LM geschrieben hat, und dem Seed des Songs. Sie liegen nicht bei YuE Studio, sondern in `ace-songs/` neben der Datenbank, und kommen mit in die Sicherung.

Ein Auftrag wartet wie ein Bild, bis YuE2, das Textmodell, die Stimmen, das Sprachlabor und die Cover den Speicher freigeben, und so lange warten diese. Tonwerk startet für jeden Auftrag `ace-lm` und `ace-synth` neu, danach ist der Speicher wieder frei. Der Standardsatz (4B-LM, Turbo-DiT, Text-Encoder, VAE, Q8) ist etwa 7,7 GB groß. Die **Warteschlange** zeigt Aufträge unter **ACE-Step**; eine Benachrichtigung meldet fertige.

**Einrichtung.** Einmal auf dem Mac im Repository, nach `deploy/install.sh`:

```sh
deploy/install-ace.sh           # acestep.cpp bauen (Commit fest im Skript), Standardmodelle laden
deploy/install-ace.sh --sft     # dazu der SFT-DiT (feiner, etwa 50 Schritte statt 8)
```

Oder über das Einrichtungsskript mit den bisherigen Optionen und `--ace`, damit Updates den Build mitpflegen. Programm und Modelle liegen unter `~/Library/Application Support/YuE UI/ace/`, Adapter als `.safetensors` in `ace/adapters/`. Bisher nur in einem Linux-Container mit nachgebauten Programmen getestet, nicht auf dem Mac und nicht mit echten Modellen.

## Apps für Mac, iPhone und iPad

Die nativen Apps liegen in `apple/` (Xcode-Projekt `apple/Tonwerk.xcodeproj`); was sie können und wie man sie verbindet, steht auf [tonwerk.studio/ai](https://tonwerk.studio/ai/).

**Mac:** Die App gibt es als DMG unter [Releases](https://github.com/Marcel-B/Tonwerk-AI/releases) (Tags `app-v…`), mit Developer ID signiert und von Apple notarisiert, nicht im App Store. Eine neue Version macht der Workflow **Mac-App veröffentlichen** (`.github/workflows/release-mac.yml`) von selbst, sobald CI auf main grün ist und sich seit dem letzten Release etwas an der App geändert hat (`apple/`, `LICENSE`, `NOTICE`; Server- und Web-Änderungen lösen keins aus). Die Hauptversion bleibt, die zweite Stelle zählt bei neuen Funktionen hoch, die dritte bei Korrekturen. Das entscheidet Claude, das auch die Neuerungen fürs Update-Fenster zusammenfasst; dafür braucht das Repository das Secret `ANTHROPIC_API_KEY`, ohne es wird jede Version eine Korrektur mit den Titeln der Änderungen als Neuerungen. Von Hand (Actions → Run workflow) lassen sich Version und Text vorgeben. Er baut die App für Apple Silicon und Intel, lässt App und DMG notarisieren und legt das Release mit dem Tag `app-v<Version>` an, dazu eine Kopie `Tonwerk-AI.dmg`, auf die die Website mit `releases/latest/download/Tonwerk-AI.dmg` zeigt. Dafür braucht das Repository die Secrets `MACOS_CERTIFICATE_P12` (das Zertifikat „Developer ID Application“ mit Schlüssel als .p12, Base64: `base64 -i DeveloperID.p12 | pbcopy`), `MACOS_CERTIFICATE_PASSWORD`, `APPLE_ID`, `APPLE_APP_PASSWORD` (app-spezifisches Passwort von account.apple.com) und `APPLE_TEAM_ID`, dieselben wie im Repository Synth, dazu der Schlüssel für Updates.

Updates: Die Mac-App sucht mit [Sparkle](https://sparkle-project.org) selbst nach neuen Versionen (Menü Tonwerk-AI → **Nach Updates suchen …**, in den Einstellungen auch automatisch). Sparkle liest `appcast.xml` aus dem neuesten Release, zeigt, was neu ist, lädt das DMG, prüft dessen Signatur, ersetzt die App und startet sie neu. Die Signatur macht der Workflow mit einem eigenen EdDSA-Schlüssel, einmal auf dem Mac erzeugt:

```sh
mkdir -p /tmp/sparkle && curl -fsSL https://github.com/sparkle-project/Sparkle/releases/download/2.9.6/Sparkle-2.9.6.tar.xz | tar -xJ -C /tmp/sparkle
/tmp/sparkle/bin/generate_keys                      # legt den Schlüssel im Schlüsselbund ab und zeigt den öffentlichen
/tmp/sparkle/bin/generate_keys -x ~/sparkle.key     # privaten Schlüssel als Datei
```

Den öffentlichen Schlüssel als Secret `SPARKLE_PUBLIC_ED_KEY`, den Inhalt von `~/sparkle.key` als `SPARKLE_PRIVATE_KEY` anlegen, danach die Datei löschen. Der Schlüssel im Schlüsselbund ist die Sicherung: Geht er verloren, nimmt keine installierte App mehr ein Update an, bis man sie einmal von Hand neu lädt. Eine App, die aus Xcode gebaut ist, hat keinen Schlüssel und kein Update-Menü.

**iPhone und iPad:** über TestFlight (`.github/workflows/testflight.yml`). Nach jedem Mac-Release lädt derselbe Lauf die iPhone-App mit derselben Version hoch; von Hand lässt sich der Workflow weiter starten.

## Konfiguration

`appsettings.json` bzw. Umgebungsvariablen:

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Urls` | `http://127.0.0.1:5090` | Adresse des Servers (in der Entwicklung `5091`, siehe `appsettings.Development.json`, damit sie neben der installierten App läuft) |
| `Yue:InstallRoot` (`Yue__InstallRoot`) | `~/Library/Application Support/YuE Studio` | Installation von YuE Studio (`env/`, `src/`, `models/`) |
| `Yue:OutputDir` (`Yue__OutputDir`) | `~/Music/YuE Studio` | Song-Bibliothek |
| `Yue:SheetSagePython` (`Yue__SheetSagePython`) | `<InstallRoot>/sheetsage-env/bin/python` | Python der SheetSage2-Umgebung |
| `Lyrics:BaseUrl` (`Lyrics__BaseUrl`) | `http://127.0.0.1:1234` | Server für Textentwürfe (LM Studio oder ein anderer OpenAI-kompatibler) |
| `Lyrics:Model` (`Lyrics__Model`) | `google/gemma-4-26b-a4b-qat` | Modell-ID, wie `GET /v1/models` sie listet |
| `Lyrics:ContextLength` (`Lyrics__ContextLength`) | `30000` | Kontext, mit dem das Modell geladen wird; bis auf 1024 Tokens für den Prompt darf die Antwort samt Denkphase ihn ganz nutzen |
| `Lyrics:MinContextLength` (`Lyrics__MinContextLength`) | `8192` | Kleinster Kontext, auf den Tonwerk zurückgeht, wenn LM Studio das Modell wegen zu wenig Speicher ablehnt |
| `Lyrics:ApiToken` (`Lyrics__ApiToken`) | – | nur nötig, wenn in LM Studio „Require Authentication“ an ist |
| `Lyrics:Lms` (`Lyrics__Lms`) | `~/.lmstudio/bin/lms` | Kommandozeilenwerkzeug, mit dem Tonwerk den Server von LM Studio startet |
| `Lyrics:ClaudeModels` (`Lyrics__ClaudeModels__0`) | `claude-opus-5-5`, `claude-fable-5-1`, `claude-sonnet-5-5`, `claude-haiku-5-5` | Claude-Modelle in der Auswahl für Textentwürfe, solange ein Schlüssel von Anthropic da ist; leer bietet keines an |
| `Lyrics:ClaudeEffort` (`Lyrics__ClaudeEffort`) | `medium` | Wie gründlich Claude über einem Text nachdenkt (`low` bis `max`) |
| `Sketch:Model` (`Sketch__Model`) | `claude-opus-5-5` | Claude-Modell für Ausarbeitungen |
| `Sketch:Effort` (`Sketch__Effort`) | `high` | wie gründlich es nachdenkt (`low` bis `max`) |
| `Sketch:Models` (`Sketch__Models__0`) | `claude-opus-5-5`, `claude-fable-5-1`, `claude-sonnet-5-5` | Claude-Modelle, unter denen die Ausarbeitung wählen lässt; `Sketch:Model` ist der Standard |
| `Sketch:KeyFile` (`Sketch__KeyFile`) | `~/.config/tonwerk/anthropic.key` | Datei mit dem API-Schlüssel von Anthropic (oder `ANTHROPIC_API_KEY`, `Sketch:ApiKey`) |
| `Sketch:MaxTokens` (`Sketch__MaxTokens`) | `64000` | Obergrenze der Antwort samt Denkphase |
| `Sketch:Timeout` (`Sketch__Timeout`) | `00:20:00` | so lange darf ein Plan dauern |
| `Machines:Name` (`Machines__Name`) | `Dieser Mac` | Name dieses Macs in der Rechnerwahl und am Auftrag |
| `Machines:Helpers` (`Machines__Helpers__0__Url`, …) | – | andere Macs, an die Tonwerk Aufträge geben darf: je `Id`, `Name`, `Url` (über Tailscale) und optional ein eigener `Key` |
| `Machines:Serve` (`Machines__Serve`) | `false` | für einen anderen Mac rechnen (`/api/helper`) |
| `Machines:KeyFile` (`Machines__KeyFile`) | `~/.config/tonwerk/helper.key` | gemeinsamer Schlüssel der Macs (oder `Machines:Key`) |
| `Logic:SplitSections` (`Logic__SplitSections`) | `false` | eine Region je Songabschnitt statt einer je Spur |
| `Queue:BundleWindow` (`Queue__BundleWindow`) | `00:20:00` | so lange dürfen Songs für YuE2 an einem wartenden Textentwurf oder einer wartenden Fassung vorbei; `00:00:00` hält die Reihenfolge streng ein |
| `Voice:EngineRoot` (`Voice__EngineRoot`) | `~/Library/Application Support/YuE UI/engines` | wo `--voices` Trennung und Seed-VC einrichtet; die folgenden fünf liegen darunter |
| `Voice:Separator` (`Voice__Separator`) | `separator/env/bin/mlx-audio-separator` | das Trennprogramm; ist es da, trennt Tonwerk selbst statt über StemMyWav |
| `Voice:SeparatorModels` (`Voice__SeparatorModels`) | `separator/models` | seine Modelle |
| `Voice:SeedVcPython` (`Voice__SeedVcPython`) | `seed-vc/env/bin/python` | Python mit den Paketen von Seed-VC; mit `Voice:SeedVcPath` singt Tonwerk selbst statt über ChangeMyVoice |
| `Voice:SeedVcPath` (`Voice__SeedVcPath`) | `seed-vc/src` | Checkout von Seed-VC |
| `Voice:SeedVcModels` (`Voice__SeedVcModels`) | `seed-vc/models` | Checkpoints von Seed-VC (`HF_HUB_CACHE`), beim ersten Singen geladen |
| `Voice:BaseUrl` (`Voice__BaseUrl`) | – | ChangeMyVoice ohne `/api` (nur ohne eigenes Seed-VC, und einmal für die Übernahme der Stimmen), z. B. `http://100.93.85.52:5080`; leer schaltet Stimmen und Fassungen ab |
| `Voice:ApiKey` (`Voice__ApiKey`) | – | Schlüssel für ChangeMyVoice (`X-Api-Key`) |
| `Voice:ApiKeyFile` (`Voice__ApiKeyFile`) | – | oder eine Datei, in der er steht |
| `Voice:StemsBaseUrl` (`Voice__StemsBaseUrl`) | `http://127.0.0.1:5081` | API von StemMyWav auf dem Mac (nicht das Gateway) |
| `Voice:StemsApiKey` (`Voice__StemsApiKey`) | – | Schlüssel für StemMyWav; ohne ihn gilt `Voice:StemsApiKeyFile` |
| `Voice:StemsApiKeyFile` (`Voice__StemsApiKeyFile`) | `~/.config/stemmywav/mac-api-key` | Datei mit dem Schlüssel für StemMyWav |
| `Voice:StemModel` (`Voice__StemModel`) | `mel-roformer-kim-vocals` | Trennmodell für Fassungen |
| `Speech:Root` (`Speech__Root`) | `~/Library/Application Support/YuE UI/speech` | Ordner des Sprachlabors (Python-Umgebung und Modelle) |
| `Speech:Python` (`Speech__Python`) | `<Root>/env/bin/python` | Python mit mlx-audio |
| `Speech:ModelCache` (`Speech__ModelCache`) | `<Root>/models` | Hugging-Face-Cache der Sprachmodelle (`HF_HOME`) |
| `Speech:Timeout` (`Speech__Timeout`) | `01:00:00` | so lange darf ein Ergebnis samt erstem Download dauern |
| `Singing:Root` (`Singing__Root`) | `~/Library/Application Support/YuE UI/singing` | Ordner von SoulX-Singer (`env/`, `src/`, `models/`, `nltk_data/`); `Python`, `Source`, `Model`, `NltkData` einzeln |
| `Singing:Device` (`Singing__Device`) | leer | PyTorch-Gerät; leer nimmt Metal (`mps`), wo es das gibt |
| `Singing:Steps` / `Singing:Cfg` | `32` / `3` | Schritte und Guidance je Abschnitt |
| `Singing:Timeout` (`Singing__Timeout`) | `00:30:00` | so lange darf ein Ergebnis samt Laden dauern |
| `Singing:Voices` | die zwei Beispielstimmen | Stimmen: je `id`, `label`, `audio`, `metadata` (relativ zum Checkout), `language` |
| `AudioMidi:Python` (`AudioMidi__Python`) | `~/Library/Application Support/YuE UI/midi/env/bin/python` | Python mit Basic Pitch für Audio zu MIDI |
| `Montage:MediaRoot` (`Montage__MediaRoot`) | `~/Pictures/Tonwerk` | Ordner, dessen Unterordner der Videodialog als Fotos & Videos anbietet |
| `Montage:Python` (`Montage__Python`) | `~/Library/Application Support/YuE UI/montage/env/bin/python` | Python mit librosa, Pillow und open_clip für Fotovideos |
| `Montage:ClipModel` / `Montage:ClipPretrained` | `xlm-roberta-base-ViT-B-32` / `laion5b_s13b_b90k` | CLIP-Modell für das Motiv, wie open_clip es nennt |
| `Montage:ModelCache` (`Montage__ModelCache`) | `~/Library/Application Support/YuE UI/montage/models` | Hugging-Face-Cache für CLIP (`HF_HOME`) |
| `Images:Root` (`Images__Root`) | `~/Library/Application Support/YuE UI/images` | Ordner für gemalte Cover (Python-Umgebung und Modelle) |
| `Images:Python` (`Images__Python`) | `<Root>/env/bin/python` | Python mit mflux |
| `Images:Flux3Python` (`Images__Flux3Python`) | `<Root>/flux3/bin/python` | Python mit flux3-mlx (FLUX 3 Klein) |
| `Images:SystemMemoryGb` (`Images__SystemMemoryGb`) | was der Mac meldet | Speicher, nach dem Krea 2 und FLUX 3 (ab 32 GB) angeboten werden |
| `Images:ModelCache` (`Images__ModelCache`) | `<Root>/models` | Hugging-Face-Cache der Bildmodelle (`HF_HOME`) |
| `Images:TokenFile` (`Images__TokenFile`) | `~/.config/tonwerk/huggingface.token` | Hugging-Face-Token, für Klein 9B und Krea 2 |
| `Images:Timeout` (`Images__Timeout`) | `01:30:00` | so lange darf ein Bild samt erstem Download dauern |
| `Ace:Root` (`Ace__Root`) | `~/Library/Application Support/YuE UI/ace` | Ordner für ACE-Step; die folgenden drei liegen darunter |
| `Ace:Binaries` (`Ace__Binaries`) | `acestep.cpp/build` | `ace-lm` und `ace-synth` |
| `Ace:Models` (`Ace__Models`) | `models` | GGUF-Dateien (LM, DiT, Text-Encoder, VAE) |
| `Ace:Adapters` (`Ace__Adapters`) | `adapters` | LoRAs für den DiT |
| `Ace:Songs` (`Ace__Songs`) | `ace-songs` neben der Datenbank | wo ACE-Songs gespeichert werden |
| `Ace:Timeout` (`Ace__Timeout`) | `01:00:00` | so lange darf ein Auftrag dauern |
| `Push:DataPath` (`Push__DataPath`) | `~/Library/Application Support/YuE UI/push.json` | VAPID-Schlüssel und Abonnements für Benachrichtigungen |
| `Data:Path` (`Data__Path`) | `~/Library/Application Support/YuE UI/yueui.db` | SQLite-Datenbank von Tonwerk (Playlists, geänderte Titel, Bewertungen) |
| `Logs:Directory` (`Logs__Directory`) | `~/Library/Application Support/YuE UI/logs` | Tagesdateien des Protokolls |
| `Logs:RetentionDays` (`Logs__RetentionDays`) | `14` | So viele Tage bleiben sie liegen |
| `Logs:UpdateLog` (`Logs__UpdateLog`) | `~/Library/Logs/tonwerk-update.log` | Das Log von `deploy/update.sh`, das die Seite mitliest |
| `Monitoring:Metrics` (`Monitoring__Metrics`) | `true` | `GET /metrics` für Prometheus |
| `Monitoring:Loki:Url` (`Monitoring__Loki__Url`) | – | Loki, z. B. `http://monitoring:3100`; leer schickt kein Protokoll |
| `Monitoring:Loki:Host` (`Monitoring__Loki__Host`) | Name des Rechners | Label `host` |
| `Monitoring:Loki:MinLevel` (`Monitoring__Loki__MinLevel`) | `info` | Zeilen darunter bleiben auf dem Mac |
| `Monitoring:Loki:Username` / `Monitoring:Loki:Password` | – | Basic Auth, falls Loki hinter einem Proxy das verlangt |
| `Push:Subject` (`Push__Subject`) | `https://github.com/Marcel-B/Tonwerk-AI` | Kontaktadresse (`mailto:` oder `https:`) für die Push-Dienste; Apple lehnt Adressen wie `mailto:ich@localhost` ab |
| `Backup:EnvFile` (`Backup__EnvFile`) | `~/.config/tonwerk/nextcloud.env` | `.env`-Datei mit `NEXTCLOUD_WEBDAV_URL`, `NEXTCLOUD_USERNAME` und `NEXTCLOUD_APP_PASSWORD` für die Datensicherung; fehlt eins davon, ist sie aus |
| `Backup:At` (`Backup__At`) | `03:00` | Uhrzeit der nächtlichen Sicherung; leer heißt nur von Hand |
| `Backup:TimeZone` (`Backup__TimeZone`) | `Europe/Berlin` | Zeitzone dafür |
| `Backup:Files` (`Backup__Files`) | `true` | Songs, Fassungen, Stems, Cover und Sprachlabor mitsichern; `false` sichert nur die Datenbank |

## API

| Methode | Pfad | |
|---|---|---|
| `GET` | `/api/status` | Worker-Zustand, Songs in Arbeit, Protokoll |
| `GET` | `/api/busy` | `{ busy, reasons }`: ob ein Neustart laufende Arbeit abbräche (`songs`, `transcription`, `lyrics`, `voices`, `speech`, `singing`); für `deploy/update.sh` |
| `GET` | `/api/logs` | Gesammeltes Protokoll, neueste Zeilen zuerst: `source` (kommagetrennt: `server`, `worker`, `queue`, `lyrics`, `voices`, `speech`, `singing`, `video`, `export`, `logic`, `backup`, `push`, `update`), `level` (Mindeststufe `debug`/`info`/`warning`/`error`), `q` (Text), `before` (Zeit der letzten Zeile, für die nächste Seite), `limit` (Standard 200) → `{ entries, more }` |
| `GET` | `/metrics` | Messwerte im Textformat von Prometheus (`404`, wenn `Monitoring:Metrics` aus ist) |
| `GET` | `/api/logs/report` | Bericht als Text für einen Chat: Version, Engines, Zustand, Warnungen und Fehler der letzten `hours` Stunden (Standard 24) mit Kontext |
| `GET` | `/api/events` | dasselbe live als Server-Sent Events (`snapshot`, `song`, `worker`, `log`, `library`, `transcription`, `lyrics`, `version`, `speech`, `singing`, `queue`, `ping`) |
| `POST` | `/api/generate` | neuer Lauf: `{ style, lyrics, title?, batch?: 1–10, quality?: "draft"\|"full", cot?, seed?, instrumental?, engines?, draftSteps?, maxTokens?: 200–15000, abc?, fullSteps?: 1–64, abcSampling?, semanticSampling?, voice?, lora?, loraStrength?: 0–3 }`; `lora` ist der Name einer LoRA aus `/api/loras` (`400`, wenn es sie nicht gibt), `voice` ist `{ voiceId, semiToneShift?, strength?, diffusionSteps?, keepReverb? }` wie bei den Fassungen und singt jeden fertigen Song danach mit dieser Stimme (`400` für eine unbekannte Stimme, `501` ohne Einrichtung); `abc` braucht `cot` "full" oder "melody", Sampling ist `{ temperature?, topP?, topK?, repetitionPenalty?, penaltyWindow? }`, über 9000 Tokens, `fullSteps` und Sampling nur mit der Worker-Erweiterung; antwortet `202`, ohne Inhalt, wenn der Lauf an den Worker ging, sonst mit dem wartenden Auftrag `{ id, kind, title, createdAt, songId, batch, quality, revision, voiceLabel }` |
| `GET` | `/api/loras` | `{ folder, loras }`: die LoRAs im Ordner mit `name`, `sizeBytes`, `modifiedAt`, `triggerWord`, `steps`, `songs`, `minutes` |
| `POST` | `/api/loras` | LoRA hochladen (Formular: `file` als `.safetensors` bis 1 GB, `name` optional); `201` mit der LoRA |
| `DELETE` | `/api/loras/{name}` | LoRA löschen |
| `POST` | `/api/songs/{run}/{song}/render` | Song aus seinen Tokens neu synthetisieren, z. B. einen Entwurf in voller Qualität; `202` wie bei `generate` |
| `GET` | `/api/machines` | Rechner für die Rechnerwahl: dieser Mac (`local`) und die Helfer mit `reachable`, `holder`, `busy` und `engines` (`lyrics`, `speech`, `images`, `ace`, `voices`, `stems`, `singing`); Aufträge dieser Werkzeuge nehmen `machine` (`local`, die `Id` eines Helfers, leer für automatisch) und nennen danach `ranOn` |
| `GET` | `/api/queue` | wartende Aufträge in Reihenfolge (auch im Snapshot und als `queue`-Event) |
| `DELETE` | `/api/queue/{id}` | wartenden Auftrag herausnehmen; `404`, wenn er nicht (mehr) wartet |
| `POST` | `/api/queue/{id}/move` | `{ offset }`: wartenden Auftrag um so viele Plätze verschieben, negativ nach vorn |
| `POST` | `/api/songs/{run}/{song}/cancel` | Song abbrechen |
| `POST` | `/api/stop` | alle Songs abbrechen |
| `POST` | `/api/worker/shutdown` | Worker-Prozess beenden (Speicher freigeben) |
| `GET` | `/api/transcriptions` | `{ installed, items }`: ob SheetSage2 installiert ist, und die fertigen Transkriptionen |
| `POST` | `/api/transcriptions` | Aufnahme transkribieren: Formular mit `file` und `task` (`melody-full` oder `melody-vocal`); der Fortschritt kommt als `transcription`-Event |
| `POST` | `/api/transcriptions/{id}/cancel` | laufende Transkription abbrechen |
| `GET` | `/api/transcriptions/{id}/score` | `score.abc` der Transkription (`?download=true` als Download) |
| `GET` | `/api/transcriptions/{id}/zip` | alle Dateien der Transkription als ZIP |
| `DELETE` | `/api/transcriptions/{id}` | fertige Transkription löschen |
| `GET` | `/api/library` | alle Läufe mit ihren Songs |
| `GET` | `/api/songs/{run}/{song}/audio` | FLAC (Range-fähig; `?download=true` als Download, eine Kopie mit Titel, Songtext und Cover in den Tags) |
| `GET` | `/api/songs/{run}/{song}/score` | `score.abc` |
| `GET` | `/api/songs/{run}/{song}/stream` | Was der Player spielt: AAC-Kopie mit 192 kbit/s (Range-fähig, beim ersten Mal erzeugt), ohne Encoder die FLAC |
| `POST` | `/api/quick` | Song aus Stichwörtern in einem Aufruf (JSON: `keywords`, optional `style`, `language` `de`/`en`, `title`, `batch`, `quality`): erst der Songtext, dann der Song; `202` mit `draftId`, `title`, `style` |
| `POST` | `/api/songs/{run}/{song}/export` | Song als MP3, M4A oder FLAC mit Tags und Cover (Formular: `format` = `small` (AAC 128 kbit/s zum Teilen)/`mp3`/`m4a`/`flac`, `artist`, `genre`, `cover` als JPEG/PNG bis 5 MB, ohne `cover` das eigene Cover des Songs); `501`, wenn der Encoder fehlt |
| `GET` | `/api/songs/{run}/{song}/cover` | Das eigene Cover des Songs; `404` ohne |
| `PUT` | `/api/songs/{run}/{song}/cover` | Setzt das Cover (Formular: `cover` als JPEG/PNG bis 5 MB) |
| `DELETE` | `/api/songs/{run}/{song}/cover` | Zurück zum gezeichneten Cover |
| `GET` | `/api/songs/{run}/{song}/logic` | Song als Logic-Projekt (ZIP mit `.logicx`), Hinweise im Header `X-YueToLogic-Diagnostics`; `422`, wenn daraus kein Projekt wird, mit dem Grund |
| `POST` | `/api/logic/convert` | Multipart: `song` (`<run>/songN`) oder `file` (`score.abc` oder MIDI-Datei, die erst zur Partitur wird), dazu `options` (ConversionOptions als JSON) und für einen Song `bassStems?`/`drumStems?` (Id eines Stem-Satzes mit `bass` bzw. `drums`, deren MIDI den erzeugten Bass bzw. das Schlagzeug ersetzt; `404` ohne den Stem, `501` ohne Basic Pitch): die umgewandelte Partitur mit Noten und MIDI-Datei (Base64) für die Seite Logic; `422` mit den Diagnosen, wenn sie sich nicht lesen lässt |
| `POST` | `/api/logic/musicxml` | Multipart wie oben, dazu `name`: die Partitur als MusicXML (`.musicxml`, ohne Schlagzeug); `422` mit den Diagnosen |
| `POST` | `/api/logic/export` | Multipart wie oben, dazu `audio` (FLAC, nur mit `file`), `name`, `splitSections`, `instruments` (Spur → Name, Port, Kanal als JSON): Logic-Projekt als ZIP, Hinweise im Header; `422` mit den Diagnosen |
| `GET` / `PUT` / `DELETE` | `/api/logic/presets[/{name}]` | Voreinstellungen der Seite Logic (`PUT` mit `{ form }`) |
| `GET` / `PUT` / `DELETE` | `/api/logic/synths/presets[/{name}]` | Gespeicherte Klänge des Browser-Synthesizers (`PUT` mit `{ patch }`) |
| `GET` / `PUT` / `DELETE` | `/api/logic/synths/tracks[/{track}]` | Klang je Spur (`PUT` mit `{ patch, preset }`; `DELETE` gibt der Spur den Standardklang zurück) |
| `GET` / `PUT` | `/api/logic/synths/mixer` | Mixer der Vorschau (`PUT` mit `{ settings }`: Master und Lautstärke/Panorama je Spur) |
| `GET` / `POST` | `/api/logic/sessions` | Sitzungen der Seite Logic, die zuletzt geänderte zuerst; `POST` als Multipart mit `name`, `settings` (JSON-Objekt) und entweder `song` (`<run>/songN`) oder `file` (`score.abc` oder MIDI-Datei, bis 4 MB) |
| `GET` / `PUT` / `DELETE` | `/api/logic/sessions/{id}` | Eine Sitzung; `PUT` mit `{ name?, settings? }` benennt um und/oder speichert, `DELETE` löscht sie mit ihren Dateien |
| `GET` | `/api/logic/sessions/{id}/file` | Die hochgeladene Partitur der Sitzung |
| `GET` / `PUT` / `DELETE` | `/api/logic/sessions/{id}/audio` | Die mitgebrachte `audio.flac` (`PUT` als Multipart-Feld `audio`, bis 250 MB); nur für Sitzungen mit eigener Partitur |
| `POST` | `/api/logic/import` | Instrumente, Zuordnungen und Voreinstellungen aus yue-to-logic-pro übernehmen (`{ instruments, assignments, presets }`, wie dessen `GET`-Routen sie liefern) |
| `GET` / `POST` | `/api/instruments` | Instrumente der Seite Logic (Name, Port, Kanal, `kind` `Synth` oder `DrumMachine`, `drums`); `409` bei doppeltem Namen |
| `PUT` / `DELETE` | `/api/instruments/{id}` | Instrument ändern oder löschen (mit seinen Zuordnungen) |
| `GET` / `PUT` | `/api/instruments/assignments[/{track}]` | Welche Spur welches Instrument spielt (`PUT` mit `{ instrumentId }`, `null` nimmt es weg) |
| `POST` | `/api/midi/abc` | Multipart-Feld `file`: MIDI-Datei (bis 4 MB), zurück in eine Partitur gewandelt: `{ abc, warnings }`; `422`, wenn keine Partitur daraus wird, `413` für zu große Dateien |
| `DELETE` | `/api/songs/{run}/{song}` | Song löschen, mit dem letzten auch den Lauf; `409`, solange der Worker daran arbeitet |
| `GET` / `POST` | `/api/templates` | Vorlagen (`{ name, settings }`, `settings` ein JSON-Objekt aus dem Formular); `409` bei vergebenem Namen (ohne Groß-/Kleinschreibung) |
| `PUT` / `DELETE` | `/api/templates/{id}` | Vorlage umbenennen und/oder überschreiben (`name`, `settings`, je optional) oder löschen |
| `PUT` | `/api/songs/{run}/{song}/note` | Notiz zum Song (`{"note": "…"}`, leer nimmt sie weg); `400` über 10 000 Zeichen |
| `PUT` | `/api/songs/{run}/{song}/rating` | Song bewerten (`{"rating": 1…5}`, `null` oder `0` nimmt die Bewertung weg); `400` außerhalb von 1 bis 5 |
| `PUT` | `/api/runs/{run}/title` | Lauf umbenennen (`{"title": "…"}`, leer = ursprünglicher Titel); Ordner und Song-IDs bleiben |
| `DELETE` | `/api/runs/{run}` | Lauf mit allen Songs löschen; `409`, solange der Worker an einem davon arbeitet |
| `GET` | `/api/storage` | `{ freeBytes, totalBytes }` des Datenträgers der Bibliothek |
| `GET` | `/api/lyrics/models` | Modelle für Textentwürfe: `{ default, models: [{ id, name, sizeBytes, loaded, vision, cloud }] }` (`vision`: kann Fotos lesen, `null`, wenn der Server es nicht sagt; `cloud`: Claude bei Anthropic); startet den Server von LM Studio bei Bedarf, `503`, wenn er nicht erreichbar ist und kein Claude angeboten wird |
| `POST` | `/api/lyrics` | Songtext entwerfen: `{ keywords, style?, language?, model?, image? }` (ohne `model` das eingestellte; `image` ein Foto als `data:image/jpeg;base64,…`-URL, dann ist `keywords` optional); mit `lyrics` und `instruction` statt dessen einen vorhandenen Text wie angewiesen überarbeiten (`keywords` optional, kein `image`); antwortet `202` mit `{ id, stage }`, der Entwurf kommt als `lyrics`-Event (`writing`, dann `done` mit `lyrics` oder `failed` mit `message`); solange YuE2 rechnet oder schon ein Entwurf entsteht, wartet er in der Warteschlange (`stage` ist dann `queued`) |
| `GET` | `/api/voice` | `{ voicesConfigured, conversionConfigured, stemsConfigured }`: ob Stimmen (Seed-VC oder ChangeMyVoice), dazu die Trennung, bzw. die Trennung eingerichtet ist |
| `GET` | `/api/voices` | Referenzstimmen: `[{ id, label, seconds, createdAt }]` |
| `POST` | `/api/voices` | Stimme anlegen: Formular mit `label` und `file` (Audio bis 64 MB) |
| `GET` | `/api/voices/{id}/audio` | Aufnahme der Stimme, wie sie behalten wurde |
| `DELETE` | `/api/voices/{id}` | Stimme löschen; `409`, solange ein Auftrag von ChangeMyVoice sie braucht |
| `POST` | `/api/songs/{run}/{song}/versions` | Song mit einer Stimme neu singen: `{ voiceId, semiToneShift?: -24–24, strength?: 0–1, diffusionSteps?: 10–100, keepReverb? }`; antwortet `202`, der Fortschritt kommt als `version`-Event (`queued`, `separating`, `converting`, `mixing`, dann `done` oder `failed`); `400` für eine unbekannte Stimme, `501` ohne Einrichtung |
| `GET` | `/api/songs/{run}/{song}/versions/{id}/audio` | fertige Fassung als FLAC (Range-fähig; `?download=true` als getaggter Download wie beim Song) |
| `GET` | `/api/songs/{run}/{song}/versions/{id}/stream` | fertige Fassung als AAC-Kopie für den Player, wie beim Song |
| `DELETE` | `/api/songs/{run}/{song}/versions/{id}` | Fassung abbrechen oder löschen |
| `GET` | `/api/swaps` | getauschte Stimmenspuren, neueste zuerst |
| `POST` | `/api/swaps` | Stimmenspur tauschen: Formular mit `file` (bis 300 MB), `voiceId`, `semiToneShift?`, `strength?`, `diffusionSteps?`, `separate?` (ganzer Song: erst trennen, dann zurückmischen) und `keepReverb?`; antwortet `202`, der Fortschritt kommt als `swap`-Event wie bei einer Fassung; `501` ohne Seed-VC (oder mit `separate` ohne Trennung) |
| `GET` | `/api/swaps/{id}/audio` / `source` | Ergebnis als FLAC (`?download=true` mit Dateinamen) bzw. der Upload, beide Range-fähig |
| `DELETE` | `/api/swaps/{id}` | Tausch abbrechen oder löschen |
| `GET` | `/api/timing` | Ob Whisper installiert ist, wo Tonwerk seine Python-Umgebung sucht und welches Modell es nimmt |
| `GET` | `/api/songs/{run}/{song}/timing` | Zeitmarken des Songtexts (`timing`: Zeilen mit `start`, `end`, `section`, `heard`; `source`, `coverage`) und wie weit ein Song ohne sie ist (`info`) |
| `POST` | `/api/songs/{run}/{song}/timing` | Song (neu) anhören lassen; `202`, `501` ohne Whisper |
| `GET` | `/api/songs/{run}/{song}/timing.srt` | Die Zeitmarken als Untertitel (SubRip) |
| `GET` | `/api/songs/{run}/{song}/videos` | Musikvideos des Songs, neueste zuerst |
| `POST` | `/api/songs/{run}/{song}/videos` | Musikvideo rendern: Formular mit `format` (`landscape`/`portrait`), `effect` (`bars`/`wave`/`none`), `color?` (`#rrggbb`, sonst Tonwerks Grün), `motion` (`particles`/`plasma`/`none`), `showCover?` (Standard an), `showTitle?`, `showLyrics?` (nur mit Zeitmarken) und den Ebenen als PNG: `background`, `cover` (mit `showCover`), `title` (mit `showTitle`), `particles` in Bildgröße, `plasma0` bis `plasma7` (die Blitze, mit Cover als Quadrat um seine Mitte, 1000×1000 bzw. 1080×1080, ohne in Bildgröße), `lyrics0`, `lyrics1`, … (je bis zu 24 Zeilen in Feldern von der Größe des Bands, darüber ein leeres). Ein Fotovideo hat `media` (ein Ordner aus `GET /api/videos/media`), `motif?` und statt `background` die Abdunklung `shade?`, keine `motion`; antwortet `202`, der Fortschritt kommt als `video`-Event; `501` ohne `ffmpeg` oder, für ein Fotovideo, ohne dessen Python-Umgebung |
| `GET` | `/api/videos/media` | Ordner für Fotovideos: `installed`, `root` und je Ordner `name`, `photos`, `videos` |
| `GET` | `/api/videos/{id}/file` | Das MP4 (`?download=true` mit Dateinamen, `&small=true` die 720p-Fassung für Messenger, beim ersten Abruf gerechnet), Range-fähig |
| `DELETE` | `/api/videos/{id}` | Video abbrechen oder löschen |
| `GET` | `/api/speech` | Sprachlabor: `{ installed, python, ffmpegInstalled, models, voices, takes }` (Modelle mit `downloaded`, Ergebnisse neueste zuerst) |
| `POST` | `/api/speech/voices` | Stimme aufnehmen: Formular mit `label`, `transcript` (Wortlaut) und `file` (jedes Format, das ffmpeg liest, bis 50 MB); `400` bei weniger als 2 Sekunden Sprache |
| `GET` / `DELETE` | `/api/speech/voices/{id}[/audio]` | Aufnahme als WAV (24 kHz mono) bzw. löschen |
| `POST` | `/api/speech/takes` | `{ text, voiceId?, models }`: ein Ergebnis je Modell, antwortet `202` mit ihnen; der Fortschritt kommt als `speech`-Event (`queued`, `loading`, `speaking`, dann `done` oder `failed` mit `message`); `501` ohne mlx-audio |
| `GET` | `/api/speech/takes/{id}/audio` | fertiges Ergebnis als WAV (`?download=true` als Download) |
| `DELETE` | `/api/speech/takes/{id}` | Ergebnis abbrechen oder löschen |
| `GET` | `/api/singing` | Singen: `{ installed, modelDownloaded, python, source, model, nltkData, voices, takes }` (Ergebnisse neueste zuerst) |
| `POST` | `/api/singing` | Formular mit `file` (MIDI oder `score.abc`, bis 5 MB) oder `song` (`run/songN`), dazu `lyrics?`, `voice?`, `pitchShift?` (−24 bis 24), `autoShift?`, `title?`; antwortet `202` mit dem Ergebnis, der Fortschritt kommt als `singing`-Event (`queued`, `loading`, `singing` mit `progress`, dann `done` oder `failed` mit `message`); `422` ohne Melodie, `501` ohne SoulX-Singer |
| `POST` | `/api/singing/voices` | eigene Stimme: Formular mit `file` (Aufnahme, bis 100 MB, 30 s werden gehört), `label`, `lyrics` (der gesungene Text); antwortet `201` mit `{ voice, heard }` (`heard`: behaltene Sekunden, Noten, gezogene, Silben übrig, behaltener Text, Spitzenpegel, tiefste und höchste Note); `422` ohne erkennbaren Gesang, `501` ohne SoulX-Singer |
| `GET` / `DELETE` | `/api/singing/voices/{id}[/audio]` | die behaltene Phrase als WAV bzw. die Stimme löschen (`409`, solange ein wartendes Ergebnis sie braucht) |
| `GET` / `DELETE` | `/api/singing/{id}[/audio]` | fertiges Ergebnis als WAV (`?download=true` als Download) bzw. abbrechen oder löschen |
| `GET` | `/api/audio-midi` | Audio zu MIDI: `{ installed, python }` |
| `POST` | `/api/audio-midi` | Aufnahme zu MIDI: Formular mit `file` (jedes Format, das ffmpeg liest, bis 200 MB), `mono?` (Standard an), `quantize?`, `bends?`, `tempo?`; antwortet sofort mit der MIDI-Datei, die Zahl der Noten im Header `X-Audio-Midi-Notes`; `400` für das Raster ohne Tempo, `422` ohne Noten, `501` ohne Basic Pitch |
| `POST` | `/api/songs/{run}/{song}/stems/{id}/{name}/midi` | Ein Stem zu MIDI: `{ mono?, quantize?, bends?, tempo? }`, Tempo sonst aus `score.abc` des Songs; Antwort wie oben |
| `GET` | `/api/ace` | ACE-Step: `{ installed, ready, binaries, models, lmModels, ditModels, adapters, jobs }` |
| `POST` | `/api/ace` | Song bei ACE-Step bestellen (`caption`, optional `title`, `lyrics`, `instrumental`, `language`, `duration`, `bpm`, `key`, `timeSignature`, `seed`, `batch`, `task`, `source`, Modelle und Parameter); antwortet `202` mit dem Auftrag, der Fortschritt kommt als `ace`-Event (`queued`, `loading`, `writing`, `composing`, `synthesizing`, `decoding`, `saving`, dann `done` mit `run` oder `failed`); `501` ohne acestep.cpp oder Modelle |
| `DELETE` | `/api/ace/{id}` | Auftrag abbrechen oder aus der Liste nehmen |
| `GET` | `/api/songs/{run}/{song}/ace` | Einstellungen eines ACE-Songs für „Alles übernehmen“ |
| `GET` | `/api/images` | Gemalte Cover: `{ installed, python, models }` (Modelle mit Lizenz, `commercial`, `downloaded`, `tokenFound`) |
| `GET` / `POST` | `/api/songs/{run}/{song}/images` | Vorschläge des Songs, neueste zuerst, bzw. `{ prompt, model?, seed? }` malen; antwortet `202`, der Fortschritt kommt als `image`-Event (`queued`, `loading`, `painting` mit `fraction`, dann `done` oder `failed`); `501`, wenn kein Mac hier das Modell malen kann (Runner fehlt, zu wenig Speicher) |
| `GET` / `DELETE` | `/api/images/{id}` | Vorschlag als JPEG bzw. abbrechen oder löschen |
| `PUT` | `/api/images/{id}/cover` | Vorschlag als Cover des Songs übernehmen |
| `GET` | `/api/sketches/info` | Ausarbeitung: `{ configured, model, models, local }` (`local`: die Modelle aus LM Studio) |
| `GET` / `POST` | `/api/sketches` | Ausarbeitungen, neueste zuerst, bzw. `{ text, instruction?, model? }` an Claude oder ein Modell aus LM Studio schicken; antwortet `202`, `400` für leeren oder zu langen Text oder ein unbekanntes Modell, `501` ohne Schlüssel für Claude |
| `GET` / `DELETE` | `/api/sketches/{id}` | `{ job }`, solange der Plan entsteht, danach `{ job, abc, lyrics, request, warnings, bars, seconds }` (`request` wie bei „Alles übernehmen“); bzw. abbrechen oder löschen |
| `GET` | `/api/sketches/{id}/midi`, `/api/sketches/{id}/score` | MIDI-Entwurf bzw. Partitur als Datei |
| `GET` | `/api/playlists` | `[{ id, name, songIds }]`: alle Playlists, älteste zuerst, Songs in Reihenfolge (`run/songN`), gelöschte weggelassen |
| `POST` | `/api/playlists` | `{ name }`: neue, leere Playlist (`201`) |
| `PUT` | `/api/playlists/{id}/name` | `{ name }`: umbenennen (1 bis 100 Zeichen) |
| `PUT` | `/api/playlists/{id}/songs` | `{ songIds }`: Songs der Playlist ersetzen; `400` für einen Song, den es nicht gibt |
| `DELETE` | `/api/playlists/{id}` | Playlist löschen (die Songs bleiben); `409` für die letzte |
| `GET` | `/api/push` | `{ publicKey }`: VAPID-Schlüssel für `pushManager.subscribe` |
| `POST` | `/api/push/subscriptions` | Browser benachrichtigen: `PushSubscription.toJSON()` plus `language` (`de`/`en`) |
| `DELETE` | `/api/push/subscriptions` | `{ endpoint }`: Abonnement entfernen |
| `GET` | `/api/backup` | Datensicherung: `{ configured, running, done, total, last, nextRun, files }`, `last` ist `{ startedAt, finishedAt, success, archive, files, bytes, error, scheduled }` |
| `POST` | `/api/backup` | Datensicherung jetzt starten; `202`, `409` während eine läuft, `501` ohne Einrichtung |
| `POST` | `/api/push/test` | `{ endpoint }`: Test-Nachricht an genau diesen Browser; `404`, wenn er nicht abonniert ist |

OpenAPI unter `/api/openapi`.

## Roadmap

Ideen und geplante Änderungen, ohne feste Reihenfolge. Erledigtes abhaken oder löschen.

- [x] Suche in der Bibliothek: nach Titel und im Volltext (Style und Lyrics)
- [x] Songs bewerten (1 bis 5 Sterne)
- [x] Notizen pro Song
- [x] Vorlagen für Stil und Parameter
- [x] Bibliothek sortieren (Datum, Sterne, Dauer)
- [x] Songtext-Entwurf mit einer kurzen Anweisung überarbeiten lassen
- [x] Songs vom Handy teilen (kleine AAC statt FLAC)
- [x] Export als MP3, M4A oder FLAC mit Songtext, Cover, Titel und Interpret
- [x] Cover bleibt beim Song (Bibliothek, Player, Sperrbildschirm, Export)
- [x] Mehrere Playlists
- [x] Songs mit einer anderen Stimme neu singen (Stem-Trennung, Seed-VC, Remix) und Stimmen hochladen
- [x] Gemeinsame Warteschlange: Songs, Neuberechnungen und Textentwürfe warten auf den Speicher statt abgelehnt zu werden
- [x] Warteschlange nach Modell bündeln (gleiche Arten zusammen, solange nichts zu lange wartet)
- [x] yue-to-logic-pro aufgenommen: Logic-Export im eigenen Prozess, Seite Logic mit Optionen, Instrumenten und Web MIDI
- [x] Synthesizer im Browser für die Vorschau der Seite Logic, Klang je Spur und gespeicherte Klänge
- [x] Mixer für die Vorschau im Browser
- [x] Oszilloskop, Delay und Hall für die Browser-Synthesizer
- [x] Vorschau mit Aufnahme und Wellenform, MusicXML-Export
- [x] Spektrum-Analyzer im Player und in der Vorschau, Hintergrund zur Musik
- [x] Player über den ganzen Bildschirm mit Songtext, Analyzer und Effekten
- [x] Stimme schon im Formular wählen; die Fassung entsteht dann von selbst, sobald der Song fertig ist
- [x] Messwerte für Prometheus, Protokoll an Loki, Grafana-Vorlage
- [ ] Eine Fassung statt der Originalstimme ins Logic-Projekt
- [x] PrimeVue-Importe optimieren (nur benötigte Komponenten, kleineres Bundle)
- [x] Restliche Oberfläche auf PrimeVue umstellen (`.button`, `.card`, `.link` und Eingabefelder aus `style.css` ablösen)

## Lizenz

Tonwerk-AI (Server, Weboberfläche und Apps) steht unter der [PolyForm Noncommercial License 1.0.0](LICENSE): nutzen, ändern und weitergeben für jeden nicht kommerziellen Zweck, mit der Zeile „Required Notice“ aus [NOTICE](NOTICE). Kommerzielle Nutzung nur mit meiner schriftlichen Erlaubnis. Die Lizenz gilt für die Software, nicht für die Modelle, die der Server lädt; die haben ihre eigenen Lizenzen (siehe die Abschnitte oben). Fremde Bibliotheken und ihre Lizenzen stehen in NOTICE.
