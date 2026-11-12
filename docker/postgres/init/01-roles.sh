#!/usr/bin/env bash
# Runs once when the PostgreSQL data directory is initialised (official image
# docker-entrypoint-initdb.d hook). Creates the two application roles:
#   - $SOLVIX_DB_MIGRATOR_USER owns the schema and runs EF Core migrations
#   - $SOLVIX_DB_APP_USER is the runtime role: NOSUPERUSER + NOBYPASSRLS, so
#     Row-Level Security policies are enforced for every query the API issues.
set -euo pipefail

: "${SOLVIX_DB_MIGRATOR_USER:=solvix_migrator}"
: "${SOLVIX_DB_MIGRATOR_PASSWORD:=solvix_migrator_dev}"
: "${SOLVIX_DB_APP_USER:=solvix_app}"
: "${SOLVIX_DB_APP_PASSWORD:=solvix_app_dev}"

psql -v ON_ERROR_STOP=1 \
  -v migrator="$SOLVIX_DB_MIGRATOR_USER" -v migrator_pw="$SOLVIX_DB_MIGRATOR_PASSWORD" \
  -v app="$SOLVIX_DB_APP_USER" -v app_pw="$SOLVIX_DB_APP_PASSWORD" \
  -v db="$POSTGRES_DB" \
  --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
CREATE ROLE :"migrator" LOGIN PASSWORD :'migrator_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE ROLE :"app"      LOGIN PASSWORD :'app_pw'      NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

ALTER DATABASE :"db" OWNER TO :"migrator";
ALTER SCHEMA public OWNER TO :"migrator";
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO :"app";

-- Objects created later by the migrator (tables, sequences, functions) are
-- automatically usable by the runtime role, still subject to RLS.
ALTER DEFAULT PRIVILEGES FOR ROLE :"migrator" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"app";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migrator" IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO :"app";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migrator" IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO :"app";
SQL
