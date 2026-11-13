#!/usr/bin/env bash
# Creates the production agent PKI ONCE, before the first start of
# docker-compose.prod.yml. Run from anywhere as the deploy user (docker group):
#   ./scripts/init-agent-pki.sh
#
#   secrets/device-ca.pfx         device CA (ECDSA P-384, 10 years), PKCS#12 encrypted with
#                                 SOLVIX_DEVICE_CA_PASSWORD from .env.production. Signs the
#                                 client certificate of every enrolled device.
#   secrets/command-signing.pem   ECDSA P-256 private key (PKCS#8) that signs remote
#                                 management commands. Agents pin its public key.
#   secrets/command-signing-standby.pem
#                                 a second P-256 key for a future rotation. Its public key
#                                 is printed: put it in SOLVIX_COMMAND_STANDBY_PUBLIC_KEY
#                                 BEFORE enrolling devices, then move this file OFF the
#                                 server. Agents pin the key set once and never accept a
#                                 new key later, so without a pre-published standby key the
#                                 command key can only be replaced by re-enrolling devices.
#
# Existing files are never overwritten: replacing the CA forces every device to
# re-enroll, and replacing the command key makes agents reject all commands.
# Back both files up OFF the server together with .env.production.
#
# Requires openssl on the host (installed by default on Ubuntu/Debian). The keys are
# generated in a private temporary directory and installed by a one-off Caddy
# container (no ports, no network use) so the files can be owned by the API
# container's "app" user (uid 1654) without sudo. Nothing leaves the host.
set -euo pipefail
cd "$(dirname "$0")/.."

ENV_FILE="${ENV_FILE:-.env.production}"
SECRETS_DIR="${SOLVIX_SECRETS_DIR:-./secrets}"
APP_UID=1654
CA_SUBJECT="${SOLVIX_DEVICE_CA_SUBJECT:-/CN=Solvix Device CA/O=Solvix EndPoints}"
COMPOSE=(docker compose -f docker-compose.prod.yml --env-file "$ENV_FILE")

command -v openssl >/dev/null || { echo "openssl is required (apt install openssl)." >&2; exit 1; }
[[ -f "$ENV_FILE" ]] || { echo "Missing $ENV_FILE (copy .env.production.example first)." >&2; exit 1; }
CA_PASSWORD="$(grep -E '^SOLVIX_DEVICE_CA_PASSWORD=' "$ENV_FILE" | tail -n1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//')"
if [[ ${#CA_PASSWORD} -lt 16 ]]; then
  echo "Set SOLVIX_DEVICE_CA_PASSWORD in $ENV_FILE (at least 16 characters, e.g. openssl rand -hex 32)." >&2
  exit 1
fi
export CA_PASSWORD

mkdir -p "$SECRETS_DIR" && chmod 700 "$SECRETS_DIR"
SECRETS_DIR="$(cd "$SECRETS_DIR" && pwd)"
if [[ -e "$SECRETS_DIR/device-ca.pfx" && -e "$SECRETS_DIR/command-signing.pem" ]]; then
  echo "Device CA and command signing key already exist in $SECRETS_DIR; nothing to do."
  exit 0
fi

umask 077
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

if [[ ! -e "$SECRETS_DIR/device-ca.pfx" ]]; then
  openssl ecparam -name secp384r1 -genkey -noout -out "$WORK/ca.key"
  # 128-bit positive serial.
  serial="$(openssl rand -hex 16 | sed 's/^[89a-f]/7/')"
  openssl req -new -x509 -sha384 -key "$WORK/ca.key" -subj "$CA_SUBJECT" \
    -days 3650 -set_serial "0x$serial" \
    -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
    -addext "keyUsage=critical,keyCertSign,cRLSign,digitalSignature" \
    -addext "subjectKeyIdentifier=hash" \
    -out "$WORK/device-ca.crt"
  openssl pkcs12 -export -inkey "$WORK/ca.key" -in "$WORK/device-ca.crt" -name "Solvix Device CA" \
    -keypbe AES-256-CBC -certpbe AES-256-CBC -macalg sha256 -iter 100000 \
    -passout env:CA_PASSWORD -out "$WORK/device-ca.pfx"
  rm -f "$WORK/ca.key"
  echo "Device CA:"
  openssl x509 -in "$WORK/device-ca.crt" -noout -subject -enddate -fingerprint -sha256
fi

key_id() { openssl pkey -in "$1" -pubout -outform DER | openssl dgst -sha256 -r | cut -c1-16 | sed 's/^/cmd-/'; }

if [[ ! -e "$SECRETS_DIR/command-signing.pem" ]]; then
  openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$WORK/command-signing.pem"
  echo "Command signing key: $(key_id "$WORK/command-signing.pem")"
  if [[ ! -e "$SECRETS_DIR/command-signing-standby.pem" ]]; then
    openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$WORK/command-signing-standby.pem"
    install -m 0600 "$WORK/command-signing-standby.pem" "$SECRETS_DIR/command-signing-standby.pem"
    echo "Standby command signing key: $(key_id "$WORK/command-signing-standby.pem")"
    echo "  Add to $ENV_FILE before enrolling any device:"
    echo "  SOLVIX_COMMAND_STANDBY_PUBLIC_KEY=$(openssl pkey -in "$WORK/command-signing-standby.pem" -pubout -outform DER | base64 | tr -d '\n')"
    echo "  Then move $SECRETS_DIR/command-signing-standby.pem OFF the server (offline backup)."
  fi
fi

# Install with the right owner; never replaces an existing file.
"${COMPOSE[@]}" run --rm --no-deps -T --entrypoint sh -e APP_UID="$APP_UID" \
  -v "$WORK:/in:ro" -v "$SECRETS_DIR:/out" caddy -eu -c '
    for f in device-ca.pfx command-signing.pem; do
      if [ -f "/in/$f" ] && [ ! -e "/out/$f" ]; then
        install -m 0600 -o "$APP_UID" -g "$APP_UID" "/in/$f" "/out/$f"
      fi
    done
    if [ -f /in/device-ca.crt ] && [ ! -e /out/device-ca.crt ]; then
      install -m 0644 /in/device-ca.crt /out/device-ca.crt
    fi
  '

echo "Done. Back up $SECRETS_DIR/device-ca.pfx and $SECRETS_DIR/command-signing.pem off the server."
