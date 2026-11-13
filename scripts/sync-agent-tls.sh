#!/usr/bin/env bash
# Installs the TLS server certificate of the agent endpoint (port 8443) from the
# certificate Caddy obtains and renews for SOLVIX_DOMAIN, and restarts the API when
# it changed. Run once before the first full start and then daily from cron:
#   20 4 * * * cd /opt/solvix && ./scripts/sync-agent-tls.sh >> /var/log/solvix-agent-tls.log 2>&1
#
# Caddy renews about 30 days before expiry, so a daily run keeps the agent endpoint
# current with a few seconds of API restart roughly every 60 days. Kestrel loads the
# certificate at start-up only, hence the restart.
#
# To use a certificate from another source instead, put the PEM chain and key at
# secrets/agent-tls.crt and secrets/agent-tls.key (owner uid 1654, mode 0600) and
# do not schedule this script.
set -euo pipefail
cd "$(dirname "$0")/.."

ENV_FILE="${ENV_FILE:-.env.production}"
SECRETS_DIR="${SOLVIX_SECRETS_DIR:-./secrets}"
APP_UID=1654
COMPOSE=(docker compose -f docker-compose.prod.yml --env-file "$ENV_FILE")

[[ -f "$ENV_FILE" ]] || { echo "Missing $ENV_FILE." >&2; exit 1; }
DOMAIN="$(grep -E '^SOLVIX_DOMAIN=' "$ENV_FILE" | tail -n1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//')"
[[ "$DOMAIN" =~ ^[A-Za-z0-9.-]+$ ]] || { echo "SOLVIX_DOMAIN is missing or invalid in $ENV_FILE." >&2; exit 1; }

mkdir -p "$SECRETS_DIR" && chmod 700 "$SECRETS_DIR"
SECRETS_DIR="$(cd "$SECRETS_DIR" && pwd)"

# Runs in a one-off Caddy container (same caddy-data volume, no ports published) so
# the files can be owned by the API's "app" user without sudo. Exit code 3 = unchanged.
set +e
"${COMPOSE[@]}" run --rm --no-deps -T --entrypoint sh -e DOMAIN="$DOMAIN" -e APP_UID="$APP_UID" \
  -v "$SECRETS_DIR:/out" caddy -eu -c '
    crt="$(find /data/caddy/certificates -type f -path "*/$DOMAIN/$DOMAIN.crt" -exec ls -t {} + 2>/dev/null | head -n1)"
    if [ -z "$crt" ]; then
      echo "Caddy has no certificate for $DOMAIN yet. Start Caddy first (see docs/DEPLOYMENT-VPS.md)." >&2
      exit 2
    fi
    key="${crt%.crt}.key"
    if [ -f /out/agent-tls.crt ] && [ -f /out/agent-tls.key ] && cmp -s "$crt" /out/agent-tls.crt && cmp -s "$key" /out/agent-tls.key; then
      exit 3
    fi
    install -m 0600 -o "$APP_UID" -g "$APP_UID" "$key" /out/agent-tls.key.new
    install -m 0644 -o "$APP_UID" -g "$APP_UID" "$crt" /out/agent-tls.crt.new
    mv -f /out/agent-tls.key.new /out/agent-tls.key
    mv -f /out/agent-tls.crt.new /out/agent-tls.crt
    echo "Installed agent TLS certificate from $crt"
  '
status=$?
set -e

case "$status" in
  0)
    # Restart only if the API is running; on the first deploy "up" starts it later.
    if [[ -n "$("${COMPOSE[@]}" ps -q api 2>/dev/null)" ]]; then
      "${COMPOSE[@]}" restart api
      echo "$(date -u +%FT%TZ) agent TLS certificate updated; api restarted"
    else
      echo "$(date -u +%FT%TZ) agent TLS certificate installed"
    fi
    ;;
  3) echo "$(date -u +%FT%TZ) agent TLS certificate unchanged" ;;
  *) exit "$status" ;;
esac
