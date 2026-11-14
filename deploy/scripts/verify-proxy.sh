#!/usr/bin/env bash
# Verifies the agent endpoint's security after a deploy or a Caddy/proxy change.
#
#   deploy/scripts/verify-proxy.sh https://agents.example.com                     # exposure + no-certificate checks
#   deploy/scripts/verify-proxy.sh https://agents.example.com device.crt device.key   # + genuine / forgery checks
#
# The device certificate and key come from a TEST agent you enrolled (e.g. the Linux agent's data directory:
# device.crt and keys/*.pem). Windows agents keep their key non-exportable, so use a test agent for this.
# Extra curl options (e.g. --resolve host:443:IP, -k for a private CA) go in CURL_OPTS.
set -uo pipefail
BASE="${1:?agents base url, e.g. https://agents.example.com}"; BASE="${BASE%/}"
CERT="${2:-}"; KEY="${3:-}"
read -r -a EXTRA <<<"${CURL_OPTS:-}"
BODY='{"agentVersion":"1.0.0","health":{"status":"Healthy","startedAtUtc":"2026-01-01T00:00:00Z","uptimeSeconds":1}}'
failures=0
check() { # <description> <expected> <actual>
  if [[ "$2" == "$3" ]]; then printf '  ok    %-62s %s\n' "$1" "$3"; else printf '  FAIL  %-62s got %s, want %s\n' "$1" "$3" "$2"; failures=$((failures+1)); fi
}
code() { curl -s -o /dev/null -w '%{http_code}' "${EXTRA[@]}" "$@"; }

echo "Only agent routes are exposed on the agent hostname"
for path in /api/v1/devices /api/v1/tenant/register /api/v1/enrollment-tokens /api/v1/auth/login /health/ready /openapi/v1.json /scalar/v1; do
  check "GET $path" 404 "$(code "$BASE$path")"
done
echo "Agent routes require a device certificate"
check "POST /api/v1/agent/heartbeat without a certificate" 401 "$(code -X POST "$BASE/api/v1/agent/heartbeat" -H 'content-type: application/json' -d "$BODY")"

if [[ -n "$CERT" && -n "$KEY" ]]; then
  der="$(openssl x509 -in "$CERT" -outform DER | base64 -w0)"
  echo "Certificate forwarding"
  check "genuine device certificate (mutual TLS)" 200 "$(code --cert "$CERT" --key "$KEY" -X POST "$BASE/api/v1/agent/heartbeat" -H 'content-type: application/json' -d "$BODY")"
  check "FORGED X-Client-Cert header, no TLS certificate" 401 "$(code -X POST "$BASE/api/v1/agent/heartbeat" -H 'content-type: application/json' -H "X-Client-Cert: $der" -d "$BODY")"
  tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
  openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -keyout "$tmp/k.pem" -out "$tmp/c.pem" -subj "/CN=attacker" -days 1 2>/dev/null
  check "FORGED header + attacker's own TLS certificate" 401 "$(code --cert "$tmp/c.pem" --key "$tmp/k.pem" -X POST "$BASE/api/v1/agent/heartbeat" -H 'content-type: application/json' -H "X-Client-Cert: $der" -d "$BODY")"
else
  echo "(no device certificate given: genuine/forgery checks skipped)"
fi
echo
if [[ $failures -eq 0 ]]; then echo "PASS"; else echo "$failures check(s) FAILED - do not put devices on this endpoint until fixed"; exit 1; fi
