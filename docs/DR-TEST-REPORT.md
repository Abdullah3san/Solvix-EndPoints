# Disaster-recovery test report

**Procedure:** `scripts/ops/dr-drill.sh` (also run by CI as the `recovery-drill` job).
**Executed:** 2026-11-16, in the development container (PostgreSQL 16 on localhost, Redis 7, .NET 10), against the real
migrator and the real API. **Result: PASSED.** The first run of the drill *failed* at its last application check and found
a real defect (see "What the drill found"); it was fixed, a regression test added, and the drill re-run to the result below.

## What was exercised

| # | Step | Proves |
|---|------|--------|
| 1 | Create a database, apply all 11 migrations with `Solvix.Migrator` | A clean environment can be built from source alone |
| 2 | Populate through the HTTP API: two tenants, tickets + comments, an attachment (PNG), an inbound e-mail ticket, a billing profile, a plan purchase (invoice) | Backup content is real application data, not hand-written SQL |
| 3 | `backup-postgres.sh` with encryption (AES-256, PBKDF2) | Backup works, is verified readable (`pg_restore --list`), checksummed and encrypted |
| 4 | `DROP DATABASE` the source | The disaster |
| 5 | Restore a corrupted copy; restore with the wrong passphrase | Tampering and wrong keys are refused and leave no half-restored database |
| 6 | `restore-postgres.sh` into a new database + `verify-restore.sh` | Faithful restore: migration history, forced RLS on every tenant table, policy + helper functions, plan catalog, constraints, **runtime role still bound by RLS**, row counts identical in all 38 tables; restoring over a populated database is refused |
| 7 | Start the API on the restored database with the restricted runtime role | Sign-in works, ticket and **byte-identical attachment** readable, subscription and invoice identical, e-mail ticket still in triage, **tenant B still gets 404 for tenant A's ticket**, new tickets continue the numbering (#3) |

## Output (verbatim)

```

== 0. Build

== 1. Create database 'solvix_drill_src' and apply migrations with the real migrator
migrations applied: 11

== 2. Populate through the real API (two tenants)
tables: 38; tickets=3 invoices=2 attachment sha256=6b7fa434f92a8b80…

== 3. Encrypted backup
161280 /tmp/tmp.aQsxyrJdf7/backups/solvix-solvix_drill_src-20261116T191119Z.dump.enc
116 /tmp/tmp.aQsxyrJdf7/backups/solvix-solvix_drill_src-20261116T191119Z.dump.enc.sha256
backup time: 0.6s

== 4. DISASTER: destroy the source database
source database exists: false

== 5. Negative checks (must be refused)
ok: corrupted backup rejected (CHECKSUM MISMATCH)
ok: wrong passphrase rejected
ok: failed attempts did not leave a half-restored 'solvix_drill_restored' behind

== 6. Restore into 'solvix_drill_restored' and verify
[restore] checksum ok
[restore] restoring into 'solvix_drill_restored' (owner solvix_migrator)
[restore] data restored in 0s; verifying
[verify] solvix_drill_restored
  ok    migration history present (11 applied, latest 20261116190100_ReportingSecurity)
  ok    all 33 tenant tables have forced RLS + tenant_isolation policy
  ok    RLS helper functions and plan-catalog guard present
  ok    plan catalog intact (starter:25 business:75 professional:200 enterprise:custom)
  ok    no unvalidated constraints
  ok    runtime role 'solvix_app' sees 0 tenant rows without a tenant context (RLS enforced)
[verify] PASSED
restore + verify time: 1.2s
ok: row counts identical in all 38 tables
ok: restoring over a populated database is refused without --force

== 7. The application works on the restored data (runtime role, RLS enforced)
ok: ticket restored and readable after sign-in ('Printer A offline')
ok: attachment is byte-identical (sha256 6b7fa434f92a8b80…)
ok: subscription identical (starter Trialing limit 25)
ok: invoice INV-000001 restored
ok: e-mail ticket still waiting in the triage queue
ok: tenant B still cannot see tenant A's ticket (HTTP 404)
ok: new ticket continues the numbering (#3)

== RESULT: recovery drill PASSED
```

## Measured timings and what they mean

| Metric | Measured | Notes |
|--------|----------|-------|
| Backup (dump + verify + encrypt + checksum) | ~0.6 s | **Tiny dataset (≈160 KB archive).** Not representative of production. |
| Restore + full verification | ~1.2 s | Same caveat. |

These numbers prove the *mechanism*, not your RTO. Re-run the drill against a copy of production data (restore the latest
real backup with `restore-postgres.sh` into a scratch database) to measure RTO for your volume. As a planning rule,
logical restore time grows roughly linearly with database size; attachments are stored in PostgreSQL, so they count.

## What the drill found

* **Defect (fixed):** the ticket list sorted by priority *alphabetically* (priority is stored as text), so "Medium" sorted
  above "High" and "Critical". The drill's last check (the e-mail ticket, priority High, should head the triage queue)
  failed on the first run. Fixed with an explicit severity ranking; regression test
  `The_queue_is_ordered_by_severity_not_alphabetically`.
* The drill also validated that an application built from the migrations works unmodified on a database produced by
  `pg_restore --no-owner --role=<migrator>`: object ownership, default privileges and RLS survive the round trip.

## Limits of this test (be aware)

* **RPO is the backup interval** (default daily in `docker-compose.prod.yml`). The drill does not exercise
  point-in-time recovery. If a 24-hour data-loss window is too long, enable continuous WAL archiving
  (`archive_mode` + pgBackRest/WAL-G) and rehearse PITR; see `docs/OPERATIONS.md`.
* Redis (cache only) is not backed up and not needed: it holds no system of record.
* The device CA private key and TLS certificates are **not in the database**. Back them up separately
  (`docs/OPERATIONS.md#secrets-and-keys`). Losing the device CA means every agent must re-enroll.
* Run on a single host. Cross-host/object-storage backup transfer, and the encryption key custody, are deployment choices.
* The Docker-based production overlay and monitoring stack could not be executed in this environment (no Docker daemon);
  only their syntax was validated. Run a staging deployment before relying on them (`docs/PRODUCTION-READINESS.md`).
