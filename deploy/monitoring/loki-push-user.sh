#!/bin/sh
# Gives a device a password for the push gate (loki-push/nginx.conf, port 3101): a new random one each time, written
# as an apr1 hash into loki-push/htpasswd and printed once here, never stored in clear. Run it in the monitoring
# container, or from the Proxmox host: pct exec <CTID> -- /opt/monitoring/loki-push-user.sh deskbuddy
#   loki-push-user.sh <name>           new password for <name> (an old one stops working)
#   loki-push-user.sh --remove <name>  <name> can no longer push
set -eu
cd "$(dirname "$0")"
FILE=loki-push/htpasswd

remove=0
if [ "${1:-}" = "--remove" ]; then remove=1; shift; fi
name="${1:-}"
case "$name" in
  ""|*[!A-Za-z0-9._-]*) echo "usage: $0 [--remove] <name>   (letters, digits, . _ -)" >&2; exit 2 ;;
esac

touch "$FILE"
# nginx' workers run as their own user inside the container; the file holds only hashes.
chmod 644 "$FILE"
kept="$(grep -v "^$name:" "$FILE" || true)"

if [ "$remove" = 1 ]; then
  printf '%s\n' "$kept" | sed '/^$/d' > "$FILE"
  echo "$name can no longer push."
  exit 0
fi

password="$(openssl rand -base64 18 | tr -d '/+=' | cut -c1-20)"
hash="$(openssl passwd -apr1 "$password")"
{ printf '%s\n' "$kept" | sed '/^$/d'; echo "$name:$hash"; } > "$FILE"

echo "Push gate for $name (POST /loki/api/v1/push on port 3101, Basic Auth):"
echo "  user     = \"$name\""
echo "  password = \"$password\""
echo "Shown only now; running this again gives a new one."
