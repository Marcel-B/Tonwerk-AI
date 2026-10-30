#!/bin/bash
# Sets up the monitoring stack (Prometheus, Loki, Grafana; this folder) in a Debian LXC on a Proxmox host, with Docker
# and Tailscale inside. Run it as root on the Proxmox host, from a copy of this folder:
#
#   curl -fsSL https://codeload.github.com/Marcel-B/Tonwerk-AI/tar.gz/main | tar -xz --strip-components=2 -C /root Tonwerk-AI-main/deploy/monitoring
#   /root/monitoring/proxmox-lxc.sh
#
# Settings through the environment, all optional:
#   TONWERK_TARGET   the Mac's Tailscale name with port, e.g. mac-mini.tail1234.ts.net:8443 (asked for if empty)
#   NTFY_URL         where alarms go, e.g. https://slipways-1.tail1234.ts.net:2586 (asked for if empty)
#   NTFY_TOPIC       their topic (a random alarme-… if empty)
#   PVE_EXPORTER     1: watch this Proxmox host and all its guests too, through a read-only user monitoring@pve whose
#                    token (role PVEAuditor) goes only into the container's pve.yml (mode 600)
#   TS_AUTHKEY       a Tailscale auth key; without one, tailscale up prints a link to sign the container in
#   CT_HOSTNAME      container name, also its Tailscale name (monitoring)
#   CTID             container id (the next free one)
#   STORAGE          storage for the root disk (local-lvm), TEMPLATE_STORAGE for the Debian template (local)
#   BRIDGE           network bridge (vmbr0), MEMORY in MB (2048), CORES (2), DISK in GB (20)
#
# Running it again finds the container by its name and only copies the files anew and restarts the stack; .env and
# the targets already there are kept.
set -euo pipefail

CT_HOSTNAME="${CT_HOSTNAME:-monitoring}"
STORAGE="${STORAGE:-local-lvm}"
TEMPLATE_STORAGE="${TEMPLATE_STORAGE:-local}"
BRIDGE="${BRIDGE:-vmbr0}"
MEMORY="${MEMORY:-2048}"
CORES="${CORES:-2}"
DISK="${DISK:-20}"
TS_AUTHKEY="${TS_AUTHKEY:-}"
TONWERK_TARGET="${TONWERK_TARGET:-}"
NTFY_URL="${NTFY_URL:-}"
NTFY_TOPIC="${NTFY_TOPIC:-}"
PVE_EXPORTER="${PVE_EXPORTER:-}"
HERE="$(cd "$(dirname "$0")" && pwd)"
TARGET_DIR=/opt/monitoring

case "${1:-}" in
  -h|--help) sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
  "") ;;
  *) echo "Unknown argument: $1" >&2; exit 2 ;;
esac

command -v pct >/dev/null || { echo "pct not found: run this on the Proxmox host." >&2; exit 1; }
[ "$(id -u)" = 0 ] || { echo "Run this as root." >&2; exit 1; }
[ -f "$HERE/docker-compose.yml" ] || { echo "docker-compose.yml is missing next to this script." >&2; exit 1; }

in_ct() { pct exec "$CTID" -- bash -c "$1"; }

# A container of that name already there: reuse it.
CTID="${CTID:-$(pct list | awk -v name="$CT_HOSTNAME" 'NR > 1 && $NF == name { print $1; exit }')}"
if [ -n "$CTID" ] && pct status "$CTID" >/dev/null 2>&1; then
  echo "Using container $CTID ($CT_HOSTNAME)."
else
  CTID="${CTID:-$(pvesh get /cluster/nextid)}"
  echo "Looking for the newest Debian 12 template ..."
  pveam update >/dev/null
  TEMPLATE="$(pveam available --section system | awk '$2 ~ /^debian-12-standard_/ { print $2 }' | sort -V | tail -n 1)"
  [ -n "$TEMPLATE" ] || { echo "No Debian 12 template offered by pveam." >&2; exit 1; }
  if ! pveam list "$TEMPLATE_STORAGE" | grep -q "$TEMPLATE"; then
    pveam download "$TEMPLATE_STORAGE" "$TEMPLATE"
  fi

  echo "Creating container $CTID ($CT_HOSTNAME) ..."
  # Unprivileged; Docker inside needs nesting and keyctl.
  pct create "$CTID" "$TEMPLATE_STORAGE:vztmpl/$TEMPLATE" \
    --hostname "$CT_HOSTNAME" \
    --memory "$MEMORY" --swap 512 --cores "$CORES" \
    --rootfs "$STORAGE:$DISK" \
    --net0 "name=eth0,bridge=$BRIDGE,ip=dhcp" \
    --unprivileged 1 --features nesting=1,keyctl=1 \
    --onboot 1
  # Tailscale needs the TUN device, which an LXC does not get by default.
  cat >> "/etc/pve/lxc/$CTID.conf" <<'CONF'
lxc.cgroup2.devices.allow: c 10:200 rwm
lxc.mount.entry: /dev/net/tun dev/net/tun none bind,create=file
CONF
fi

if [ "$(pct status "$CTID" | awk '{ print $2 }')" != running ]; then
  pct start "$CTID"
fi
echo "Waiting for the network ..."
for _ in $(seq 1 60); do
  in_ct "getent hosts deb.debian.org >/dev/null" 2>/dev/null && break
  sleep 2
done
in_ct "getent hosts deb.debian.org >/dev/null" || { echo "The container has no network." >&2; exit 1; }

echo "Installing Docker and Tailscale ..."
in_ct "export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq && apt-get install -y -qq ca-certificates curl openssl >/dev/null
  command -v docker >/dev/null || curl -fsSL https://get.docker.com | sh >/dev/null
  command -v tailscale >/dev/null || curl -fsSL https://tailscale.com/install.sh | sh >/dev/null
  systemctl enable --now docker tailscaled >/dev/null"

if ! in_ct "tailscale status >/dev/null 2>&1"; then
  echo "Signing the container in to Tailscale as $CT_HOSTNAME ..."
  if [ -n "$TS_AUTHKEY" ]; then
    in_ct "tailscale up --hostname='$CT_HOSTNAME' --authkey='$TS_AUTHKEY'"
  else
    echo "Open the link that follows to sign it in."
    in_ct "tailscale up --hostname='$CT_HOSTNAME'"
  fi
fi

echo "Copying the stack to $TARGET_DIR ..."
ARCHIVE="$(mktemp)"
tar -C "$HERE" --exclude=.env --exclude=pve.yml --exclude='prometheus/targets/*.yml' --exclude='prometheus/pve/*.yml' -czf "$ARCHIVE" .
pct push "$CTID" "$ARCHIVE" /tmp/monitoring.tgz
rm -f "$ARCHIVE"
in_ct "mkdir -p '$TARGET_DIR' && tar -C '$TARGET_DIR' -xzf /tmp/monitoring.tgz && rm /tmp/monitoring.tgz"

PASSWORD=""
if ! in_ct "test -f '$TARGET_DIR/.env'"; then
  PASSWORD="$(openssl rand -base64 18 | tr -d '/+=')"
  in_ct "umask 077 && printf 'GF_SECURITY_ADMIN_USER=admin\nGF_SECURITY_ADMIN_PASSWORD=%s\n' '$PASSWORD' > '$TARGET_DIR/.env'"
fi

# Alarms (grafana/provisioning/alerting/) go to ntfy; asked once, kept in .env like the password.
if ! in_ct "grep -q '^NTFY_URL=' '$TARGET_DIR/.env'"; then
  if [ -z "$NTFY_URL" ] && [ -t 0 ]; then
    read -r -p "ntfy server for alarms (e.g. https://slipways-1.tail1234.ts.net:2586, empty to skip): " NTFY_URL
  fi
  if [ -n "$NTFY_URL" ]; then
    NTFY_TOPIC="${NTFY_TOPIC:-alarme-$(openssl rand -hex 4)}"
    in_ct "printf 'NTFY_URL=%s\nNTFY_TOPIC=%s\n' '${NTFY_URL%/}' '$NTFY_TOPIC' >> '$TARGET_DIR/.env'"
  fi
fi
NTFY_TOPIC="$(in_ct "sed -n 's/^NTFY_TOPIC=//p' '$TARGET_DIR/.env'" || true)"

if ! in_ct "test -f '$TARGET_DIR/prometheus/targets/tonwerk.yml'"; then
  if [ -z "$TONWERK_TARGET" ] && [ -t 0 ]; then
    read -r -p "Tailscale name of the Mac with port (e.g. mac-mini.tail1234.ts.net:8443, empty to skip): " TONWERK_TARGET
  fi
  if [ -n "$TONWERK_TARGET" ]; then
    in_ct "cat > '$TARGET_DIR/prometheus/targets/tonwerk.yml' <<YAML
- targets: [$TONWERK_TARGET]
  labels:
    app: tonwerk
    __scheme__: https
    __metrics_path__: /metrics
YAML"
  fi
fi

# Proxmox: a read-only user and token, made here on the host. The token's value is written only into the container's
# pve.yml (through a file of mode 600 that is deleted at once), never printed or kept on the host.
if [ "$PVE_EXPORTER" = 1 ]; then
  if ! in_ct "test -s '$TARGET_DIR/pve.yml'"; then
    echo "Creating the read-only Proxmox user monitoring@pve and its token ..."
    pveum user add monitoring@pve --comment "Monitoring (LXC $CT_HOSTNAME), nur lesen" >/dev/null 2>&1 || true
    pveum acl modify / --users monitoring@pve --roles PVEAuditor
    # A token's value is shown only when it is made: an old one without its pve.yml is of no use.
    pveum user token remove monitoring@pve exporter >/dev/null 2>&1 || true
    TOKEN="$(pveum user token add monitoring@pve exporter --privsep 1 --comment "pve exporter in LXC $CT_HOSTNAME" --output-format json \
      | sed -n 's/.*"value"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')"
    [ -n "$TOKEN" ] || { echo "Proxmox gave no token value." >&2; exit 1; }
    # A privilege-separated token only has what its own ACL grants, within the user's.
    pveum acl modify / --tokens 'monitoring@pve!exporter' --roles PVEAuditor
    PVE_FILE="$(mktemp)"
    chmod 600 "$PVE_FILE"
    printf 'default:\n  user: monitoring@pve\n  token_name: exporter\n  token_value: %s\n  verify_ssl: false\n' "$TOKEN" > "$PVE_FILE"
    unset TOKEN
    pct push "$CTID" "$PVE_FILE" "$TARGET_DIR/pve.yml" --perms 600
    rm -f "$PVE_FILE"
  fi
  in_ct "grep -q '^COMPOSE_PROFILES=' '$TARGET_DIR/.env' || echo COMPOSE_PROFILES=pve >> '$TARGET_DIR/.env'"
  if ! in_ct "test -f '$TARGET_DIR/prometheus/pve/proxmox.yml'"; then
    HOST_IP="$(ip -4 -o addr show "$BRIDGE" | awk '{ print $4 }' | cut -d/ -f1 | head -n 1)"
    [ -n "$HOST_IP" ] || { echo "No address on $BRIDGE for the exporter to ask." >&2; exit 1; }
    in_ct "mkdir -p '$TARGET_DIR/prometheus/pve' && printf -- '- targets: [%s]\n  labels:\n    app: proxmox\n' '$HOST_IP' > '$TARGET_DIR/prometheus/pve/proxmox.yml'"
  fi
fi

echo "Starting Prometheus, Loki, Grafana, the blackbox exporter$( [ "$PVE_EXPORTER" = 1 ] && echo ' and the pve exporter') ..."
in_ct "cd '$TARGET_DIR' && docker compose pull -q && docker compose up -d --remove-orphans"

NAME="$(in_ct "tailscale status --json 2>/dev/null | sed -n 's/.*\"DNSName\": *\"\\([^\"]*\\)\\.\".*/\\1/p' | head -n 1" || true)"
NAME="${NAME:-$CT_HOSTNAME}"
echo
echo "Done. Container $CTID, in the tailnet as $NAME."
echo "  Grafana:    http://$NAME:3000  (user admin)"
[ -n "$PASSWORD" ] && echo "  Password:   $PASSWORD  (also in $TARGET_DIR/.env inside the container)"
echo "  Prometheus: http://$NAME:9090/targets"
echo "  For Tonwerk, in appsettings.Production.json on the Mac:"
echo "    \"Monitoring\": { \"Loki\": { \"Url\": \"http://$CT_HOSTNAME:3100\", \"Host\": \"mac-mini\" } }"
if [ -n "$NTFY_TOPIC" ]; then
  echo "  Alarms:     ntfy topic $NTFY_TOPIC; subscribe to it in the ntfy app on that server."
else
  echo "  Alarms:     no ntfy server yet: run again with NTFY_URL=https://<server>:<port>."
fi
if in_ct "test -s '$TARGET_DIR/pve.yml'"; then
  echo "  Proxmox:    read-only user monitoring@pve (token exporter, PVEAuditor); revoke under Datacenter > Permissions."
else
  echo "  Proxmox:    not watched yet: run again with PVE_EXPORTER=1."
fi
[ -z "$TONWERK_TARGET" ] && ! in_ct "test -f '$TARGET_DIR/prometheus/targets/tonwerk.yml'" && \
  echo "  No target for Tonwerk yet: run again with TONWERK_TARGET=<Mac>.<tailnet>.ts.net:8443."
exit 0
