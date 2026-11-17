#!/usr/bin/env bash
# Integrity checks for a restored (or live) Solvix database. Exit 0 only if every check passes.
#
#   ./scripts/ops/verify-restore.sh <db> [--compare-with <other-db>]
#
# Environment: libpq variables; optional SOLVIX_DB_APP_USER / SOLVIX_DB_APP_PASSWORD to prove the runtime role is
# still bound by row-level security (skipped when not set); EXPECTED_MIGRATIONS to pin the migration count.
set -uo pipefail

DB="${1:?usage: verify-restore.sh <db> [--compare-with <other-db>]}"
OTHER=""
[ "${2:-}" = "--compare-with" ] && OTHER="${3:?--compare-with needs a database}"

FAIL=0
ok()   { echo "  ok    $*"; }
bad()  { echo "  FAIL  $*"; FAIL=1; }
q()    { psql -XAtq -d "${2:-$DB}" -c "$1"; }

echo "[verify] $DB"

# 1. Schema history.
MIGS=$(q 'SELECT count(*) FROM "__EFMigrationsHistory"') || MIGS=""
LAST=$(q 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 1') || LAST=""
if [ -n "$MIGS" ] && [ "$MIGS" -gt 0 ]; then ok "migration history present ($MIGS applied, latest $LAST)"; else bad "no migration history"; fi
if [ -n "${EXPECTED_MIGRATIONS:-}" ]; then
  [ "$MIGS" = "$EXPECTED_MIGRATIONS" ] && ok "migration count equals expected ($EXPECTED_MIGRATIONS)" || bad "expected $EXPECTED_MIGRATIONS migrations, found $MIGS"
fi

# 2. Every table with a tenant_id column has FORCED row-level security and the tenant_isolation policy.
UNPROTECTED=$(q "
  SELECT string_agg(c.relname, ', ') FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
  JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped
  WHERE c.relkind = 'r'
    AND NOT (c.relrowsecurity AND c.relforcerowsecurity
             AND EXISTS (SELECT 1 FROM pg_policies p WHERE p.schemaname = 'public' AND p.tablename = c.relname AND p.policyname = 'tenant_isolation'))")
TENANT_TABLES=$(q "SELECT count(*) FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public' WHERE a.attname = 'tenant_id' AND NOT a.attisdropped AND c.relkind = 'r'")
if [ -z "$UNPROTECTED" ]; then ok "all $TENANT_TABLES tenant tables have forced RLS + tenant_isolation policy"; else bad "tables without forced RLS: $UNPROTECTED"; fi

# 3. Helper functions used by the policies.
FUNCS=$(q "SELECT count(*) FROM pg_proc WHERE proname IN ('app_current_tenant_id','app_rls_bypass','plans_owner_only')")
[ "$FUNCS" = "3" ] && ok "RLS helper functions and plan-catalog guard present" || bad "missing RLS helper functions ($FUNCS of 3)"

# 4. Plan catalog.
PLANS=$(q "SELECT string_agg(code || ':' || COALESCE(device_limit::text, 'custom'), ' ' ORDER BY sort_order) FROM plans")
[ "$PLANS" = "starter:25 business:75 professional:200 enterprise:custom" ] && ok "plan catalog intact ($PLANS)" || bad "unexpected plan catalog: '$PLANS'"

# 5. Referential integrity: every constraint validated (restores validate them; assert nothing is left NOT VALID).
NOTVALID=$(q "SELECT count(*) FROM pg_constraint WHERE NOT convalidated")
[ "$NOTVALID" = "0" ] && ok "no unvalidated constraints" || bad "$NOTVALID constraints are NOT VALID"

# 6. The runtime role must still be bound by RLS (sees nothing without a tenant).
if [ -n "${SOLVIX_DB_APP_USER:-}" ] && [ -n "${SOLVIX_DB_APP_PASSWORD:-}" ]; then
  HOST="${PGHOST:-localhost}"; PORT="${PGPORT:-5432}"
  SEEN=$(PGPASSWORD="$SOLVIX_DB_APP_PASSWORD" psql -XAtq -h "$HOST" -p "$PORT" -U "$SOLVIX_DB_APP_USER" -d "$DB" -c "SELECT (SELECT count(*) FROM tickets) + (SELECT count(*) FROM subscriptions) + (SELECT count(*) FROM invoices) + (SELECT count(*) FROM devices)" 2>&1) || SEEN="error: $SEEN"
  [ "$SEEN" = "0" ] && ok "runtime role '$SOLVIX_DB_APP_USER' sees 0 tenant rows without a tenant context (RLS enforced)" || bad "runtime role check failed: $SEEN"
else
  echo "  skip  runtime-role RLS check (set SOLVIX_DB_APP_USER / SOLVIX_DB_APP_PASSWORD)"
fi

# 7. Optional: row counts identical to another database (e.g. the source of the backup).
if [ -n "$OTHER" ]; then
  DIFFS=0
  for t in $(q "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public' WHERE c.relkind = 'r' ORDER BY 1"); do
    A=$(q "SELECT count(*) FROM \"$t\"" "$DB"); B=$(q "SELECT count(*) FROM \"$t\"" "$OTHER")
    [ "$A" = "$B" ] || { bad "row count differs in $t: $DB=$A $OTHER=$B"; DIFFS=1; }
  done
  [ "$DIFFS" = "0" ] && ok "row counts identical to '$OTHER' in every table"
fi

if [ "$FAIL" = "0" ]; then echo "[verify] PASSED"; else echo "[verify] FAILED"; fi
exit "$FAIL"
