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

4. Grafana öffnen: `http://monitoring:3000`, Anmeldung mit `admin` und dem Passwort aus `.env`. Das Dashboard **Tonwerk** ist die Startseite.

Ob Prometheus den Mac erreicht, zeigt `http://monitoring:9090/targets`. Löst der Container den Tailscale-Namen nicht auf (MagicDNS im LXC aus), im Target die 100.x-Adresse des Macs eintragen und in `prometheus.yml` beim Job `apps` `tls_config: { server_name: <Mac>.<tailnet>.ts.net }` ergänzen, weil das Zertifikat auf den Namen ausgestellt ist.

## Was das Dashboard zeigt

- **Jetzt:** Worker, wer den Speicher hält, wartende Aufträge, Fertige und Fehlgeschlagene der letzten 24 Stunden, freier Platz.
- **Aufträge:** laufend und wartend je Art (Song, Render, Transkription, Songtext, Fassung, Stems, Stimmentausch, Video, Cover, ACE-Step, Sprachlabor), fertig pro Stunde, mittlere Dauer (Arbeitszeit ohne Warten auf den Speicher), Fehlschläge.
- **Speicher und Server:** wer wann den Speicher hielt, freier Platz über die Zeit, Speicher des Server-Prozesses (nicht der Modelle).
- **Protokoll:** Warnungen und Fehler je Quelle über die Zeit, darunter das Protokoll aus Loki mit Filtern für Quelle, Stufe und Text.

Das Dashboard **Finanz-O-Mat** (`grafana/dashboards/finanzomat.json`) zeigt Erreichbarkeit, Kontoumsätze ohne Beleg, Belege mit Prüfhinweis, Alter des letzten Backups, Updates, Anfragen und Antwortzeiten, eingelesene Belege, Anmeldungen, Speicher und CPU sowie das Protokoll. Die App meldet sich mit `finanz-o-mat-lxc.sh monitoring <CTID>` auf dem Proxmox-Host selbst an (Ziel `prometheus/targets/finanzomat.yml`, siehe README von [finanz-O-mat](https://github.com/Marcel-B/finanz-O-mat/blob/main/deploy/proxmox/README.md#monitoring)).

Das Dashboard **MyJourney** (`grafana/dashboards/myjourney.json`) zeigt Erreichbarkeit, den Bestand an Orten und Reisen, offene Live-Verbindungen, Anfragen und Antwortzeiten, Datenänderungen, Nextcloud-Backups, Speicher und CPU sowie das Protokoll. Die App bindet sich über ihre Env-Variablen an (`MYJOURNEY_METRICS_ENABLED`, `MYJOURNEY_LOKI_URL`; Ziel `prometheus/targets/myjourney.yml`, siehe DEPLOY.md §8 von [MyJourney](https://github.com/Marcel-B/MyJourney/blob/main/DEPLOY.md)).

Das Dashboard **Slipways** (`grafana/dashboards/slipways.json`) zeigt Erreichbarkeit, den Bestand an Slipanlagen, Marinas und Pegeln (nach Quelle: eigene Daten oder OpenStreetMap), die Hintergrund-Jobs (Pegelstände, Gewässerverläufe, OSM-Import: letzter Erfolg, Läufe, Dauer, Änderungen, ausgelastetes Overpass), Anfragen und Antwortzeiten, Größe der Datenbank, Speicher, CPU und Logversand sowie das Protokoll mit Filtern für Stufe, Quelle und Text. Angebunden ist nur die Produktion slipways.de (`slipways-1`). Das Ziel legt `deploy/monitoring/add-prometheus-target.sh` an, die Logs richtet `deploy/lxc/setup-loki.sh` ein, beide im [Slipways-Repo](https://github.com/Marcel-B/Slipways/blob/master/deploy/README.md#überwachung).

Prometheus behält 90 Tage (`PROMETHEUS_RETENTION`), Loki 30 Tage (`loki/loki.yml`).

## Wie viel ist das?

Für eine Handvoll Apps (Tonwerk, eine Buchhaltung, eine Reise-App) braucht der Stack zusammen etwa 600 MB bis 1 GB Arbeitsspeicher und wenig CPU; Prometheus und Loki sind für Tausende Ziele gebaut. Jede App kostet nur ihre Zeilen und ein paar hundert Messreihen. 2 GB RAM für den Container lassen Luft.

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

Für .NET-Apps ist das am einfachsten mit [prometheus-net](https://github.com/prometheus-net/prometheus-net) oder dem Prometheus-Exporter von OpenTelemetry. Logs schickt eine App an `http://monitoring:3100/loki/api/v1/push`, am besten mit dem Label `app` wie im Target, damit Messwerte und Zeilen in Grafana zusammenpassen; für .NET etwa mit Serilog und `Serilog.Sinks.Grafana.Loki`. Ein eigenes Dashboard ist eine weitere JSON-Datei in `grafana/dashboards/`.

## Sicherheit

Prometheus, Loki und Grafana haben außer dem Grafana-Login keine Anmeldung. Im Heimnetz und Tailnet ist das in Ordnung; wer die Ports nur im Tailnet will, setzt in `.env` `BIND_ADDRESS` auf die Tailscale-Adresse des Containers. Tonwerks `/metrics` ist wie der Rest von Tonwerk ohne Login im Tailnet erreichbar; es enthält keine Titel, Texte oder Schlüssel, nur Zahlen.
