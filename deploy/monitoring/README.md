# Monitoring: Prometheus, Loki, Grafana

Ein kleiner Monitoring-Stack für Tonwerk und weitere Apps. Er läuft **nicht auf dem Mac mini**: Prometheus, Loki und Grafana brauchen zusammen 1 bis 2 GB Arbeitsspeicher, die YuE2, ACE-Step und das Textmodell dort selbst brauchen. Gedacht ist ein LXC-Container oder eine VM auf dem Proxmox mit Docker und Tailscale.

```
Mac mini (Tonwerk)                          Proxmox (dieser Ordner)
  GET /metrics  <── tailscale serve :8443 ── Prometheus :9090 ──┐
  Protokoll     ── POST :3100 ─────────────> Loki       :3100 ──┼─> Grafana :3000
                                                                 │
  weitere Apps  <── /metrics, Logs ───────── (je eine Datei in prometheus/targets/)
```

## Einrichten mit dem Skript

`proxmox-lxc.sh` erledigt alles auf dem Proxmox-Host (als root in dessen Shell; der erste Befehl holt diesen Ordner von GitHub nach `/root/monitoring`): Es legt einen Debian-12-LXC an (2 GB RAM, 2 Kerne, 20 GB, unprivilegiert, mit Nesting und TUN für Docker und Tailscale), installiert darin Docker und Tailscale, kopiert diesen Ordner nach `/opt/monitoring`, erzeugt ein Grafana-Passwort und startet den Stack.

```sh
curl -fsSL https://codeload.github.com/Marcel-B/Tonwerk-AI/tar.gz/main | tar -xz --strip-components=2 -C /root Tonwerk-AI-main/deploy/monitoring
TONWERK_TARGET=mac-mini.tail1234.ts.net:8443 /root/monitoring/proxmox-lxc.sh
```

Ohne `TS_AUTHKEY` zeigt Tailscale einen Link, mit dem man den Container im Browser (auch auf dem Handy) anmeldet. Am Ende stehen Adresse und Passwort von Grafana und die Zeile für Tonwerks `appsettings.Production.json` (Schritt 3 unten). Speicher, Bridge, Größe und Name lassen sich über Umgebungsvariablen ändern (`proxmox-lxc.sh --help`). Ein zweiter Lauf findet den Container wieder, kopiert nur die Dateien neu und startet den Stack neu; `.env` und die Targets bleiben.

Mit `PVE_EXPORTER=1` beobachtet der Stack auch den Proxmox-Host und alle seine LXCs und VMs (siehe [Hosts](#hosts)):

```sh
PVE_EXPORTER=1 /root/monitoring/proxmox-lxc.sh
```

## Einrichten von Hand

1. Auf dem Proxmox einen Debian-LXC anlegen (2 GB RAM, 2 Kerne, 20 GB Platte reichen; für Docker im LXC unter *Optionen → Features* „Nesting“ und „keyctl“ einschalten), darin Docker und Tailscale installieren und `tailscale up` ausführen. Im Folgenden heißt er im Tailnet `monitoring`.
2. Diesen Ordner dorthin kopieren (oder das Repository klonen) und die Vorlagen ausfüllen:

   ```sh
   cp .env.example .env                                         # GF_SECURITY_ADMIN_PASSWORD setzen
   cp prometheus/targets/tonwerk.yml.example prometheus/targets/tonwerk.yml   # Tailscale-Name des Macs eintragen
   docker compose up -d
   ```

3. Auf dem Mac Tonwerk das Protokoll schicken lassen, in `~/Library/Application Support/YueUI/app/appsettings.Production.json`:

   ```json
   {
     "Monitoring": {
       "Loki": { "Url": "http://monitoring:3100", "Host": "mac-mini" }
     }
   }
   ```

   und neu starten: `launchctl kickstart -k gui/$(id -u)/de.bvelop.yueui`. Der LaunchAgent erreicht keine Adressen im Heimnetz (`192.168.…`), nur Tailscale-Namen und 100.x-Adressen.

4. Grafana öffnen: `http://monitoring:3000`, Anmeldung mit `admin` und dem Passwort aus `.env`. Das Dashboard **Übersicht** ist die Startseite.

Ob Prometheus den Mac erreicht, zeigt `http://monitoring:9090/targets`. Löst der Container den Tailscale-Namen nicht auf (MagicDNS im LXC aus), im Target die 100.x-Adresse des Macs eintragen und in `prometheus.yml` beim Job `apps` `tls_config: { server_name: <Mac>.<tailnet>.ts.net }` ergänzen, weil das Zertifikat auf den Namen ausgestellt ist.

## Alarme

Grafana schickt Alarme an ntfy, und zwar an den Server von Slipways auf slipways-1. Regeln, Kontaktpunkt und Richtlinie liegen als Dateien in `grafana/provisioning/alerting/` und sind damit versioniert; in der Oberfläche stehen sie im Ordner **Alarme** und lassen sich dort nicht ändern.

In `.env`:

```sh
NTFY_URL=https://slipways-1.<tailnet>.ts.net:2586
NTFY_TOPIC=alarme-<zufall>        # mit zufälligem Teil: wer im Tailnet den Namen kennt, kann mitlesen
GRAFANA_URL=http://monitoring:3000/   # optional, die Adresse in den Nachrichten
```

`proxmox-lxc.sh` fragt beim ersten Lauf danach. Fehlt `NTFY_URL`, geht der Kontaktpunkt ins Leere; Grafana zeigt das unter *Alerting → Contact points*. In der ntfy-App das Topic auf diesem Server abonnieren (wie bei Slipways' Benachrichtigungen). Alarme kommen rund um die Uhr; Ruhezeiten regelt das Telefon. Die Nachricht nennt die Regel, betroffene App oder Seite und einen Link zu Grafana; ein Alarm, der weiter anliegt, kommt nach 12 Stunden noch einmal, und wenn er vorbei ist, kommt „Wieder gut“.

| Regel | Wann | Für |
|---|---|---|
| App nicht erreichbar | `up` ist 0 | 10 min; jede App aus `prometheus/targets/` und der Stack selbst |
| Sicherung zu alt | letzte erfolgreiche Sicherung über 36 h her | 30 min; jede App mit `<app>_backup_last_success_timestamp_seconds` |
| Platte fast voll | unter 10 % frei | 30 min; Tonwerks Volumes |
| Logs erreichen Loki nicht | eine App hat in 30 min Zeilen verworfen | 15 min |
| Website nicht erreichbar | `probe_success` ist 0 | 15 min |
| Zertifikat läuft ab | in weniger als 14 Tagen | 1 h |
| oi-o-mat swappt | über 4 GB Swap | 10 min |
| Platte von oi-o-mat fast voll | intern oder ein externes Laufwerk (T7) unter 10 % frei | 30 min |
| Proxmox-Gast läuft nicht | ein LXC oder eine VM mit Autostart (`onboot`) läuft nicht | 10 min |

Fehlt einer App ein Messwert, schweigt die Regel für sie. Ausprobieren: unter *Alerting → Contact points* bei **ntfy** auf *Test*.

## Websites und Health-Endpunkte

Der Blackbox-Exporter fragt Adressen für Prometheus ab (Job `probes`, alle 5 Minuten): ob sie mit 2xx antworten und wann ihr Zertifikat abläuft. Die öffentlichen Seiten stehen in `prometheus/probes/websites.yml`. Apps im Tailnet, die noch kein `/metrics` haben, kommen in eine eigene Datei nach dem Muster `prometheus/probes/health.yml.example`; sie bleibt wie die Targets außerhalb von Git. Prometheus liest neue Dateien binnen einer Minute.

## Was die Übersicht zeigt

Ob jede App, jede Website und der Stack läuft (ein Klick auf eine App öffnet ihr Dashboard), aktive Alarme, die letzte erfolgreiche Sicherung je App, wann die Zertifikate ablaufen, Warnungen und Fehler je App aus Loki, die letzten Fehler, Speicher und CPU der App-Prozesse.

## Was das Dashboard Tonwerk zeigt

- **Jetzt:** Worker, wer den Speicher hält, wartende Aufträge, Fertige und Fehlgeschlagene der letzten 24 Stunden, freier Platz.
- **Aufträge:** laufend und wartend je Art (Song, Render, Transkription, Songtext, Fassung, Stems, Stimmentausch, Video, Cover, ACE-Step, Sprachlabor), fertig pro Stunde, mittlere Dauer (Arbeitszeit ohne Warten auf den Speicher), Fehlschläge.
- **Speicher und Server:** wer wann den Speicher hielt, freier Platz über die Zeit, Speicher des Server-Prozesses (nicht der Modelle).
- **Protokoll:** Warnungen und Fehler je Quelle über die Zeit, darunter das Protokoll aus Loki mit Filtern für Quelle, Stufe und Text. Die Quelle `update` ist das Log von `deploy/update.sh`.

Das Dashboard **Finanz-O-Mat** (`grafana/dashboards/finanzomat.json`) zeigt Erreichbarkeit, Kontoumsätze ohne Beleg, Belege mit Prüfhinweis, Alter des letzten Backups, Updates, Anfragen und Antwortzeiten, eingelesene Belege, Anmeldungen, Speicher und CPU sowie das Protokoll. Die App meldet sich mit `finanz-o-mat-lxc.sh monitoring <CTID>` auf dem Proxmox-Host selbst an (Ziel `prometheus/targets/finanzomat.yml`, siehe README von [finanz-O-mat](https://github.com/Marcel-B/finanz-O-mat/blob/main/deploy/proxmox/README.md#monitoring)).

Das Dashboard **MyJourney** (`grafana/dashboards/myjourney.json`) zeigt Erreichbarkeit, den Bestand an Orten und Reisen, offene Live-Verbindungen, Anfragen und Antwortzeiten, Datenänderungen, Nextcloud-Backups, Speicher und CPU sowie das Protokoll. Die App bindet sich über ihre Env-Variablen an (`MYJOURNEY_METRICS_ENABLED`, `MYJOURNEY_LOKI_URL`; Ziel `prometheus/targets/myjourney.yml`, siehe DEPLOY.md §8 von [MyJourney](https://github.com/Marcel-B/MyJourney/blob/main/DEPLOY.md)).

Das Dashboard **Slipways** (`grafana/dashboards/slipways.json`) zeigt Erreichbarkeit, den Bestand an Slipanlagen, Marinas und Pegeln (nach Quelle: eigene Daten oder OpenStreetMap), die Hintergrund-Jobs (Pegelstände, Gewässerverläufe, OSM-Import: letzter Erfolg, Läufe, Dauer, Änderungen, ausgelastetes Overpass), Anfragen und Antwortzeiten, Größe der Datenbank, Speicher, CPU und Logversand sowie das Protokoll mit Filtern für Stufe, Quelle und Text. Angebunden ist nur die Produktion slipways.de (`slipways-1`). Das Ziel legt `deploy/monitoring/add-prometheus-target.sh` an, die Logs richtet `deploy/lxc/setup-loki.sh` ein, beide im [Slipways-Repo](https://github.com/Marcel-B/Slipways/blob/master/deploy/README.md#überwachung).

Prometheus behält 90 Tage (`PROMETHEUS_RETENTION`), Loki 30 Tage (`loki/loki.yml`).

## Wie viel ist das?

Für eine Handvoll Apps (Tonwerk, eine Buchhaltung, eine Reise-App) braucht der Stack zusammen etwa 600 MB bis 1 GB Arbeitsspeicher und wenig CPU; Prometheus und Loki sind für Tausende Ziele gebaut. Jede App kostet nur ihre Zeilen und ein paar hundert Messreihen. 2 GB RAM für den Container lassen Luft.

## Hosts

Das Dashboard **Hosts** (`grafana/dashboards/hosts.json`) zeigt oi-o-mat und den Proxmox; ein Klick auf `oi-o-mat` oder `proxmox` in der Übersicht führt dorthin.

- **oi-o-mat:** Speicher (belegt laut macOS, fest, komprimiert, Swap) mit einem Band darunter, wer ihn hält (Tonwerks `tonwerk_memory_holder` und die Modelle, die die LLM-Schranke für Apps geladen hat), Speicherdruck, CPU, Platten (intern und T7), Netz. Die Zahlen liefert node_exporter, den `bin/node-exporter on` im scripts-Repo auf oi-o-mat einrichtet (nur `127.0.0.1:9100`, ins Tailnet per `tailscale serve --https=9100`); Ziel nach `prometheus/targets/oi-o-mat.yml.example`.
- **Proxmox:** Host, Speicher, CPU und Platte je Gast, Speicherpools, eine Tabelle der Gäste mit Autostart. Die Zahlen liefert der pve-Exporter im Stack (Dienst `pve-exporter`, Profil `pve`, Job `proxmox`). `proxmox-lxc.sh` legt dafür mit `PVE_EXPORTER=1` auf dem Host den Benutzer `monitoring@pve` an, gibt ihm und seinem Token `exporter` (privilege separated) nur die Rolle `PVEAuditor` (lesen), schreibt das Token in `pve.yml` im Container (Modus 600, in `.gitignore`, nie ausgegeben, auf dem Host nicht aufbewahrt), trägt den Host in `prometheus/pve/proxmox.yml` ein und schaltet das Profil in `.env` ein (`COMPOSE_PROFILES=pve`). Der Exporter hat keinen Port nach außen, nur Prometheus erreicht ihn; er fragt den Host im Heimnetz (Proxmox' eigenes Zertifikat, daher `verify_ssl: false`). Widerrufen: in Proxmox unter *Datacenter → Permissions → API Tokens*, danach `pve.yml` im Container löschen.

## LLM-Schranke

Das Dashboard **LLM-Schranke** (`grafana/dashboards/llm-gate.json`) zeigt die gemeinsame Warteschlange vor LM Studio auf oi-o-mat (scripts-Repo, `lib/llm-gate`): was wartet und warum (Tonwerk arbeitet, kein Speicher, angehalten, andere davor), Wartezeit und Dauer je App, Ergebnisse, geladene Modelle mit RAM, Laden und Entladen mit Grund, darüber Tonwerks Speicherhalter, und ihr Protokoll aus Loki. Die App steht in den Messwerten der Schranke im Label `client`, weil `app` das Label des Targets ist (`llm-gate`). Ziel nach `prometheus/targets/llm-gate.yml.example`, Logs über `loki.url` in der Konfiguration der Schranke (scripts, `docs/llm-gate.md`).

## Weitere Apps anbinden

Jede App, die einen Endpunkt im Prometheus-Format hat, ist eine Datei in `prometheus/targets/`; Prometheus liest neue Dateien innerhalb einer Minute ohne Neustart:

```yaml
# prometheus/targets/module-o-mat.yml
- targets: [module-o-mat.example.ts.net:8080]
  labels:
    app: module-o-mat
    __scheme__: http          # https, wenn sie hinter tailscale serve liegt
    __metrics_path__: /metrics
```

Für .NET-Apps ist das am einfachsten mit [prometheus-net](https://github.com/prometheus-net/prometheus-net) oder dem Prometheus-Exporter von OpenTelemetry. Logs schickt eine App an `http://monitoring:3100/loki/api/v1/push`, am besten mit dem Label `app` wie im Target, damit Messwerte und Zeilen in Grafana zusammenpassen; für .NET etwa mit Serilog und `Serilog.Sinks.Grafana.Loki`. Ein eigenes Dashboard ist eine weitere JSON-Datei in `grafana/dashboards/`; seine `uid` ist der Wert des Labels `app`, dann führt die Übersicht dorthin. Meldet die App `<app>_backup_last_success_timestamp_seconds` (Unixzeit der letzten erfolgreichen Sicherung), wacht der Alarm „Sicherung zu alt“ auch über sie.

## Sicherheit

Prometheus, Loki und Grafana haben außer dem Grafana-Login keine Anmeldung. Der Blackbox-Exporter hat keinen eigenen Port nach außen, nur Prometheus erreicht ihn. Im Heimnetz und Tailnet ist das in Ordnung; wer die Ports nur im Tailnet will, setzt in `.env` `BIND_ADDRESS` auf die Tailscale-Adresse des Containers. Tonwerks `/metrics` ist wie der Rest von Tonwerk ohne Login im Tailnet erreichbar; es enthält keine Titel, Texte oder Schlüssel, nur Zahlen.
