# Security review: Phase 12 and production readiness

**Date:** 2026-11-16. **Scope:** all code added in Phase 12 (Billing, Helpdesk, Reporting, host hardening, operations
scripts, frontend pages) and the cross-cutting controls they depend on (tenant resolution, RLS, authorization).
**Out of scope / not re-reviewed:** the Windows agent and device PKI (Phase 03, reviewed then), the Identity module's password
handling, container base images, the hosting environment.

**Method:** manual code review of every new endpoint and trust boundary; abuse-case integration tests written *as attacks*
(cross-tenant access, forged webhooks, token splicing, hostile uploads/e-mail, privilege changes); database-level checks
(RLS, privileges, constraints); dependency scans (`dotnet list package --vulnerable --include-transitive` and
`npm audit --omit=dev`: **0 findings**); a backup/restore rehearsal. **Not done:** external penetration test, DAST/SAST
tooling, load/soak testing, container image scanning. These are listed under *Open items*; none of them is replaced by this
document.

## 1. Findings fixed during the phase

| # | Severity | Finding | Fix | Regression test |
|---|----------|---------|-----|-----------------|
| F1 | **High** | A role change did not take effect until the user's token expired: the role came from the token, only "active membership" was re-checked. A demoted Owner/Admin kept billing, reporting and helpdesk-admin rights for up to the token lifetime (60 min default). Pre-existing, but Phase 12 added Owner-only billing. | `TenantResolutionMiddleware` now overwrites the role claim with the live membership role on every request. | `A_demoted_member_loses_admin_and_owner_rights_immediately…` |
| F2 | Medium | ReDoS / CPU exhaustion: regexes ran on attacker-controlled input (inbound e-mail HTML/subject, ticket text passed to the AI redactor) without bounds. | Input caps before matching, 250 ms match timeouts on every pattern, timeouts caught and **fail closed** (fixed body text; AI path falls back to the local summariser so unredacted text is never sent). | `Crafted_html_cannot_burn_unbounded_cpu`, `Crafted_subjects_and_redaction_input_stay_fast` |
| F3 | Medium | Manual payment confirmations used a reference that was not globally unique, so two tenants confirming with the same reference collided on the replay-protection index (integrity/availability, found by a test). | Reference now includes the invoice id. | `BillingPaymentsTests` (3 tests failed before the fix) |
| F4 | Low–Med | The webhook relied only on `[RequestSizeLimit]`, which is not enforced for chunked bodies / in-process hosts. | Explicit bounded read + `Content-Length` check (413). | `Webhook_endpoint_only_exists_for_the_active_provider_and_limits_body_size` |
| F5 | Medium (correctness) | Ticket queue was ordered by the stored text of the priority (alphabetical: Medium > High > Critical > Low), hiding urgent tickets. Found by the DR drill. | Explicit severity ranking. | `The_queue_is_ordered_by_severity_not_alphabetically` |
| F6 | Low | Replacing SLA targets on an existing policy was refused: EF treats a child with a client-generated key reachable from a tracked parent as *Modified*; the tenant-immutability interceptor correctly blocked it. | New targets are explicitly added; documented in the domain method. | `Sla_policies_can_be_managed_with_exactly_one_default` |
| F7 | Low | No security response headers; sign-in/registration unthrottled; operator API unthrottled. | `SecurityHeadersMiddleware` (nosniff, frame deny, CSP `default-src 'none'`, referrer, permissions, CORP, HSTS on HTTPS); shared `auth` rate-limit policy on login, tenant switch, registration and the operator API. | `HardeningTests` |
| F8 | Low | Empty `content` array from the AI API raised instead of falling back. | Safe parse + fallback. | `Failures_fall_back_to_the_extractive_summary` |
| F9 | Low | `Resuming` an SLA truncated remaining time to whole minutes (up to 59 s lost per pause, in the customer's disfavour). | Exact `TimeSpan` arithmetic. | `Resuming_never_shaves_seconds_off_the_remaining_time` |

## 2. Controls verified for the requested security requirements

| Requirement | Control | Evidence |
|-------------|---------|----------|
| **Protect billing data** | Owner-only endpoints for invoices, history and billing profile (Admins refused); no card data stored (hosted checkout; only provider ids + amounts); tax rate and plan prices not tenant-editable; invoice HTML encoded + `default-src 'none'`; webhook secrets/payloads/bodies never logged; billing tables under forced RLS; secrets only via environment | `Billing_data_is_owner_only…`, `Invoice_html_is_encoded…`, `Tax_comes_from_the_operator…`, `The_runtime_database_role_sees_no_billing_rows…` |
| **Enforce device limits on the backend** | Checked inside the enrollment transaction under a per-tenant advisory lock; fail-closed entitlement computed from timestamps (no reliance on a job); 402 responses; token not consumed on refusal | `The_device_limit_is_enforced_by_the_server_at_enrollment`, `Concurrent_enrollments_can_never_exceed_the_limit` (8 parallel ⇒ exactly 3), `A_lapsed_subscription_blocks_new_devices_even_if_no_job_has_run`, `Suspension_blocks…` |
| **Never rely on frontend subscription checks** | The UI only displays state; every limit/plan rule lives in the API; the web proxy forwards nothing that can bypass it | same tests, all via raw HTTP |
| **Subscription operations cannot affect another tenant** | Tenant comes only from the signed token; RLS on all 8 billing tables; composite tenant-safe FKs; operator actions run in a scope bound to the named tenant (other tenant's invoice id ⇒ 404); webhooks derive the tenant from *our* invoice and reject a mismatching signed tenant; settlement runs in a single-tenant scope; per-tenant invoice numbers | `Invoices_and_billing_profiles_are_isolated_between_tenants`, `A_signed_event_cannot_attribute_one_tenants_invoice_to_another_tenant`, `Reports_never_include_another_tenants_data`, `Tickets_and_catalogs_are_isolated_between_tenants`, generic RLS tests (33 tenant tables) |

## 3. Threat summary (new attack surface)

| Entry point | Auth | Main threats considered | Mitigations |
|-------------|------|------------------------|-------------|
| `/billing/*` | JWT, owner/member | IDOR on invoices, privilege escalation, XSS via invoice, price tampering | tenant-scoped queries + RLS, policies, server-side pricing from the catalog, encoded HTML + CSP |
| `/billing/webhooks/stripe` | HMAC signature | forgery, replay, tenant confusion, amount tampering, double settlement, DoS | signature + timestamp window, idempotency table, invoice-derived tenant, amount/currency match, 256 KiB cap, rate limit |
| `/platform/billing/*` | shared key | key guessing, use from the internet | ≥32-char key, constant-time compare, rate limit, 404 when unset; **must be network-restricted** |
| `/helpdesk/inbound-email` | channel secret (header) | forged mail, hijacking a ticket by number, flood, loops, hostile HTML, spliced tokens, oversized bodies | hashed per-channel secret bound to tenant, requester-only threading, dedupe, per-sender cap, auto-reply drop, bounded parsing, 16 MiB cap |
| Attachments | JWT / email | malware, content-type spoofing, stored XSS, path traversal, huge files | extension allow-list + signature check, sanitised names, download-only with sandbox CSP, size and count caps |
| Ticket content | JWT | stored XSS, prompt injection, data leakage to AI | rendered as text, redaction, internal notes excluded from AI by default, untrusted-data prompt framing |
| `/reports/*` | admin | data exposure, CSV/formula injection, SQL injection in search | admin-only, RLS, CSV neutralisation, LIKE escaping, parameterised SQL (jsonb paths are constants) |
| Background workers | n/a | cross-tenant leakage, duplicate work | per-tenant scopes (RLS), platform scope only to *list* tenants, advisory locks, idempotent writes |

## 4. Residual risks and open items (not fixed here)

| # | Item | Why it matters | Recommendation |
|---|------|----------------|----------------|
| O1 | No malware scanning of attachments | Staff download user-supplied files | Add ClamAV/Defender scanning before accepting uploads; keep download-only |
| O2 | No external pen test, SAST, DAST, container scan | This review is internal and manual | Add CodeQL + Trivy to CI; commission a third-party test of the public surface before general availability |
| O3 | Free-trial farming: anyone can register tenants (rate-limited per IP only) | Cost / abuse | CAPTCHA or e-mail verification, per-domain trial limits |
| O4 | Operator API is one shared secret with no per-person identity or MFA | Weak accountability | Keep internal; replace with an SSO-backed admin console; every action is audited as `Operator` |
| O5 | Access tokens last 60 min and cannot be revoked individually (membership and **role** are now re-checked per request) | A stolen token works until expiry | Shorten lifetime / add refresh tokens + revocation if the threat model needs it |
| O6 | Anonymous internet endpoints (webhook, inbound mail, agent enrollment) | Volumetric abuse | Put a WAF / provider IP allow-list in front; Stripe publishes webhook source IPs |
| O7 | AI provider (opt-in) receives redacted ticket text | Customer data processor / contractual | Obtain customer consent / DPA; consider a per-tenant switch |
| O8 | Device CA has no rotation procedure | A leaked CA key forces re-enrollment of all agents | Plan CA rotation (dual-trust window) before the fleet grows; HSM/KMS custody |
| O9 | No data-subject workflows (export/erasure) for tickets and billing | GDPR/CCPA obligations | Define retention; build export/delete jobs; billing records are normally retained for tax law |
| O10 | Attachments and billing data live in the same database as everything else | A DB compromise exposes them; backups carry them | Encrypted backups (done), disk encryption, restricted DB network; consider column-level encryption for tax ids |
| O11 | `GET /billing/usage` writes a usage snapshot (idempotent upsert) | A read with a side effect | Acceptable; the worker is the primary recorder |
| O12 | The compose overlays/images were not executed here | Config errors only show at deploy | Stage first (`PRODUCTION-READINESS.md`) |

## 5. Verdict

For a **controlled** rollout (pilot customers, `manual` billing provider first, AI disabled, operator API internal), I found
no unresolved issue that should block it, subject to the owner actions in `PRODUCTION-READINESS.md`. For general
availability close O1–O3, O6 and O8 and obtain the external test.
