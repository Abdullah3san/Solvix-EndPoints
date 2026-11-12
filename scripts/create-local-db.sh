#!/usr/bin/env bash
# Creates the local development database and roles on a PostgreSQL server you
# run yourself (i.e. without Docker). Requires a superuser connection.
#   PGHOST=localhost PGUSER=postgres PGPASSWORD=postgres ./scripts/create-local-db.sh [dbname]
set -euo pipefail
DB="${1:-solvix}"
MIGRATOR_USER="${SOLVIX_DB_MIGRATOR_USER:-solvix_migrator}"
MIGRATOR_PASSWORD="${SOLVIX_DB_MIGRATOR_PASSWORD:-solvix_migrator_dev}"
APP_USER="${SOLVIX_DB_APP_USER:-solvix_app}"
APP_PASSWORD="${SOLVIX_DB_APP_PASSWORD:-solvix_app_dev}"

psql -v ON_ERROR_STOP=1 -d postgres \
  -v db="$DB" -v migrator="$MIGRATOR_USER" -v migrator_pw="$MIGRATOR_PASSWORD" -v app="$APP_USER" -v app_pw="$APP_PASSWORD" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS', :'migrator', :'migrator_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'migrator') \gexec
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS', :'app', :'app_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'app') \gexec
SELECT format('CREATE DATABASE %I OWNER %I', :'db', :'migrator')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db') \gexec
SQL

psql -v ON_ERROR_STOP=1 -d "$DB" -v migrator="$MIGRATOR_USER" -v app="$APP_USER" <<'SQL'
ALTER SCHEMA public OWNER TO :"migrator";
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO :"app";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migrator" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"app";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migrator" IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO :"app";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migrator" IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO :"app";
SQL
echo "Database '$DB' ready (owner: $MIGRATOR_USER, runtime role: $APP_USER)."
