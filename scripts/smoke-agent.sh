#!/usr/bin/env bash
# End-to-end smoke test of the agent AS A COMMAND against a running Solvix API.
#
# Registers a throw-away workspace with two stores, creates one enrollment token
# per store, then runs the published agent binary's CLI (enroll + run) for three
# terminals and verifies that each device appears in the right branch/department.
#
#   scripts/smoke-agent.sh <api-base> <agent-url> [agent-binary] [server-cert-sha256]
#   scripts/smoke-agent.sh http://localhost:8080 https://localhost:8443/ ./artifacts/agent/Solvix.Agent
#
# Without an agent binary the script publishes one for the current OS. A pin is
# only needed when the agent endpoint uses a self-signed certificate; if omitted
# it is read from the endpoint with openssl.
set -euo pipefail
API="${1:?api base url, e.g. http://localhost:8080}"
AGENT_URL="${2:?agent endpoint, e.g. https://localhost:8443/}"
AGENT="${3:-}"
PIN="${4:-}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
V1="$API/api/v1"
JSON='Content-Type: application/json'
fail() { echo "FAIL: $*" >&2; exit 1; }
field() { python3 -c "import sys,json;print(json.load(sys.stdin)$1)"; }

if [[ -z "$AGENT" ]]; then
  echo "== publishing the agent"
  dotnet publish "$(dirname "$0")/../src/agent/Solvix.Agent" -c Release -o "$WORK/bin" >/dev/null
  AGENT="$WORK/bin/Solvix.Agent"
fi
if [[ -z "$PIN" && "$AGENT_URL" == https://* ]]; then
  hostport="${AGENT_URL#https://}"; hostport="${hostport%%/*}"
  PIN="$(echo | openssl s_client -connect "$hostport" -servername "${hostport%%:*}" 2>/dev/null | openssl x509 -noout -fingerprint -sha256 | cut -d= -f2 | tr -d :)"
fi

slug="smoke-$RANDOM$RANDOM"
echo "== registering workspace $slug"
JWT="$(curl -fsS -X POST "$V1/tenant/register" -H "$JSON" \
  -d "{\"companyName\":\"Smoke Retail\",\"slug\":\"$slug\",\"ownerEmail\":\"owner@$slug.test\",\"ownerDisplayName\":\"Owner\",\"ownerPassword\":\"Passw0rd!Secure\"}" | field '["auth"]["accessToken"]')"
post() { curl -fsS -X POST "$V1/$1" -H "Authorization: Bearer $JWT" -H "$JSON" -d "$2"; }
get() { curl -fsS "$V1/$1" -H "Authorization: Bearer $JWT"; }

BER="$(post branches '{"name":"Berlin store","code":"BER"}' | field '["id"]')"
PAR="$(post branches '{"name":"Paris store","code":"PAR"}' | field '["id"]')"
TILLS="$(post departments "{\"branchId\":\"$BER\",\"name\":\"Tills\",\"code\":\"TILL\"}" | field '["id"]')"
GRP="$(post device-groups '{"name":"POS terminals"}' | field '["id"]')"
T_BER="$(post enrollment-tokens "{\"name\":\"Berlin tills\",\"maxUses\":2,\"deviceGroupId\":\"$GRP\",\"departmentId\":\"$TILLS\"}" | field '["token"]')"
T_PAR="$(post enrollment-tokens "{\"name\":\"Paris POS\",\"maxUses\":1,\"deviceGroupId\":\"$GRP\",\"branchId\":\"$PAR\"}" | field '["token"]')"

export SolvixAgent__Updates__Enabled=false
pin_args=(); [[ -n "$PIN" ]] && pin_args=(--pin "$PIN")
for terminal in "berlin-1:$T_BER" "berlin-2:$T_BER" "paris-1:$T_PAR"; do
  name="${terminal%%:*}"; token="${terminal#*:}"
  export SolvixAgent__DataDirectory="$WORK/$name"
  echo "== terminal $name"
  "$AGENT" enroll --server "$AGENT_URL" --token "$token" "${pin_args[@]}" >/dev/null
  timeout 12 "$AGENT" >"$WORK/$name.log" 2>&1 || true
  grep -q "Enrolled as device" "$WORK/$name.log" || { cat "$WORK/$name.log"; fail "$name did not enroll"; }
  "$AGENT" status | grep -q "Enrolled: *True" || fail "$name status does not say enrolled"
done

echo "== verifying on the platform"
devices="$(get 'devices?pageSize=50')"
[[ "$(echo "$devices" | field '["totalCount"]')" == "3" ]] || fail "expected 3 devices"
berlin=0; paris=0
for id in $(echo "$devices" | python3 -c 'import sys,json;[print(d["id"]) for d in json.load(sys.stdin)["items"]]'); do
  d="$(get "devices/$id")"
  branch="$(echo "$d" | field '["assignment"]["branch"]["name"]')"
  dept="$(echo "$d" | python3 -c 'import sys,json;x=json.load(sys.stdin)["assignment"]["department"];print(x["name"] if x else "")')"
  [[ "$(echo "$d" | field '["summary"]["connectivity"]')" == "Online" ]] || fail "$id not online"
  [[ "$(echo "$d" | field '["groups"][0]["name"]')" == "POS terminals" ]] || fail "$id not in the group"
  if [[ "$branch" == "Berlin store" && "$dept" == "Tills" ]]; then berlin=$((berlin+1)); fi
  if [[ "$branch" == "Paris store" && -z "$dept" ]]; then paris=$((paris+1)); fi
done
[[ $berlin == 2 && $paris == 1 ]] || fail "placement wrong (berlin=$berlin paris=$paris)"

echo "== exhausted token is refused"
export SolvixAgent__DataDirectory="$WORK/berlin-3"
"$AGENT" enroll --server "$AGENT_URL" --token "$T_BER" "${pin_args[@]}" >/dev/null
timeout 8 "$AGENT" >"$WORK/berlin-3.log" 2>&1 || true
"$AGENT" status | grep -q "REJECTED" || fail "rejected token not reported by status"

echo "OK: 3 terminals enrolled via the agent command and placed in the right store/department"
