# Development Roadmap

**Status:** Proposed for approval · see CH-1 in [DECISIONS.md](DECISIONS.md) for why
Phases 02–03 precede the device plane.

Every phase follows the engineering workflow: review the repository → acceptance
criteria → implement → tests → run tests → security review → documentation and
CHANGELOG. No phase starts before the previous phase's exit criteria are met, unless a
parallel track is noted.

**V1 scope (PR-6):** Windows inventory, online/offline monitoring, remote PowerShell with
strict authorization, restart/shutdown, basic software deployment, Windows Update
visibility, basic security monitoring, remote desktop (unattended RDP), MSP tenancy.

| Phase | Theme | Key deliverables | Exit criteria |
|-------|-------|------------------|---------------|
| **01** ✅ | Platform foundation | Tenancy, identity, organization, 4-layer isolation, BFF shell, Docker | Done – [PHASE-01-SUMMARY.md](PHASE-01-SUMMARY.md) |
| **02** | Security, MSP tenancy and audit foundation | ADR-004 RLS hardening; module schemas (ADR-016); partner tenants, links, grants, access resolver, tenant switcher API; permission RBAC + scopes with Phase 01 role migration; ES256 JWT + rotating refresh tokens + revocation; TOTP MFA + step-up; rate limiting; invitations, e-mail verification, password reset (needs OD-4); audit module (hash chain) + outbox; CI (build, tests, lint, gitleaks, dependency + image scan) | Cross-tenant and cross-partner test matrix green; every action permission-annotated (architecture test); R-1 regression test; auth abuse tests |
| **03** | UI foundation *(may overlap with late 02)* | shadcn/ui migration, tokens, DataTable/filters/empty/error/confirm patterns; users, roles, partner grants, customer links screens; tenant switcher with partner indicator; audit log viewer; MFA enrolment UI; Playwright E2E + axe | Accessibility checks pass; E2E for login, MFA, switch tenant, partner flows |
| **04** | Device plane foundation | `Solvix.Gateway` + `Solvix.Worker` hosts; Hangfire; enrollment tokens; device identity (TPM key, device tokens); SignalR agent channel; presence pipeline; partitions + retention jobs; agent MVP (service, updater skeleton, enrollment, heartbeat, basic inventory); devices list/detail UI with live presence; MSI build + test signing; simulated agent | DEVICE_PLATFORM §8 acceptance criteria (incl. 10k simulated devices) |
| **05** | Inventory, posture and update visibility | Full hardware/software inventory with history; tenant software titles; device groups (static/dynamic); security posture + security events; Windows Update scan visibility; agent self-update with signed manifests and rings | Inventory accuracy on reference machines; update rollback test |
| **06** | Commands and approvals | Command definitions, KMS signer (OD-1), envelopes, agent verification; tier 0–1 commands incl. restart/shutdown; approvals module; script library; ad-hoc PowerShell (tier 3) with all CH-2 controls; command audit and live progress UI | Red-team tests: replay, tampered/expired envelope, wrong device/tenant, revoked approval, partner without grant, all rejected; independent security review |
| **07** | Software deployment | Secure upload pipeline + ClamAV; packages and versions; deployment to devices/groups; results | Malicious/oversized/mistyped uploads rejected; hash mismatch blocks install |
| **08** | Monitoring and alerts | Metrics pipeline and rollups; alert rules; in-app + e-mail notifications; dashboards | 10k simulated devices metrics ingest within budget |
| **09** | Remote desktop | Spike (week 1): guacd tunnel vs. auth-json; attended-support option (OD-5). Then: agent tunnel, gateway Guacamole tunnel, session approval, audit, optional recording | Security review of tunnel; session audit complete |
| **10** | GDPR tooling and production readiness | Tenant export, deletion workflow, user erasure, retention verification, backup/restore drill, monitoring/alerting, pen test, DPA/sub-processor list, WebAuthn | INFRASTRUCTURE §11 checklist; **V1 GA** |
| post-V1 | Expansion | AI assistant (ADR-015); third-party patch catalog; MSP portal (cross-customer dashboards, billing); SSO/SCIM; public API keys; SIEM export; ARM64 agent; macOS/Linux agents; Apple MDM; Android Enterprise | – |

## Parallel, non-code tracks (start now)
* **Code-signing certificate** (OD-2): organization validation can take weeks. Needed
  as a real certificate by Phase 05 (self-update); a test certificate suffices for 04.
* **Hosting provider, region, object storage, backup storage** (OD-3) before Phase 04
  staging.
* **E-mail provider** (OD-4) before Phase 02 invitations.
* **Legal:** DPA template, privacy policy, sub-processor list; license review for any
  remote-support component (OD-5).
