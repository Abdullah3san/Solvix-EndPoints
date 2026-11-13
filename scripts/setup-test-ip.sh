#!/usr/bin/env bash
# TEST MODE setup (see docker-compose.test-ip.yml): prepares a server to run the
# production stack on its bare IP before a domain and SMTP exist. Run once, as root,
# from the repository root:
#
#   ./scripts/setup-test-ip.sh <server-ip>
#
# It is safe to re-run: existing secrets, keys and certificates are never replaced.
# It creates/updates .env.production (generated secrets + test-mode values), creates
# the agent PKI (scripts/init-agent-pki.sh) and a self-signed agent TLS certificate
# for the IP, then prints the start command.
set -euo pipefail
cd "$(dirname "$0")/.."

IP="${1:-}"
ENV_FILE=.env.production
SECRETS_DIR=./secrets
APP_UID=1654
HTTP_PORT="${SOLVIX_HTTP_PORT:-8080}"
AGENT_PORT="${SOLVIX_AGENT_PORT:-8443}"

[[ $IP =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]] || { echo "usage: $0 <server-ipv4>" >&2; exit 1; }
[[ $EUID -eq 0 ]] || { echo "Run as root: secret files must be owned by uid $APP_UID." >&2; exit 1; }
command -v openssl >/dev/null || { echo "openssl is required (apt install openssl)." >&2; exit 1; }
command -v docker >/dev/null || { echo "Docker is required." >&2; exit 1; }

# --- pre-flight: ports and the backend network subnet ------------------------------
for port in "$HTTP_PORT" "$AGENT_PORT"; do
  if ss -ltnH "( sport = :$port )" 2>/dev/null | grep -q .; then
    if ! docker ps --filter "label=com.docker.compose.project=solvix" --format '{{.Ports}}' | grep -q ":$port->"; then
      echo "Port $port is already in use on this server. Stop whatever uses it, or set" >&2
      echo "SOLVIX_HTTP_PORT / SOLVIX_AGENT_PORT to free ports and re-run." >&2
      exit 1
    fi
  fi
done
if docker network ls -q | xargs -r docker network inspect --format '{{.Name}} {{range .IPAM.Config}}{{.Subnet}} {{end}}' \
    | grep -v '^solvix_backend ' | grep -q '172\.30\.10\.'; then
  echo "Another Docker network already uses 172.30.10.0/24, which docker-compose.prod.yml" >&2
  echo "needs for its backend network. Change that subnet in docker-compose.prod.yml first." >&2
  exit 1
fi

# --- .env.production -------------------------------------------------------------
get_var() { grep -E "^$1=" "$ENV_FILE" | tail -n1 | cut -d= -f2- || true; }
set_var() {
  if grep -qE "^$1=" "$ENV_FILE"; then
    sed -i "s|^$1=.*|$1=$2|" "$ENV_FILE"
  else
    printf '%s=%s\n' "$1" "$2" >> "$ENV_FILE"
  fi
}

[[ -f $ENV_FILE ]] || cp .env.production.example "$ENV_FILE"
chmod 600 "$ENV_FILE"

# Secrets are generated once and never replaced (database passwords only apply when
# the data volume is created; the MFA and CA keys must never change).
for key in POSTGRES_PASSWORD SOLVIX_DB_MIGRATOR_PASSWORD SOLVIX_DB_APP_PASSWORD REDIS_PASSWORD \
           SOLVIX_JWT_SIGNING_KEY SOLVIX_IDENTITY_DATA_PROTECTION_KEY SOLVIX_DEVICE_CA_PASSWORD; do
  [[ -n "$(get_var "$key")" ]] || set_var "$key" "$(openssl rand -hex 32)"
done

# docker-compose.prod.yml requires these; in test mode they are placeholders that
# docker-compose.test-ip.yml overrides (no ACME, no SMTP delivery).
set_var SOLVIX_DOMAIN "$IP"
set_var SOLVIX_PUBLIC_HOST "$IP"
set_var SOLVIX_HTTP_PORT "$HTTP_PORT"
set_var SOLVIX_AGENT_PORT "$AGENT_PORT"

# --- agent TLS certificate (self-signed for the IP) ------------------------------
mkdir -p "$SECRETS_DIR" && chmod 700 "$SECRETS_DIR"
if [[ ! -e $SECRETS_DIR/agent-tls.crt || ! -e $SECRETS_DIR/agent-tls.key ]]; then
  openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -sha256 -days 825 \
    -subj "/CN=$IP" \
    -addext "subjectAltName=IP:$IP" \
    -addext "extendedKeyUsage=serverAuth" \
    -addext "keyUsage=critical,digitalSignature" \
    -keyout "$SECRETS_DIR/agent-tls.key" -out "$SECRETS_DIR/agent-tls.crt" 2>/dev/null
  echo "Created a self-signed agent TLS certificate for $IP."
fi
chown "$APP_UID:$APP_UID" "$SECRETS_DIR/agent-tls.crt" "$SECRETS_DIR/agent-tls.key"
chmod 600 "$SECRETS_DIR/agent-tls.crt" "$SECRETS_DIR/agent-tls.key"
# Agents compare the SHA-256 of the certificate (uppercase hex, no separators).
PIN="$(openssl x509 -in "$SECRETS_DIR/agent-tls.crt" -noout -fingerprint -sha256 | cut -d= -f2 | tr -d ':')"
set_var SOLVIX_AGENT_SERVER_CERT_SHA256 "$PIN"

# --- device CA and command signing keys ------------------------------------------
if ! pki_output="$(ENV_FILE="$ENV_FILE" SOLVIX_SECRETS_DIR="$SECRETS_DIR" ./scripts/init-agent-pki.sh 2>&1)"; then
  echo "$pki_output" >&2
  exit 1
fi
echo "$pki_output"
standby="$(printf '%s\n' "$pki_output" | grep -o 'SOLVIX_COMMAND_STANDBY_PUBLIC_KEY=.*' | head -n1 | cut -d= -f2- || true)"
if [[ -n $standby ]]; then
  set_var SOLVIX_COMMAND_STANDBY_PUBLIC_KEY "$standby"
fi

cat <<EOF

Test-mode setup complete.
  Web:               http://$IP:$HTTP_PORT/register
  Agent endpoint:    https://$IP:$AGENT_PORT/  (pin $PIN)

Start (or update) the stack:
  docker compose -f docker-compose.prod.yml -f docker-compose.test-ip.yml --env-file $ENV_FILE up -d --build

Back up OFF the server now: $ENV_FILE, $SECRETS_DIR/device-ca.pfx, $SECRETS_DIR/command-signing.pem,
and move $SECRETS_DIR/command-signing-standby.pem off the server (it must not stay here).
EOF
