# Phase 10 – Endpoint Security Management

Centralised visibility into the security posture of Windows devices, with explainable findings, a compliance score,
policy, approval-gated remediation and an audit trail.

## What this is — and is not

Solvix **reads the security configuration of Windows devices through supported Windows interfaces, evaluates it
against your policy, and reports the result**. It is a posture-visibility and policy-management tool.

It is **not antivirus and not EDR**. There is no malware-detection engine, no behavioural analysis, no process or file
inspection and no real-time blocking. Anything the product says about malware is *what Microsoft Defender itself reported*
(`Get-MpThreat`, Defender event 1116). A compliance score describes **configuration**, not whether a device is free of
compromise. This statement is shown in the UI (every overview page and *Rules and scoring*), returned by
`GET /api/v1/security/rules` (`productScope`), and covered by tests. Do not market the product as antivirus/EDR unless
those capabilities are built and independently validated.

## Assumption: no earlier device phase

Only Phase 01 exists in this repository (no device inventory, agent or notification service). Phase 10 therefore owns a
minimal endpoint registry (`security_endpoints`) and the agent channel. If a device module arrives later, `ManagedEndpoint`
can be linked to it; nothing here depends on it.

## Architecture

```
Windows device                         Solvix API (modular monolith)                  Admin UI (Next.js)
┌──────────────────────┐  HTTPS   ┌──────────────────────────────────────────┐
│ Solvix-SecurityAgent │ ───────► │ SecurityIngestController  (device key)   │
│  .ps1 (read-only by  │  posture │   DeviceGateway: auth → tenant scope     │
│  default)            │ ◄─────── │   IngestService → PostureProcessor       │
│                      │ commands │      PostureEvaluator (pure) ► Scorer    │
└──────────────────────┘          │      findings · alerts · proposals       │
                                  │ Management controllers (JWT, roles)      │ ◄──── /security/* pages
                                  │   policies · approvals · reports · audit │
                                  └───────────────┬──────────────────────────┘
                                                  │ EF Core + PostgreSQL RLS (all security tables)
```

* **Module:** `src/backend/Modules/Solvix.Modules.Security` (Domain, Evaluation, Application, Api, Persistence).
* **Pure engine:** `PostureEvaluator`, `ComplianceScorer`, `EventRules`, `RuleCatalog`, `RemediationCatalog` have no I/O, clock
  or randomness; the same report and policy always give the same result.
* **Agent:** `agent/windows/Solvix-SecurityAgent.ps1` (see its README).

## The 12 features and how each is implemented

| # | Feature | Windows interface | Where you see it | Notes / limits |
|---|---------|-------------------|------------------|----------------|
| 1 | Windows Defender status | `Get-MpComputerStatus`, `Get-MpThreat`, Security Center (other AV) | Device page → Defender; rules DEF-001…006 | Passive mode with a registered third-party AV makes Defender-specific checks *not applicable* instead of failing. |
| 2 | Windows Firewall status | `Get-NetFirewallProfile` | Device page → Firewall; FW-001…004 | Per profile; default-inbound-allow flagged. Rules inside the firewall are not audited. |
| 3 | BitLocker status | `Get-BitLockerVolume`, `Get-Tpm` | Device page → BitLocker; BL-001…003 | Suspended protection is a separate finding. Editions without BitLocker fail the requirement (they cannot meet it). |
| 4 | Local administrator inventory | `Get-LocalGroupMember -SID S-1-5-32-544` (language independent), `Get-LocalUser` | Device page → Local administrators; ADM-001…004 | Approved-admin check only runs when the policy has a list (no mass false positives). |
| 5 | USB policy compliance | Removable Storage Access policy values, USBSTOR start type | USB-001 | Compares the *effective* restriction with the policy (Allow < BlockWrite < BlockAll). It does not inventory USB devices. |
| 6 | Vulnerable software identification | Uninstall registry keys | *Vulnerable software* page; SW-001 | **Advisory-driven.** Solvix ships and syncs **no** vulnerability database. Administrators maintain advisories (product substring + version range). Matching is plain text, not CPE/CVE lookup. No advisory ⇒ not applicable, never "clean". |
| 7 | Security configuration compliance | `Get-SmbServerConfiguration`, UAC/RDP/Windows Update registry values, `Confirm-SecureBootUEFI` | CFG-001…005 | |
| 8 | Security event monitoring | `Get-WinEvent`, **8 allow-listed event ids** | Alerts page → recent events | 1102, 104, 4625, 4720, 4732, 1116, 1117, 5001. Short fixed summaries only; the server drops anything else. Not a SIEM. |
| 9 | Security alerts | – | Alerts page | High/Critical findings and event rules. Deduplicated; New → Acknowledged → Resolved/Dismissed (dismiss needs a reason). Alerts auto-close when the finding resolves. **No email/push notification yet.** |
| 10 | Security reporting | – | Overview, CSV exports | Fleet summary, top issues, 30-day trend, device and findings CSV (formula-injection safe, exports audited). |
| 11 | Policy enforcement | see below | Policies, Approvals | Detect-only by default. |
| 12 | Device compliance scoring | – | Device page → *Why this score* | Fully explained; see below. |

## Detecting is not remediating

Three independent gates stand between a finding and a change on a device:

1. **Policy gate.** The active, owner-approved policy has an enforcement mode:
   * `DetectOnly` (default, and what applies before any policy exists) – **hard stop**: no remediation can be requested, proposed, approved or delivered.
   * `ApprovalRequired` – listed actions may be requested; each needs a second person.
   * `AutoApproveLowRisk` – as above, but *Low-risk* actions the policy lists, **proposed by the policy itself**, are approved automatically (domain-enforced: only Low risk can ever be auto-approved).
   The policy also lists *which* catalogue actions are allowed; default is none.
2. **Approval gate.** The approver cannot be the requester (enforced in the domain model and covered by tests); High-risk actions need an **Owner**. Requests expire (7 days to approve, 24 h to be collected, 30 min to report back). Approval is re-checked against the policy **at delivery** – if the policy changed to detect-only meanwhile, the command is cancelled, not sent.
3. **Agent gate.** The agent runs remediation only if the device owner set `"allowRemediation": true` locally.

**The remediation catalogue is the complete allow-list** (`RemediationCatalog`): update Defender signatures, start a quick scan,
enable Defender real-time protection, enable a firewall profile, apply the removable-storage restriction, disable SMBv1.
The server sends a catalogue key plus at most one value from a closed set (e.g. `Domain|Private|Public`) – **never a script**.
Deliberately **not** automatable: BitLocker enablement (key-escrow/data-loss risk), removing administrators (lock-out), uninstalling or
updating software. These are guided manual fixes. A successful remediation does *not* close the finding; only the next report can.

## Approval workflow

| Item | Created by | Decided by | Rules |
|------|-----------|------------|-------|
| Policy | Admin/Owner (draft, only the author can edit) → *submit* | **Owner**, who is **neither the author nor the submitter** | Submitted policies are immutable; changing means a new version. Approval retires the previous policy (DB guarantees ≤1 active) and re-evaluates all devices. |
| Remediation | Admin/Owner, or the policy (system) | Admin/Owner ≠ requester; High risk ⇒ Owner | See gates above. |
| Risk acceptance (finding) | Admin/Owner | Medium/Low: Admin/Owner. **High/Critical: Owner.** | Time-boxed (≤ 90 days), reason required, auto-reopens, visible but not scored while accepted. |

*Consequence for small tenants:* a policy needs two people (e.g. an Admin authors, the Owner approves). A solo owner cannot self-approve; that is intentional.

## Least privilege

| Capability | Member | Admin | Owner | Device key |
|------------|:-----:|:-----:|:-----:|:----------:|
| Read posture, findings, alerts, events, reports, audit | – | ✔ | ✔ | – |
| Enrol / rotate / revoke / delete devices; triage alerts; author policy; manage advisories; request remediation | – | ✔ | ✔ | – |
| Approve remediation (not own request) | – | ✔ (not High) | ✔ | – |
| Approve policy (not own) · accept High/Critical risk | – | – | ✔ | – |
| `POST ingest/posture`, `GET ingest/commands`, `POST ingest/commands/{id}/result` for **its own device only** | – | – | – | ✔ |

* Policies `SecurityRead`, `SecurityManage`, `SecurityApprove` (host `Program.cs`); every management controller inherits `SecurityControllerBase` (`[Authorize(SecurityRead)]`); a test fails if a controller bypasses it or if anything other than the ingest controller is anonymous.
* Agent credential: per-device key `sxk1.<tenant>.<device>.<256-bit secret>`; only a SHA-256 hash is stored, compared in constant time, shown once, rotatable and revocable. Failures are indistinguishable (same 401 body for malformed, unknown device, wrong secret, other tenant, revoked).
* The device runs in a **tenant-bound scope** (`ITenantScopeFactory`), so EF filters, write enforcement and PostgreSQL RLS apply to it like to a user.

## Audit trail

`security_audit` records every state change and decision: enrol/rotate/revoke/delete, policy create/edit/submit/approve/reject/withdraw/delete (with before/after settings), approval and every remediation step (requested, approved, auto-approved, dispatched, completed, expired), advisory changes, risk acceptance, alert triage and report exports.

* Written in the **same transaction** as the change (no action without a record, no record without an action).
* Actor type **User / System (policy) / Device**, with an e-mail/device-name snapshot, source IP and correlation id.
* **Append-only in the database**: RLS has SELECT and INSERT policies only; the runtime role has UPDATE/DELETE/TRUNCATE revoked; a trigger rejects UPDATE/DELETE/TRUNCATE even for a superuser. Tests exercise all three roles.
* Not recorded: routine posture reports and finding open/resolve (that is data, not decisions). Not tamper-*evident* (no hash chain); a database superuser can still disable the trigger – ship audit rows to external WORM storage if your regime requires it.

## Findings, severity and scoring

Every finding carries: the evidence, **why it matters**, **why it has that severity**, **how to fix it**, and the Windows interface the fact came from (`RuleCatalog`; tests require all of them for every rule). The same text is served by `GET /security/rules`.

| Severity | Meaning | Points |
|----------|---------|:------:|
| Critical | Evidence of active compromise, or exposure to remote exploitation right now (e.g. Defender reports an unremediated threat; audit log cleared) | −40 |
| High | A primary protection is disabled or a well-known weakness is exposed (firewall off, system drive unencrypted, SMBv1, UAC off, unapproved admin) | −20 |
| Medium | Protection degraded / policy not met, primary control still works | −10 |
| Low | Hygiene; needs several other failures to matter | −4 |
| Info | Informational | 0 |

**Score** = 100 − Σ penalties, floored at 0; repeats of one rule count at most twice its weight. **Unknown** (could not be collected) and **not-applicable** checks never cost points and **never count as a pass**; unknown checks reduce *coverage* instead.
**Band:** NonCompliant if score < 70 or any Critical; AtRisk if score < 90, any High, or < 90 % of applicable checks assessable; otherwise Compliant; **Unknown** if no report within the staleness limit (default 7 days). A finding is resolved only by *positive* evidence – missing data leaves it open.

## Rules

| Key | Rule | Severity | Remediable (policy + approval) |
|-----|------|----------|:---:|
| DEF-001 | Defender real-time protection off | High | ✔ |
| DEF-002 | No active antivirus | High | – |
| DEF-003 | Signatures out of date | Medium | ✔ |
| DEF-004 | Tamper Protection off | Medium | – (cannot be set locally by design) |
| DEF-005 | No recent scan | Low | ✔ |
| DEF-006 | Defender reports active threats | Critical | – |
| FW-001/2/3 | Firewall off (Domain / Private / Public) | Medium / High / High | ✔ |
| FW-004 | Default inbound action Allow | Medium | – |
| BL-001/2/3 | System drive / fixed drive not encrypted / protection suspended | High / Medium / Medium | – |
| ADM-001…004 | Too many admins · unapproved admin · built-in Administrator enabled · Guest enabled | Medium / High / Medium / Medium | – |
| USB-001 | USB storage weaker than policy | Medium | ✔ |
| SW-001 | Software matches an advisory | per advisory | – |
| CFG-001…005 | SMBv1 · UAC off · RDP without NLA · Secure Boot off · Windows Update disabled | High / High / High / Medium / Medium | SMBv1 only |
| EVT-001…006 | Log cleared · malware detected · Defender disabled · failed-logon burst · admin group change · account created | Critical / High / High / Medium(High at 5×) / High / Medium | – |

## API

Management (JWT, `/api/v1/security/…`): `summary`, `rules`, `endpoints` (+`/{id}`, `/history`, `/software`, enrol, `rotate-key`, `revoke`, delete), `findings` (+accept/revoke), `alerts` (+acknowledge/resolve/dismiss), `events`, `policies` (+`effective`, revise, submit, withdraw, approve, reject), `advisories`, `remediation` (+approve/reject/cancel), `audit`, `reports/devices.csv`, `reports/findings.csv`.
Agent (device key header `X-Solvix-Device-Key`): `ingest/posture`, `ingest/commands`, `ingest/commands/{id}/result`. OpenAPI/Scalar documents all of it.

Configuration: `Solvix:Security:MinReportIntervalSeconds` (default 30; a device reporting faster gets 429 + `Retry-After`). Ingest is also IP-rate-limited (600/min).

## Threat model (abridged)

| Threat | Mitigation |
|--------|-----------|
| Stolen/guessed device key | 256-bit secret, hash-only storage, constant-time compare, rotate/revoke, per-device scope, identical failure responses, rate limits |
| Compromised device pushes hostile data | Strict validation and hard caps on every field/list (4 MB body limit), event allow-list enforced server-side, control characters stripped, tenant-bound scope, reports can only affect the device's own rows |
| Cross-tenant access | Tenant from token/key only; EF filters; write interceptor; **tenant-safe composite FKs**; PostgreSQL RLS (forced) on all 9 tables; tested via API and raw SQL |
| Malicious/over-broad advisory (ReDoS, match-everything) | Plain substring matching (no regex), ≥3 characters |
| Server-driven code execution on endpoints | Not possible by construction: catalogue key + closed-set parameter, agent-side implementations, local opt-in |
| Insider approves their own change | Domain rule (not just UI); policy needs Owner ≠ author/submitter; High risk needs Owner; double-approve races caught by optimistic concurrency (`xmin`) |
| Audit tampering | Append-only via RLS + privileges + trigger; same-transaction writes |
| Spreadsheet formula injection | CSV cells neutralised |
| Sensitive data exposure | Members have no access; key hash never serialised; event bodies reduced to summaries; `Cache-Control: no-store` on key responses |

## Testing

* **Unit (131):** every evaluator rule incl. unknown/not-applicable semantics, determinism, scoring and caps, banding, version compare, event rules, CSV neutralisation, device-key handling, catalogue validation, all domain state machines (self-approval, expiry, lifecycle), and the **agent contract** (real script output deserialises, validates and can never score Compliant).
* **Integration (55 total, 24 new) against real PostgreSQL with RLS:** authorization matrix (anonymous/Member/Admin on every read endpoint), controller-surface reflection test, device-key lifecycle and uniform-401 behaviour, finding open/resolve/re-open, "missing data never resolves", rate limiting, hostile payload rejection, event allow-list and dedupe, advisory re-evaluation, risk-acceptance rules, four-eyes for policy and remediation, high-risk gating, auto-approval only for Low risk, detect-only hard stop, approval-not-a-standing-licence, tenant isolation (API, forged `X-Tenant-Id`, raw SQL RLS), audit immutability for app role, schema owner and superuser, CSV, summary.
* **Frontend (Vitest):** severity/band semantics, "not antivirus/EDR" notice, no misleading zero score, nav behaviour. `tsc`, ESLint and `next build` pass.
* **Not tested here:** the PowerShell agent on real Windows (it was parsed and run on Linux, where every Windows cmdlet is absent, which verifies error handling and the report contract only); load/scale; browser E2E.

## Known limitations and suggested follow-ups

* No notification delivery (e-mail/Teams/PagerDuty) for alerts.
* Vulnerability data is manual; consider an import/sync from a vetted feed (NVD/OSV/vendor) plus CPE matching, with provenance.
* Rate limiting keys on the connection IP; behind a reverse proxy configure forwarded headers.
* Policy activation re-evaluates up to 2 000 devices synchronously; larger fleets update on their next report (a background job is the next step).
* Audit is append-only but not hash-chained; no external export.
* Agent: no installer/service wrapper, no self-update, runs on a schedule (not event-driven); BitLocker "unavailable" is inferred from a missing cmdlet.
* Device-scoped, not user-scoped: no per-user data collected.
* Retention: last 200 snapshots per device (only the latest keeps the raw report), events 90 days; no tenant-configurable retention.
