#!/usr/bin/env bash
# End-to-end smoke test of the agent PACKAGE flow against a running Solvix API.
#
# Registers a throw-away workspace with two stores, creates one enrollment token per store, then does what an
# administrator does: downloads the agent package (zip) for each token, extracts it, and runs the agent from the
# extracted folder. The server URL and token come from the package's enrollment.json -- nothing is pasted.
# Verifies each device lands in the right branch/department and that tokens are single-purpose.
#
# The API must have an agent package configured (Solvix:Devices:AgentPackage:Directory with the agent binary named
# Solvix.Agent.exe -- built into the Docker image). To run the agent on this machine the package must contain a
# build for this OS (e.g. `dotnet publish src/agent/Solvix.Agent -c Release -r linux-x64`, copied to the package
# directory as Solvix.Agent.exe). The agent endpoint must be reachable at the URL configured as PublicServerUrl.
#
#   scripts/smoke-agent.sh <api-base>
#   scripts/smoke-agent.sh http://localhost:8080
set -euo pipefail
API="${1:?api base url, e.g. http://localhost:8080}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
V1="$API/api/v1"
JSON='Content-Type: application/json'
fail() { echo "FAIL: $*" >&2; exit 1; }
field() { python3 -c "import sys,json;print(json.load(sys.stdin)$1)"; }

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
run_terminal() { # <name> <token>
  local name="$1" token="$2" dir="$WORK/$1"
  mkdir -p "$dir"
  echo "== terminal $name: download package"
  curl -fsS -X POST "$V1/enrollment-tokens/package" -H "Authorization: Bearer $JWT" -H "$JSON" -d "{\"token\":\"$token\"}" -o "$dir/package.zip"
  python3 -m zipfile -e "$dir/package.zip" "$dir/package"
  [[ -f "$dir/package/Solvix.Agent.exe" && -f "$dir/package/enrollment.json" ]] || fail "$name: package is incomplete"
  chmod +x "$dir/package/Solvix.Agent.exe"
  export SolvixAgent__DataDirectory="$dir/data"
  "$dir/package/Solvix.Agent.exe" enroll >/dev/null          # no --server/--token: read from enrollment.json
  [[ ! -e "$dir/package/enrollment.json" ]] || fail "$name: enrollment.json was not deleted after use"
  timeout 12 "$dir/package/Solvix.Agent.exe" run >"$dir/agent.log" 2>&1 || true
  grep -q "Enrolled as device" "$dir/agent.log" || { cat "$dir/agent.log"; fail "$name did not enroll"; }
}
run_terminal berlin-1 "$T_BER"
run_terminal berlin-2 "$T_BER"
run_terminal paris-1 "$T_PAR"

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

echo "== a used-up token can no longer be packaged or enrolled"
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$V1/enrollment-tokens/package" -H "Authorization: Bearer $JWT" -H "$JSON" -d "{\"token\":\"$T_PAR\"}")"
[[ "$code" == "409" ]] || fail "expected 409 for an exhausted token, got $code"

echo "OK: 3 terminals enrolled from downloaded packages and placed in the right store/department"
