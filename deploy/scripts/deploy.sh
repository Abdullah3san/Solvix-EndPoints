#!/usr/bin/env bash
# Build, migrate and (re)start Solvix on this VPS. Safe to re-run; it is also the update procedure.
#   deploy/scripts/deploy.sh [--pull]     --pull: git pull --ff-only first
set -euo pipefail
cd "$(dirname "$0")/.."
fail() { echo "ERROR: $*" >&2; exit 1; }
[[ -f .env ]] || fail "deploy/.env not found. Copy .env.production.example to .env and fill it in."
command -v docker >/dev/null || fail "Docker is not installed."
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is required."

if [[ "${1:-}" == "--pull" ]]; then
  git -C .. pull --ff-only
fi

# Tag images with the commit so a previous version can be started again (see README: rollback).
SOLVIX_VERSION="$(git -C .. rev-parse --short HEAD 2>/dev/null || echo latest)"
export SOLVIX_VERSION
echo "== deploying version $SOLVIX_VERSION"

# Placeholder values would silently produce an insecure install.
for var in SOLVIX_DOMAIN ACME_EMAIL POSTGRES_PASSWORD SOLVIX_DB_MIGRATOR_PASSWORD SOLVIX_DB_APP_PASSWORD REDIS_PASSWORD SOLVIX_JWT_SIGNING_KEY SOLVIX_DEVICE_CA_PASSWORD; do
  value="$(grep -E "^${var}=" .env | head -1 | cut -d= -f2- || true)"
  [[ -n "$value" ]] || fail "$var is empty in deploy/.env"
done
grep -qE '^SOLVIX_DOMAIN=solvix\.example\.com' .env && fail "SOLVIX_DOMAIN is still the example value."
jwt="$(grep -E '^SOLVIX_JWT_SIGNING_KEY=' .env | cut -d= -f2-)"
[[ ${#jwt} -ge 32 ]] || fail "SOLVIX_JWT_SIGNING_KEY must be at least 32 characters."

# Back up the database and the PKI before touching anything on an existing install.
if docker compose ps --status running --services 2>/dev/null | grep -qx postgres; then
  echo "== backing up before the update"
  ./scripts/backup.sh
fi

echo "== building images"
docker compose build
echo "== starting (the migrator runs first and applies pending migrations)"
docker compose up -d --remove-orphans

echo "== waiting for the stack to become healthy"
for _ in $(seq 1 90); do
  status="$(docker compose ps --format '{{.Service}} {{.Status}}')"
  if echo "$status" | grep -q "api.*(healthy)" && echo "$status" | grep -q "web.*(healthy)" && echo "$status" | grep -q "^caddy.*Up"; then
    docker compose ps --format 'table {{.Service}}\t{{.Status}}'
    domain="$(grep -E '^SOLVIX_DOMAIN=' .env | cut -d= -f2-)"
    echo
    echo "Solvix is up:  https://$domain"
    echo "Agent endpoint: https://agents.$domain  (verify with scripts/verify-proxy.sh)"
    exit 0
  fi
  sleep 2
done
docker compose ps
docker compose logs --tail 40 api web caddy
fail "the stack did not become healthy in time"
