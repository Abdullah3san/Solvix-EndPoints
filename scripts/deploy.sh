#!/usr/bin/env bash
# Updates a running Solvix server to the latest code of its branch and checks the result.
#
#   cd /opt/solvix && ./scripts/deploy.sh
#
# Steps: fetch + fast-forward the checked-out branch (never discards local changes), rebuild
# and restart the stack (database migrations run automatically), wait until every service
# is healthy, then check that the site answers and print a short report. Test mode (bare IP,
# docker-compose.test-ip.yml) is detected from SOLVIX_PUBLIC_HOST in .env.production.
# Exit code 0 means the new version is up and healthy.
set -euo pipefail
cd "$(dirname "$0")/.."

ENV_FILE="${ENV_FILE:-.env.production}"
[[ -f $ENV_FILE ]] || { echo "Missing $ENV_FILE (run scripts/setup-test-ip.sh or create it from .env.production.example)." >&2; exit 1; }

files=(-f docker-compose.prod.yml)
public_host="$(grep -E '^SOLVIX_PUBLIC_HOST=' "$ENV_FILE" | tail -n1 | cut -d= -f2- || true)"
http_port="$(grep -E '^SOLVIX_HTTP_PORT=' "$ENV_FILE" | tail -n1 | cut -d= -f2- || true)"
if [[ -n $public_host ]]; then
  files+=(-f docker-compose.test-ip.yml)
  mode="test mode (http://$public_host:${http_port:-8080})"
else
  mode="production (https://$(grep -E '^SOLVIX_DOMAIN=' "$ENV_FILE" | tail -n1 | cut -d= -f2-))"
fi
compose() { docker compose "${files[@]}" --env-file "$ENV_FILE" "$@"; }

branch="$(git rev-parse --abbrev-ref HEAD)"
before="$(git rev-parse --short HEAD)"
echo "==> Updating $branch ($mode)"
git fetch --quiet origin "$branch"
if ! git merge --ff-only --quiet "origin/$branch"; then
  echo "The server checkout has local commits or changes that block a fast-forward:" >&2
  git status --short >&2
  exit 1
fi
after="$(git rev-parse --short HEAD)"
if [[ $before == "$after" ]]; then
  echo "    Already at $after: rebuilding anyway."
else
  echo "    $before -> $after"
  git log --oneline "$before..$after" | sed 's/^/    /'
fi

echo "==> Building and starting (this takes a few minutes)"
compose up -d --build --remove-orphans

echo "==> Waiting for services to become healthy"
deadline=$((SECONDS + 300))
while :; do
  pending="$(compose ps --format '{{.Service}} {{.Health}}' | awk '$2 != "" && $2 != "healthy" {print $1 "(" $2 ")"}' | xargs || true)"
  [[ -z $pending ]] && break
  if (( SECONDS > deadline )); then
    echo "Still not healthy after 5 minutes: $pending" >&2
    for service in api web; do
      echo "--- last $service log lines ---" >&2
      compose logs --tail=40 "$service" >&2 || true
    done
    exit 1
  fi
  sleep 5
done

failed_migration="$(compose ps -a --format '{{.Service}} {{.State}} {{.ExitCode}}' | awk '$1 == "migrator" && $3 != 0' || true)"
if [[ -n $failed_migration ]]; then
  echo "The database migration failed:" >&2
  compose logs --tail=60 migrator >&2
  exit 1
fi

url="http://127.0.0.1:${http_port:-8080}/login"
[[ -z $public_host ]] && url="http://127.0.0.1/login"
code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$url" || echo 000)"

echo
echo "==> Deployed $(git log -1 --format='%h %s')"
compose ps --format 'table {{.Service}}\t{{.Status}}'
echo "    Sign-in page: HTTP $code"
if [[ $code != 200 && $code != 307 && $code != 308 ]]; then
  echo "The site did not answer as expected." >&2
  exit 1
fi
echo "All services are healthy."
