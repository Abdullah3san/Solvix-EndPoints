#!/usr/bin/env bash
# End-to-end disaster-recovery drill against a REAL Solvix stack, on a throw-away database.
#
#   PGHOST=localhost PGUSER=postgres PGPASSWORD=postgres ./scripts/ops/dr-drill.sh
#
# What it proves (and prints):
#   1. a database built by the real migrator + populated through the real API (two tenants: tickets, an attachment,
#      an inbound e-mail ticket, a subscription change/invoice, billing profile);
#   2. an encrypted backup is produced, checksummed and verified;
#   3. the source database is DESTROYED, the backup restored into a new database and verify-restore.sh passes;
#   4. the application, started against the restored database with the restricted runtime role, still serves the same
#      data (ticket, byte-identical attachment, subscription) and still isolates the two tenants;
#   5. corruption / wrong key / overwrite attempts are refused.
# Timings are reported so you can quote a measured RTO for your data volume.
#
# Requirements: bash, psql/pg_dump/pg_restore, curl, jq, openssl, the .NET SDK and a Redis on localhost:6379
# (or REDIS=host:port). The API/migrator are built from this checkout. Set KEEP=1 to keep the databases.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OPS="$ROOT/scripts/ops"
SRC="${DRILL_SRC_DB:-solvix_drill_src}"
DST="${DRILL_DST_DB:-solvix_drill_restored}"
PORT="${DRILL_API_PORT:-18080}"
REDIS="${REDIS:-localhost:6379}"
WORK="$(mktemp -d)"
API_PID=""
MIGRATOR_PW="${SOLVIX_DB_MIGRATOR_PASSWORD:-solvix_migrator_dev}"
APP_PW="${SOLVIX_DB_APP_PASSWORD:-solvix_app_dev}"
export SOLVIX_DB_APP_USER="${SOLVIX_DB_APP_USER:-solvix_app}" SOLVIX_DB_APP_PASSWORD="$APP_PW"
HOST="${PGHOST:-localhost}"
PG_PORT="${PGPORT:-5432}"

step() { printf '\n== %s\n' "$*"; }
need() { command -v "$1" > /dev/null || { echo "missing required tool: $1" >&2; exit 1; }; }
for t in psql pg_dump pg_restore curl jq openssl dotnet; do need "$t"; done

cleanup() {
  [ -n "$API_PID" ] && kill "$API_PID" 2> /dev/null || true
  if [ "${KEEP:-0}" != "1" ]; then
    psql -XAtq -d postgres -c "DROP DATABASE IF EXISTS \"$SRC\" WITH (FORCE)" > /dev/null 2>&1 || true
    psql -XAtq -d postgres -c "DROP DATABASE IF EXISTS \"$DST\" WITH (FORCE)" > /dev/null 2>&1 || true
    rm -rf "$WORK"
  else
    echo "KEEP=1: databases $SRC / $DST and $WORK left in place"
  fi
}
trap cleanup EXIT

conn() { echo "Host=$HOST;Port=$PG_PORT;Database=$1;Username=$2;Password=$3"; }

start_api() { # <db> -> exports API_PID, waits for readiness
  local db="$1"
  ASPNETCORE_ENVIRONMENT=Staging ASPNETCORE_URLS="http://127.0.0.1:$PORT" \
  ConnectionStrings__Postgres="$(conn "$db" "$SOLVIX_DB_APP_USER" "$APP_PW")" \
  ConnectionStrings__Redis="$REDIS" \
  Solvix__Redis__InstanceName="drill-$RANDOM:" \
  Solvix__Database__MigrateOnStartup=false \
  Solvix__Jwt__SigningKey="drill-signing-key-drill-signing-key-0123456789" \
  Solvix__Devices__CertificateAuthority__AutoGenerate=true \
  Solvix__RateLimits__AuthPerMinute=100000 \
  Solvix__Billing__Worker__Enabled=false Solvix__Helpdesk__Worker__Enabled=false Solvix__Reporting__AvailabilityWorkerEnabled=false \
  Solvix__Logging__Format=text Serilog__MinimumLevel__Default=Warning \
    dotnet "$ROOT/src/backend/Solvix.Api/bin/Debug/net10.0/Solvix.Api.dll" > "$WORK/api-$db.log" 2>&1 &
  API_PID=$!
  for _ in $(seq 1 60); do
    curl -fsS "http://127.0.0.1:$PORT/health/ready" > /dev/null 2>&1 && return 0
    kill -0 "$API_PID" 2> /dev/null || { echo "API exited; log:" >&2; tail -30 "$WORK/api-$db.log" >&2; exit 1; }
    sleep 1
  done
  echo "API did not become ready" >&2; tail -30 "$WORK/api-$db.log" >&2; exit 1
}
stop_api() { [ -n "$API_PID" ] && { kill "$API_PID" 2> /dev/null || true; wait "$API_PID" 2> /dev/null || true; API_PID=""; }; }

api() { # <method> <path> <token> [json] -> body (fails on HTTP >= 400)
  local m="$1" p="$2" t="$3" d="${4:-}"
  curl -fsS -X "$m" "http://127.0.0.1:$PORT/api/v1/$p" ${t:+-H "Authorization: Bearer $t"} ${d:+-H 'Content-Type: application/json' -d "$d"}
}

counts() { # <db> -> sorted "table count" lines for every public table
  for t in $(psql -XAtq -d "$1" -c "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace AND n.nspname='public' WHERE c.relkind='r' ORDER BY 1"); do
    printf '%s %s\n' "$t" "$(psql -XAtq -d "$1" -c "SELECT count(*) FROM \"$t\"")"
  done
}

step "0. Build"
( cd "$ROOT" && dotnet build src/backend/Solvix.Api -c Debug --nologo -v q > /dev/null && dotnet build src/backend/Solvix.Migrator -c Debug --nologo -v q > /dev/null )

step "1. Create database '$SRC' and apply migrations with the real migrator"
psql -XAtq -d postgres -c "DROP DATABASE IF EXISTS \"$SRC\" WITH (FORCE)" -c "DROP DATABASE IF EXISTS \"$DST\" WITH (FORCE)" > /dev/null
"$ROOT/scripts/create-local-db.sh" "$SRC" > /dev/null
ConnectionStrings__PostgresMigrator="$(conn "$SRC" "${SOLVIX_DB_MIGRATOR_USER:-solvix_migrator}" "$MIGRATOR_PW")" ConnectionStrings__Redis="$REDIS" \
  dotnet "$ROOT/src/backend/Solvix.Migrator/bin/Debug/net10.0/Solvix.Migrator.dll" > "$WORK/migrator.log" 2>&1
echo "migrations applied: $(psql -XAtq -d "$SRC" -c 'SELECT count(*) FROM "__EFMigrationsHistory"')"

step "2. Populate through the real API (two tenants)"
start_api "$SRC"
PNG_B64="iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
echo "$PNG_B64" | base64 -d > "$WORK/pixel.png"
declare -A TOKEN SLUG TICKET
for n in A B; do
  slug="drill-$(echo "$n" | tr 'A-Z' 'a-z')-$RANDOM"
  reg=$(api POST tenant/register "" "{\"companyName\":\"Drill $n Ltd\",\"slug\":\"$slug\",\"ownerEmail\":\"owner-$slug@example.test\",\"ownerDisplayName\":\"Owner $n\",\"ownerPassword\":\"Passw0rd!Secure\"}")
  TOKEN[$n]=$(jq -r .auth.accessToken <<< "$reg"); SLUG[$n]=$slug
  t=$(api POST helpdesk/tickets "${TOKEN[$n]}" "{\"subject\":\"Printer $n offline\",\"description\":\"The printer of company $n is offline since the morning.\"}")
  TICKET[$n]=$(jq -r .summary.id <<< "$t")
  api POST "helpdesk/tickets/${TICKET[$n]}/comments" "${TOKEN[$n]}" '{"body":"Checked the cable.","isInternal":false}' > /dev/null
  api PUT billing/profile "${TOKEN[$n]}" "{\"legalName\":\"Drill $n Ltd\",\"billingEmail\":\"ap-$slug@example.test\"}" > /dev/null
  api POST billing/subscription/change "${TOKEN[$n]}" '{"planCode":"business","interval":"Annual"}' > /dev/null
done
curl -fsS -X POST "http://127.0.0.1:$PORT/api/v1/helpdesk/tickets/${TICKET[A]}/attachments" -H "Authorization: Bearer ${TOKEN[A]}" -F "file=@$WORK/pixel.png;filename=screen.png" > "$WORK/attachment.json"
ATT_ID=$(jq -r .id "$WORK/attachment.json")
chan=$(api POST helpdesk/email-channels "${TOKEN[A]}" '{"name":"Support","defaultPriority":"High"}')
curl -fsS -X POST "http://127.0.0.1:$PORT/api/v1/helpdesk/inbound-email" -H "X-Channel-Token: $(jq -r .token <<< "$chan")" -H 'Content-Type: application/json' \
  -d '{"messageId":"<drill-1@mail.test>","from":{"email":"customer@client.test","name":"Customer"},"subject":"Cannot log in","text":"I cannot log in since this morning."}' > /dev/null
SUB_BEFORE=$(api GET billing/subscription "${TOKEN[A]}" | jq -S .)
ATT_SHA_BEFORE=$(psql -XAtq -d "$SRC" -c "SELECT encode(sha256(data),'hex') FROM ticket_attachment_contents LIMIT 1")
stop_api
counts "$SRC" > "$WORK/counts-before.txt"
echo "tables: $(wc -l < "$WORK/counts-before.txt"); tickets=$(grep '^tickets ' "$WORK/counts-before.txt" | cut -d' ' -f2) invoices=$(grep '^invoices ' "$WORK/counts-before.txt" | cut -d' ' -f2) attachment sha256=${ATT_SHA_BEFORE:0:16}…"

step "3. Encrypted backup"
echo "drill-passphrase-$RANDOM$RANDOM" > "$WORK/pass"; chmod 600 "$WORK/pass"
T0=$(date +%s.%N)
BACKUP="$(BACKUP_DIR="$WORK/backups" BACKUP_PASSPHRASE_FILE="$WORK/pass" "$OPS/backup-postgres.sh" "$SRC" | tail -1)"
T1=$(date +%s.%N)
ls -l "$BACKUP" "$BACKUP.sha256" | awk '{print $5, $9}'
printf 'backup time: %.1fs\n' "$(echo "$T1 - $T0" | bc)"

step "4. DISASTER: destroy the source database"
psql -XAtq -d postgres -c "DROP DATABASE \"$SRC\" WITH (FORCE)"
psql -XAtq -d postgres -c "SELECT 'source database exists: ' || (count(*) > 0) FROM pg_database WHERE datname = '$SRC'"

step "5. Negative checks (must be refused)"
cp "$BACKUP" "$WORK/corrupt.enc"; cp "$BACKUP.sha256" "$WORK/corrupt.enc.sha256"
sed -i "s/ .*/  corrupt.enc/" "$WORK/corrupt.enc.sha256"
printf '\x00' | dd of="$WORK/corrupt.enc" bs=1 seek=200 count=1 conv=notrunc 2> /dev/null
if BACKUP_PASSPHRASE_FILE="$WORK/pass" "$OPS/restore-postgres.sh" "$WORK/corrupt.enc" "$DST" > "$WORK/neg1.log" 2>&1; then echo "FAIL: corrupt backup was accepted"; exit 1; else echo "ok: corrupted backup rejected ($(grep -o 'CHECKSUM MISMATCH' "$WORK/neg1.log" | head -1))"; fi
echo "wrong-passphrase" > "$WORK/badpass"
if BACKUP_PASSPHRASE_FILE="$WORK/badpass" "$OPS/restore-postgres.sh" "$BACKUP" "$DST" > "$WORK/neg2.log" 2>&1; then echo "FAIL: wrong passphrase accepted"; exit 1; else echo "ok: wrong passphrase rejected"; fi
psql -XAtq -d postgres -c "SELECT count(*) FROM pg_database WHERE datname = '$DST'" | grep -q '^0$' && echo "ok: failed attempts did not leave a half-restored '$DST' behind" || { echo "FAIL: '$DST' exists after failed attempts"; exit 1; }

step "6. Restore into '$DST' and verify"
T0=$(date +%s.%N)
BACKUP_PASSPHRASE_FILE="$WORK/pass" "$OPS/restore-postgres.sh" "$BACKUP" "$DST"
T1=$(date +%s.%N)
printf 'restore + verify time: %.1fs\n' "$(echo "$T1 - $T0" | bc)"
counts "$DST" > "$WORK/counts-after.txt"
if diff -u "$WORK/counts-before.txt" "$WORK/counts-after.txt" > "$WORK/counts.diff"; then echo "ok: row counts identical in all $(wc -l < "$WORK/counts-after.txt") tables"; else cat "$WORK/counts.diff"; echo "FAIL: row counts differ"; exit 1; fi
if BACKUP_PASSPHRASE_FILE="$WORK/pass" "$OPS/restore-postgres.sh" "$BACKUP" "$DST" > "$WORK/neg3.log" 2>&1; then echo "FAIL: restore over a populated database was allowed"; exit 1; else echo "ok: restoring over a populated database is refused without --force"; fi

step "7. The application works on the restored data (runtime role, RLS enforced)"
start_api "$DST"
for n in A B; do
  login=$(api POST auth/login "" "{\"email\":\"owner-${SLUG[$n]}@example.test\",\"password\":\"Passw0rd!Secure\",\"tenantSlug\":\"${SLUG[$n]}\"}")
  TOKEN[$n]=$(jq -r .accessToken <<< "$login")
done
SUBJ=$(api GET "helpdesk/tickets/${TICKET[A]}" "${TOKEN[A]}" | jq -r .summary.subject)
[ "$SUBJ" = "Printer A offline" ] && echo "ok: ticket restored and readable after sign-in ('$SUBJ')" || { echo "FAIL: ticket subject '$SUBJ'"; exit 1; }
curl -fsS "http://127.0.0.1:$PORT/api/v1/helpdesk/tickets/${TICKET[A]}/attachments/$ATT_ID" -H "Authorization: Bearer ${TOKEN[A]}" -o "$WORK/restored.png"
[ "$(sha256sum < "$WORK/restored.png" | cut -d' ' -f1)" = "$ATT_SHA_BEFORE" ] && echo "ok: attachment is byte-identical (sha256 ${ATT_SHA_BEFORE:0:16}…)" || { echo "FAIL: attachment differs"; exit 1; }
SUB_AFTER=$(api GET billing/subscription "${TOKEN[A]}" | jq -S .)
[ "$SUB_BEFORE" = "$SUB_AFTER" ] && echo "ok: subscription identical ($(jq -r '.planCode + " " + .status + " limit " + (.deviceLimit|tostring)' <<< "$SUB_AFTER"))" || { echo "FAIL: subscription differs"; diff <(echo "$SUB_BEFORE") <(echo "$SUB_AFTER"); exit 1; }
INV=$(api GET billing/invoices "${TOKEN[A]}" | jq -r '.items[0].number')
[ "$INV" = "INV-000001" ] && echo "ok: invoice $INV restored" || { echo "FAIL: invoice number '$INV'"; exit 1; }
TRIAGE=$(api GET "helpdesk/tickets?triage=true" "${TOKEN[A]}" | jq -r '.items[0].subject')
[ "$TRIAGE" = "Cannot log in" ] && echo "ok: e-mail ticket still waiting in the triage queue" || { echo "FAIL: triage '$TRIAGE'"; exit 1; }
CODE=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/api/v1/helpdesk/tickets/${TICKET[A]}" -H "Authorization: Bearer ${TOKEN[B]}")
[ "$CODE" = "404" ] && echo "ok: tenant B still cannot see tenant A's ticket (HTTP $CODE)" || { echo "FAIL: cross-tenant read returned HTTP $CODE"; exit 1; }
# Writes work too (sequences/counters continue where they left off).
NEXT=$(api POST helpdesk/tickets "${TOKEN[A]}" '{"subject":"After restore","description":"Created after the restore."}' | jq -r .summary.number)
[ "$NEXT" = "3" ] && echo "ok: new ticket continues the numbering (#$NEXT)" || { echo "FAIL: next ticket number '$NEXT'"; exit 1; }
stop_api

step "RESULT: recovery drill PASSED"
