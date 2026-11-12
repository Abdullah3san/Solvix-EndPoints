# Security Architecture

**Status:** Phase 01 controls implemented; everything marked *(Phase N)* is designed
and not yet built. Decisions are referenced as ADR-x from [DECISIONS.md](DECISIONS.md).

> **Compliance statement.** Solvix-EndPoint is designed to support future SOC 2 Type II
> and ISO 27001 certification and GDPR obligations. **No certification has been
> obtained.** Product, website and sales material must not claim otherwise.

## 1. Trust boundaries

```
 Internet ──► [Caddy TLS] ──► web (Next.js BFF) ──► api ─┐
                     │                                   ├─► PostgreSQL (RLS)  Redis
 Endpoints ──────────┴──────► gateway ───────────────────┤
                                  └─► guacd (internal)   └─► worker ──► object storage, KMS, e-mail
```

| Boundary | Principal | Authenticated by | Trusted for |
|----------|-----------|------------------|-------------|
| Browser → BFF | User | httpOnly session cookie (`__Host-`) + CSRF check | nothing by itself; BFF attaches the access token |
| BFF → api | User (tenant-bound) | Access token (JWT) | `sub`, `tid`, `ptid`, `amr`, `auth_time` |
| Endpoint → gateway | Device | Device access token (ADR-006) | `sub`=device, `tid` (from server-side device record) |
| gateway → endpoint | Platform | Command envelope signature (KMS key) | command authenticity, binding to device, freshness |
| worker → DB maintenance | Platform job | separate DB role | partition/retention only |
| Platform operator | Solvix staff | separate audience + MFA (later) | time-boxed, customer-consented support access |

**Rule:** the tenant is never taken from a URL, body, header, domain name or agent
payload. It comes from a signed token or from the server-side device record.

## 2. Tenant isolation

### 2.1 Implemented (Phase 01)
Four layers, described in [ARCHITECTURE.md §6](ARCHITECTURE.md): token-only tenant
resolution with a membership re-check per request; EF global query filters;
SaveChanges tenant enforcement; PostgreSQL FORCE RLS under a `NOBYPASSRLS` runtime role.

### 2.2 Changes required (Phase 02)
* **Remove the RLS bypass variable** (ADR-004, risk R-1). Platform operations move to
  narrow `SECURITY DEFINER` functions and a separate maintenance role.
* **Partitions** are reachable only through RLS-protected parents (ADR-009).
* **Tests** for every new tenant-owned endpoint: the cross-tenant matrix (tenant A
  token × tenant B ids ⇒ 404; foreign references ⇒ 404/422) is generated from the
  OpenAPI document so a new endpoint cannot ship without coverage.

### 2.3 MSP isolation (Phase 02, ADR-003)
Invariants, each with an automated test:
1. A request executes in exactly one tenant. A partner user acting for customer C has
   `tid = C` and is subject to C's RLS like any other user.
2. Partner access requires: an active partner membership, MFA enrolled and used for the
   session, an active link partner↔C, and a non-expired grant covering C. All four are
   re-checked per request (cached ≤ 60 s, invalidated on change).
3. A customer has at most one active managing partner (unique index). Partner P cannot
   create a link to a customer without that customer's code, unless P provisioned it.
4. Revoking a link immediately invalidates cached access, refresh-token families scoped
   to that customer, and live SignalR connections for those users.
5. The customer Owner sees the managing partner, the partner users who acted, and every
   partner action in the customer audit log. Optionally, tier-2/3 actions by partner
   users require customer-side approval (OD-6).
6. A partner tenant cannot own devices, enrollment tokens or any device-plane data.

## 3. Authentication (users)

| Control | State |
|---------|-------|
| Password hashing (ASP.NET Identity PBKDF2) + password policy | Implemented |
| BFF: access token never in browser JS, httpOnly cookie | Implemented |
| Asymmetric JWT signing (ES256) with `kid`, rotation, JWKS shared by api/gateway/worker | Phase 02 (replaces HS256) |
| Access token 10 min; rotating refresh token with reuse detection → family revocation | Phase 02 |
| "Sign out everywhere" and admin session revocation | Phase 02 |
| TOTP MFA + recovery codes; WebAuthn/passkeys | Phase 02 (TOTP), Phase 10 (WebAuthn) |
| MFA mandatory: partner users, Owners, any role with tier-2/3 permissions | Phase 02 |
| Step-up: `auth_time` ≤ 10 min for tier-2/3 commands, approvals, remote sessions, role/grant changes, enrollment-token creation, tenant deletion/export | Phase 02 policy, enforced as features ship |
| Login/MFA/registration rate limits per IP and per account (Redis; **fail closed**) | Phase 02 |
| Breached-password check (offline k-anonymity list) | Phase 02 |
| E-mail verification, invitations, password reset (single-use, hashed, 30 min) | Phase 02 |
| OIDC SSO (Entra ID, Google) | post-V1 |

**Custom domains (CH-7):** a verified custom domain only selects branding and a login
hint. Cookies are host-only (`__Host-`), so a session on `app.platform` is not valid on
`portal.msp.com` and vice versa. CSRF origin checks accept only the request's own
verified host.

## 4. Authorization

* Permission-based RBAC with scopes (ADR-005). Every controller action declares a
  permission (`[RequirePermission(...)]`). An architecture test fails if any action lacks
  one or is not explicitly `[AllowAnonymous]`.
* Effective permissions = the union of role assignments for the membership (or partner
  grants) whose scope covers the target. Branch/device-group scopes are applied as query
  filters in the application layer; RLS stays tenant-only.
* Separation of duties: a requester can never approve their own request; approvers need
  `approvals.decide` within the target's scope.
* The UI hides actions the user lacks, but **the server is always authoritative**.

V1 system roles:

| Role | Summary |
|------|---------|
| Owner | Everything, including tenant deletion, billing, MSP links, enabling ad-hoc PowerShell |
| Administrator | Everything except Owner-only actions |
| Technician | Devices, inventory, tier-0/1 commands, script library execution, software deployment, remote sessions (subject to approval policy) |
| Helpdesk | Read devices/inventory, tier-0 commands, restart, remote sessions with approval |
| Auditor | Read-only, including the audit log; no commands |
| Viewer | Read-only devices and inventory |

`remote.powershell.adhoc` is in **no** default role. It must be granted explicitly by an
Owner after the tenant feature is enabled.

## 5. Device identity and enrollment lifecycle (Phase 04, ADR-006)

```
            create token (Admin, MFA)                         admin approve
 [token] ───────────────► agent install ──► enroll ──► PendingApproval ──────► Active
                                                   └─(auto_approve)──────────►   │  ▲
                                                                                 │  │ release
                                           anomaly / admin ────────────► Quarantined
                                                                                 │
                                    retire (admin) / uninstall ───────────► Retired ──(retention)──► deleted
```

1. **Enrollment token:** created in a *customer* tenant by a user with
   `devices.enroll`. 256-bit random, shown once, stored as SHA-256, prefix
   `sxe_enr_` (so secret scanners can detect it). Default expiry 7 days, max 30; optional
   max uses; optional target branch/group; revocable.
2. **Install:** `msiexec /i SolvixAgent.msi /qn SERVER=https://agents.<domain> TOKEN=…`.
   The token is written to a SYSTEM-only ACL'd location and deleted after enrollment.
3. **Key generation:** a P-256 key in the TPM (Microsoft Platform Crypto Provider). If no
   usable TPM exists, a non-exportable software CNG machine key is used and the device
   is flagged `key_protection = software`; tenant policy can refuse such devices.
4. **Enroll:** `POST /agent/v1/enrollment/challenge` → nonce;
   `POST /agent/v1/enrollment` {token, public key, signature over nonce, hardware
   fingerprint, OS, agent version}. Rate-limited per IP and per token. The server
   validates the token, proof-of-possession and quota, then creates the device and its
   credential and returns `deviceId`, agent configuration and the signed command-signing
   key set.
5. **Authenticate:** `POST /agent/v1/auth/challenge` → `POST /agent/v1/auth/token`
   {deviceId, nonce, signature} → 15-minute ES256 token, with `cnf` = key thumbprint.
   Issuance is refused for retired or quarantined-with-block devices and for revoked
   credentials.
6. **Revocation:** retiring a device or revoking a credential publishes an event; every
   gateway node closes that device's socket within seconds. Status is cached in Redis
   ≤ 30 s for HTTP calls.
7. **Key rotation:** the agent can register a new key, signed by the old key; the old
   credential is revoked after confirmation. Admins can force rotation.
8. **Re-enrollment and clones:** a new enrollment whose fingerprint matches an active
   device in the same tenant is linked as a re-install (old credential revoked, history
   kept). Two concurrent sessions for one device, or a changed fingerprint under the same
   key, trigger **Quarantined** plus a security event (VM cloning, R-10).
9. **Quarantine:** telemetry is accepted, but all commands except `agent.release-check`
   and `agent.uninstall` are refused. Shown prominently in the UI.

Device-originated data (hostnames, inventory, event text) is **untrusted input**. It is
validated, length-capped and encoded on output, and never interpreted as instructions
(important for future AI features).

## 6. Remote command security model (Phase 06, ADR-010)

### 6.1 Command definitions
Commands are typed, versioned, allow-listed definitions with a JSON parameter schema,
validated on both server **and** agent. There is no "run this string" endpoint.

| Tier | Examples | Permission | Step-up MFA | Approval | Fan-out without approval |
|------|----------|-----------|-------------|----------|--------------------------|
| 0 Read | refresh inventory, list services/processes, scan updates | `commands.read` | no | no | unlimited |
| 1 Operate | restart service, restart/shutdown device (with user notice/deferral), install approved package | `commands.operate`, `software.deploy` | no | tenant policy | 50 devices (configurable) |
| 2 Script library | run an **approved** library script version | `scripts.execute` | yes | tenant policy (default: off for staff, on for partner users) | 10 devices |
| 3 Ad-hoc PowerShell | technician-authored script | `remote.powershell.adhoc` + tenant feature enabled | yes | default **on** for >1 device and for partner users | 1 device |

### 6.2 Execution pipeline
```
request ─► validate (schema, permission, scope, tier limits, quota, idempotency key)
        ─► approval (if required; requester ≠ approver; approver step-up MFA)
        ─► per device: allocate sequence, nonce, expiry ─► sign (ICommandSigner/KMS)
        ─► dispatch via SignalR (or queued for polling) ─► agent verifies ─► executes
        ─► ack/result bound to executionId ─► audit at every transition
```
At dispatch time the request is **re-authorized**: approval still valid, requester still
holds the permission, device still active and not quarantined.

### 6.3 Signed envelope
```json
{ "v":1, "executionId":"…", "requestId":"…", "tenantId":"…", "deviceId":"…",
  "command":"device.restart", "commandVersion":1, "parameters":{…},
  "contentSha256":"… (scripts/packages)", "issuedAt":"…", "notBefore":"…",
  "expiresAt":"… (≤ 15 min, ≤ 24 h for queued maintenance)", "nonce":"128-bit",
  "sequence":1234, "approvalId":"…", "kid":"cmd-2026-11" }
```
Canonical JSON (RFC 8785) is signed with ECDSA P-256 / SHA-256 by a KMS key.

**Agent verification order:** known `kid` in the pinned, signed key set → signature →
`deviceId`/`tenantId` match local enrollment → time window (±5 min skew; the agent
tracks server time offset) → nonce unseen (persisted for the max validity window) →
`sequence` > last executed (persisted) → command implemented locally → parameters valid
→ content hash matches the downloaded artifact. Any failure means **reject**, report a
security event, and do not execute.

### 6.4 Ad-hoc PowerShell (tier 3) specifics (CH-2)
* Each execution is a **submitted script artifact**, not an interactive keystroke shell.
  The script is stored immutably (hash in the envelope) so the audit trail shows exactly
  what ran.
* The tenant feature is off by default; enabling it is Owner-only with MFA and is
  audited. The permission is granted explicitly per user and scope.
* Pre-submit checks: size limit (64 KB), secret-pattern scan (warn or block on embedded
  credentials, tokens or private keys), and the risk banner shown to the user.
* The agent writes the script to a SYSTEM-only ACL'd temp directory and runs
  `powershell.exe -NoProfile -NonInteractive -File …` under SYSTEM (or the logged-on
  user context if the definition says so). Timeout ≤ 10 min, output cap 1 MB
  (stored in object storage, preview in DB). The file is deleted afterwards. **AMSI and
  Defender are never bypassed.**
* AI can never create or submit tier-3 scripts (ADR-015).

### 6.5 Restart and shutdown
These are tier 1 with a mandatory confirmation dialog and optional user notification and
deferral. Fan-out above the limit requires approval. Servers (OS type `server`) require
approval by default.

## 7. Remote desktop (Phase 09, ADR-011)

1. A technician requests a session → permission `remote.desktop` + step-up MFA →
   approval if tenant policy requires (default on for partner users).
2. The server sends a signed `remote.tunnel.open` command {sessionId, target
   `127.0.0.1:3389`, expiry}. The agent opens an outbound WebSocket tunnel to the gateway
   with a session-bound one-time token.
3. The gateway connects guacd to the tunnel endpoint on the internal network. The browser
   connects to the gateway's Guacamole tunnel with a short-lived, single-use, user- and
   session-bound ticket.
4. The technician enters Windows credentials into the RDP/NLA prompt. **They are never
   stored or logged.**
5. If RDP is disabled on the device, enabling it is itself a tier-2 command and is
   reverted when the session ends. The firewall is not opened to the network because the
   tunnel connects via loopback.
6. Session start, end, duration and participants are audited. Recording (guacd) is
   optional per tenant, stored encrypted in object storage, and retained 30 days; viewing
   a recording requires `remote.recordings.read` and is itself audited.
7. Sessions have a maximum duration and an idle timeout, and an admin can terminate one.

## 8. Software deployment (Phase 07)
Packages are uploaded through the secure upload pipeline (§10). A package version has
fixed silent-install arguments and a detection rule; deployment parameters cannot add
free-form arguments. The agent downloads through a short-lived pre-signed URL, verifies
SHA-256 (from the signed envelope) and optionally the Authenticode publisher, then
installs.

## 9. Agent and release integrity (ADR-012)
* Binaries and the MSI are Authenticode-signed with a publicly trusted certificate whose
  key sits in a cloud HSM signing service or hardware token. **No private key is ever
  in the repository, CI variables, images or distributed software.**
* CI signing uses short-lived federated credentials and a protected `release`
  environment with manual approval.
* The update manifest is separately signed by the Solvix release key (KMS). The agent's
  updater checks the manifest signature, SHA-256 and Authenticode publisher, then installs.
  It rolls back automatically if the new agent fails its health check within 10 minutes.
* Rollout rings: internal → 5% → 25% → 100%, each with a hold period, and pause on
  error-rate threshold.
* Development builds are signed with an internal test certificate trusted only on test
  machines.
* Configuration keys: `Solvix:Signing:CodeSigningProvider`, `…:CertificateSubject`,
  `…:CommandKeyId`, `…:ReleaseKeyId` (PR-11).

## 10. Secure file uploads (Phase 07)
The browser uploads to a quarantine prefix through a pre-signed URL (size limit per
type), followed by a magic-byte/type allow-list check, SHA-256, malware scan (ClamAV
container) and a move to the clean prefix, which sets `scan_status = clean`. Only clean
files are deployable or downloadable. Downloads use short-lived pre-signed URLs with
`Content-Disposition: attachment`.

## 11. Web application controls
* CSRF: BFF state-changing routes require `Sec-Fetch-Site: same-origin` or a matching
  `Origin`, plus a double-submit token. The backend API is bearer-only.
* Headers: CSP with nonces (no `unsafe-inline` scripts), HSTS (preload after GA),
  `frame-ancestors 'none'`, `Referrer-Policy: strict-origin-when-cross-origin`,
  `Permissions-Policy`, `X-Content-Type-Options`.
* Output encoding: React escaping. A lint rule bans `dangerouslySetInnerHTML`; markdown
  (later AI output) goes through a sanitizing renderer.
* Input validation: FluentValidation on every DTO (implemented). List filters use a
  whitelisted field/operator grammar; no dynamic SQL.
* Rate limiting: per IP, user, tenant and device (gateway) with tighter limits on
  authentication, enrollment and command creation; responses are `429` with `Retry-After`.

## 12. Secrets and keys

| Secret | Storage | Rotation |
|--------|---------|----------|
| DB/Redis passwords, OpenAI key, e-mail API key | Docker secrets (files, root-only) on the VPS; source copies encrypted with SOPS+age in a separate private infra repository | 90 days / on staff change |
| JWT signing keys | generated keys in the secret store, `kid` rotation every 90 days with overlap | automated |
| Command-signing key, release key | Cloud KMS, non-exportable (ADR-010/012) | yearly, signed key-set rollover |
| Code-signing key | CA cloud HSM / hardware token | per certificate validity |
| Application data keys (MFA seeds etc.) | envelope encryption, KEK in KMS / secret store | yearly re-wrap |

Secret scanning (gitleaks) runs in CI and as a pre-commit hook.

## 13. Audit and security monitoring
* Audit events (DATABASE §5) cover authentication, membership/role/grant changes, MSP
  links, enrollment, every command transition, approvals, remote sessions, exports,
  deletions, and settings changes. They are hash-chained and append-only.
* Security events raise alerts: repeated login failures, MFA failures, envelope
  verification failures reported by agents, clone detection, enrollment bursts,
  cross-tenant access attempts (403/404 patterns), partner access from new countries
  or IPs, and tier-3 executions.
* Endpoint security monitoring (V1, PR-6): Defender status and detections (event 1116),
  firewall profiles, BitLocker, UAC, local administrators membership changes (4732),
  account creation (4720), new services (7045), audit log cleared (1102), failed logon
  bursts (4625, aggregated). Collected data is minimized: counts and identifiers, not
  free-form message bodies.
* Export to SIEM (syslog/webhook): post-V1.

## 14. AI (post-V1, ADR-015)
* Server-side only; key in the secret store; opt-in per tenant; disclosed as a
  sub-processor.
* Pipeline: user request → tenant-authorized backend tools (the caller's permissions and
  scope) → **field allow-list per tool** → redaction (secrets, tokens, keys, e-mail
  addresses, user names → pseudonyms) → model → output schema validation → UI rendering
  through the sanitizer.
* Never sent: passwords, credentials, tokens, private keys, recovery codes, script
  bodies flagged as containing secrets, personal data beyond pseudonymized identifiers.
* Mutating tools create **proposals** only. They become commands through the normal
  pipeline (permission, approval, signing). Tier-3 is unavailable to AI.
* Prompt-injection posture: device-originated text is delimited and treated as data; tool
  results cannot grant permissions; every tool call is audited.

## 15. Encryption
* In transit: TLS 1.2+ (1.3 preferred) at Caddy; HSTS; internal Docker networks are
  isolated. If database or Redis traffic ever leaves the host, TLS (`sslmode=verify-full`)
  becomes mandatory.
* At rest: encrypted off-site backups and object storage (server-side encryption plus
  client-side encryption for exports and recordings); application-level encryption of
  secrets. Full-disk encryption of the VPS volume depends on the provider and is
  recorded in INFRASTRUCTURE.md. Where unavailable, LUKS on the data volume is the
  documented option, with its boot-unlock trade-off.

## 16. Compliance readiness (SOC 2 / ISO 27001 / GDPR)
Built-in evidence sources: audit log, access reviews (export of role assignments and
partner grants), change management (PR reviews and CI), vulnerability management
(dependency scanning, image scanning), backup/restore test records, incident runbooks,
retention configuration, DPA and sub-processor list, data export/deletion workflow
records. Organizational policies (HR, vendor management, risk assessment) are outside
this repository but must exist before an audit window starts.
