#!/usr/bin/env bash
# PostgreSQL backup for the production stack. Run from the repository root on
# the VPS, for example nightly from cron:
#   15 3 * * * cd /opt/solvix && ./scripts/backup-db.sh >> /var/log/solvix-backup.log 2>&1
#
# Writes a compressed, pg_restore-able dump (schema, data, RLS policies and
# triggers) to ./backups and checks that it can be read back. Dumps older than
# KEEP_DAYS days in ./backups are pruned. Copy the dumps OFF the server (object
# storage or another host): a backup on the same disk is not a backup.
set -euo pipefail
cd "$(dirname "$0")/.."

KEEP_DAYS="${KEEP_DAYS:-14}"
COMPOSE=(docker compose -f docker-compose.prod.yml --env-file .env.production)
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
FILE="solvix-$STAMP.dump"

mkdir -p backups
"${COMPOSE[@]}" exec -T postgres pg_dump -U postgres -d solvix --format=custom --file="/backups/$FILE"
"${COMPOSE[@]}" exec -T postgres pg_restore --list "/backups/$FILE" > /dev/null

# Retention: only files named solvix-*.dump directly inside ./backups.
find ./backups -maxdepth 1 -type f -name 'solvix-*.dump' -mtime +"$KEEP_DAYS" -print -exec rm -- {} \;

echo "$(date -u +%FT%TZ) backup ok: backups/$FILE ($(du -h "backups/$FILE" | cut -f1))"
