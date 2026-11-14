#!/usr/bin/env bash
# Back up the database and the PKI volume (device CA private key!) to deploy/backups. Keeps the 14 newest sets.
# COPY THE FILES OFF THIS SERVER (another host or object storage): a backup on the same disk is not a backup.
set -euo pipefail
cd "$(dirname "$0")/.."
[[ -f .env ]] || { echo "deploy/.env not found" >&2; exit 1; }
set -a; source .env; set +a
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p backups && chmod 700 backups
db="${POSTGRES_DB:-solvix}"

docker compose exec -T postgres pg_dump -U "${POSTGRES_USER:-postgres}" -Fc "$db" > "backups/db-$stamp.dump"
volume="$(docker volume ls -q --filter name=solvix-pki | head -1)"
[[ -n "$volume" ]] || { echo "PKI volume not found" >&2; exit 1; }
docker run --rm -v "$volume":/pki:ro -v "$PWD/backups":/backup alpine tar czf "/backup/pki-$stamp.tgz" -C /pki .
chmod 600 backups/*-"$stamp".*

# retention
for kind in db pki; do ls -1t backups/$kind-* 2>/dev/null | tail -n +15 | xargs -r rm -f; done
echo "Backup written: backups/db-$stamp.dump, backups/pki-$stamp.tgz"
