#!/usr/bin/env bash
# Logical backup of the Solvix database (pg_dump custom format), integrity-checked, optionally encrypted.
#
#   PGHOST=... PGUSER=... PGPASSWORD=... BACKUP_DIR=/var/backups/solvix ./scripts/ops/backup-postgres.sh [dbname]
#
# Environment (libpq variables are honoured as usual):
#   BACKUP_DIR              target directory (default ./backups); created with mode 700
#   BACKUP_RETENTION_DAYS   delete backups older than this many days (default 14; 0 disables pruning)
#   BACKUP_PASSPHRASE_FILE  if set, the dump is encrypted with AES-256-CBC + PBKDF2 (openssl) and the plain dump removed
#   BACKUP_MIN_BYTES        sanity floor for a believable dump (default 20000)
#
# Output: solvix-<db>-<UTC timestamp>.dump[.enc] and a .sha256 file next to it. Exit status is non-zero on ANY failure,
# so it can be used directly from cron / a scheduler and alerted on.
set -euo pipefail
umask 077

DB="${1:-${PGDATABASE:-solvix}}"
DIR="${BACKUP_DIR:-./backups}"
RETENTION="${BACKUP_RETENTION_DAYS:-14}"
MIN_BYTES="${BACKUP_MIN_BYTES:-20000}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
NAME="solvix-${DB}-${STAMP}.dump"

mkdir -p "$DIR"
chmod 700 "$DIR"
TMP="$(mktemp "$DIR/.${NAME}.XXXXXX")"
trap 'rm -f "$TMP" "$TMP.enc"' EXIT

echo "[backup] dumping '$DB' -> $DIR/$NAME"
START=$(date +%s)
# -Fc: compressed, restorable in parallel/selectively. Owners and privileges are kept so a restore is faithful.
pg_dump --format=custom --compress=6 --no-sync --dbname="$DB" --file="$TMP"

# 1. The archive must be readable by pg_restore (catches truncated / corrupt output).
pg_restore --list "$TMP" > /dev/null
# 2. And it must not be suspiciously small.
SIZE=$(stat -c %s "$TMP")
if [ "$SIZE" -lt "$MIN_BYTES" ]; then
  echo "[backup] FAILED: dump is only ${SIZE} bytes (< ${MIN_BYTES}); refusing to keep it" >&2
  exit 2
fi

FINAL="$DIR/$NAME"
if [ -n "${BACKUP_PASSPHRASE_FILE:-}" ]; then
  openssl enc -aes-256-cbc -pbkdf2 -iter 600000 -salt -in "$TMP" -out "$TMP.enc" -pass "file:${BACKUP_PASSPHRASE_FILE}"
  FINAL="$FINAL.enc"
  mv "$TMP.enc" "$FINAL"
  rm -f "$TMP"
else
  mv "$TMP" "$FINAL"
fi
( cd "$DIR" && sha256sum "$(basename "$FINAL")" > "$(basename "$FINAL").sha256" )

if [ "$RETENTION" -gt 0 ]; then
  find "$DIR" -maxdepth 1 -name 'solvix-*.dump*' -type f -mtime "+$RETENTION" -print -delete | sed 's/^/[backup] pruned /'
fi

echo "[backup] OK $(basename "$FINAL") $(stat -c %s "$FINAL") bytes in $(( $(date +%s) - START ))s"
echo "$FINAL"
