#!/usr/bin/env bash
# Restores a backup made by backup-postgres.sh into a database, then runs the integrity checks.
#
#   PGHOST=... PGUSER=<superuser or owner> PGPASSWORD=... \
#   SOLVIX_DB_MIGRATOR_USER=solvix_migrator \
#   ./scripts/ops/restore-postgres.sh <backup-file> <target-db> [--force]
#
# - Verifies the .sha256 next to the file first.
# - Decrypts *.enc with BACKUP_PASSPHRASE_FILE.
# - Refuses to restore over a database that already contains tables unless --force (which drops and recreates it).
# - Objects are created owned by the migrator role ($SOLVIX_DB_MIGRATOR_USER); the application roles must already
#   exist (they come from docker/postgres/init/01-roles.sh or scripts/create-local-db.sh).
# - Never touches a database you did not name: restore into a NEW name first and switch the application over once
#   verify-restore.sh passes.
set -euo pipefail
umask 077

FILE="${1:?usage: restore-postgres.sh <backup-file> <target-db> [--force]}"
TARGET="${2:?usage: restore-postgres.sh <backup-file> <target-db> [--force]}"
FORCE="${3:-}"
OWNER="${SOLVIX_DB_MIGRATOR_USER:-solvix_migrator}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

[ -f "$FILE" ] || { echo "[restore] no such file: $FILE" >&2; exit 1; }
if [ -f "$FILE.sha256" ]; then
  ( cd "$(dirname "$FILE")" && sha256sum --check --status "$(basename "$FILE").sha256" ) \
    || { echo "[restore] CHECKSUM MISMATCH for $FILE: the backup is corrupt or was tampered with" >&2; exit 3; }
  echo "[restore] checksum ok"
else
  echo "[restore] WARNING: no .sha256 next to the backup; integrity not verified" >&2
fi

WORK="$FILE"
CLEANUP=""
if [[ "$FILE" == *.enc ]]; then
  : "${BACKUP_PASSPHRASE_FILE:?BACKUP_PASSPHRASE_FILE is required to restore an encrypted backup}"
  WORK="$(mktemp)"; CLEANUP="$WORK"
  openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -in "$FILE" -out "$WORK" -pass "file:${BACKUP_PASSPHRASE_FILE}"
fi
trap '[ -n "$CLEANUP" ] && rm -f "$CLEANUP"' EXIT
pg_restore --list "$WORK" > /dev/null

EXISTS=$(psql -XAtq -d postgres -c "SELECT 1 FROM pg_database WHERE datname = '$TARGET'")
if [ "$EXISTS" = "1" ]; then
  TABLES=$(psql -XAtq -d "$TARGET" -c "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'")
  if [ "$TABLES" != "0" ] && [ "$FORCE" != "--force" ]; then
    echo "[restore] '$TARGET' already contains $TABLES tables. Choose a new name, or pass --force to replace it." >&2
    exit 4
  fi
  if [ "$FORCE" = "--force" ]; then
    echo "[restore] --force: dropping '$TARGET'"
    psql -XAtq -d postgres -c "DROP DATABASE \"$TARGET\" WITH (FORCE)"
    EXISTS=0
  fi
fi
if [ "$EXISTS" != "1" ]; then
  psql -XAtq -d postgres -c "CREATE DATABASE \"$TARGET\" OWNER \"$OWNER\""
fi

echo "[restore] restoring into '$TARGET' (owner $OWNER)"
START=$(date +%s)
# --no-owner + --role: every object becomes owned by the migrator role, whatever the source owner was.
pg_restore --exit-on-error --no-owner --role="$OWNER" --dbname="$TARGET" "$WORK"
echo "[restore] data restored in $(( $(date +%s) - START ))s; verifying"
"$HERE/verify-restore.sh" "$TARGET"
