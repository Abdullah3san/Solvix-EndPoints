# Phase 02 – Identity and Access Management: technical summary

## Scope delivered

| # | Requirement | Status | Where |
|---|-------------|--------|-------|
| 1 | Login and logout | Done | `POST /auth/login`, `/auth/mfa/verify`, `/auth/logout`; logout revokes the session on the server |
| 2 | User registration | Done | `POST /tenant/register` (workspace + owner); invited users register by accepting an invitation |
| 3 | E-mail verification | Done | Sign-in refused until confirmed; `/auth/verify-email`, `/auth/resend-verification` |
| 4 | Password reset | Done | `/auth/forgot-password`, `/auth/reset-password`; single-use, 60 min, signs out everywhere |
| 5 | Multi-factor authentication | Done | TOTP (RFC 6238) + 10 one-time recovery codes; per-tenant "require MFA" |
| 6 | Session management | Done | Server-side sessions with idle + absolute expiry; list/revoke own sessions; admins revoke a member's sessions |
| 7 | Refresh token rotation | Done | Opaque, single-use, hashed; reuse outside a 10 s grace window revokes the whole session |
| 8 | Account lockout | Done | 5 failures → 15 min, doubling per consecutive lockout (cap 24 h); atomic attempt reservation |
| 9 | Password policy configuration | Done | Platform baseline + per-tenant policy (length, classes, history, max age, session lifetimes, MFA) |
| 10 | Role-Based Access Control | Done | Tenant-owned roles, 7 built-in + custom roles, role assignment |
| 11 | Permission-based authorization | Done | `[RequirePermission("users.manage")]` on every protected action; role names never checked |
| 12 | User invitation | Done | E-mailed single-use link, roles fixed by the inviter, resend/revoke, existing accounts supported |
| 13 | User activation and deactivation | Done | Per-tenant membership; deactivation revokes the member's sessions in that tenant at once |
| 14 | Authentication audit logs | Done | Append-only `auth_audit_events` (DB triggers), tenant-scoped API with filters, UI |
| — | Default roles | Done | Super Administrator, Tenant Administrator, IT Manager, IT Technician, Asset Manager, Security Administrator, Read-Only Auditor |
| — | Tests for privilege escalation and cross-tenant access | Done | `PrivilegeEscalationTests` (19), `CrossTenantAccessTests` (9) plus related suites |

## How it works

### Sessions and tokens

```
browser ──cookie──► Next.js BFF ──Bearer access token──► API ──► session row (alive?) ──► membership + roles ──► permissions
                    (httpOnly)                                   PostgreSQL + RLS, Redis cache (30 s, invalidated on change)
```

* **Access token** – HS256 JWT, 15 min, carrying only `sub`, `tid`, `sid` (session), `email`, `name`, `amr`.
  It contains **no roles or permissions**; algorithm pinned to HS256, issuer/audience/lifetime validated.
* **Session** (`user_sessions`) – one per sign-in per tenant. Idle timeout slides on refresh; absolute lifetime is
  fixed at sign-in; both come from the tenant's security policy. Every request checks the session is alive, so
  logout, password change, deactivation, removal and admin revocation take effect on the next request even though
  the access token is still cryptographically valid.
* **Refresh token** (`refresh_tokens`) – 256-bit random, only its SHA-256 stored, valid as long as the idle
  window. Redeeming one marks it consumed (single atomic UPDATE) and issues a successor. Presenting a consumed token
  more than 10 s later is treated as theft: the session is revoked and `token.reuse_detected` is audited.
* **Tenant switch** issues a new session for the target workspace and revokes the current one.

### Authorization model

* **Permission catalog** (`SharedKernel/Authorization`) – 22 keys including all those in the brief
  (`devices.*`, `assets.*`, `mdm.manage`, `users.manage`, `security.manage`, `reports.view`, `billing.manage`) plus
  `users.view`, `roles.view/manage`, `security.view`, `audit.view`, `organization.view/manage`, `tenant.manage`.
  "manage" implies the matching "view" (expanded when a role is saved).
* **Roles are data, per tenant** (`roles`, `role_permissions`, `membership_roles`). Each tenant gets its own copy of
  the 7 built-in roles at registration; built-in roles are read-only through the API, custom roles are editable.
* **Enforcement** – every controller action must declare exactly one of `[AllowAnonymous]`,
  `[RequirePermission(...)]` or `[AuthenticatedOnly]` (own account pages); **the API refuses to start otherwise**
  (`ExplicitAuthorizationConvention`). `perm:<key>` policies are built on demand and evaluated against the
  permissions resolved on the server for this request and tenant. Denials return `403 permission_denied` naming
  the missing permission.
* **Privilege-escalation guards** (`PrivilegeGuard`), re-reading the actor's permissions from the database:
  1. nobody can grant a permission they do not hold (role assignment, invitation, creating/editing/deleting roles,
     reactivating a member);
  2. nobody can manage (re-role, deactivate, remove, sign out) a member who holds a permission they lack;
  3. nobody can change their own roles or deactivate/remove themselves;
  4. a tenant always keeps at least one active Super Administrator; role changes run under a per-tenant
     PostgreSQL advisory lock so two concurrent demotions cannot both succeed.
* **Built-in role → permissions**

  | Role | Permissions |
  |------|-------------|
  | Super Administrator | all 22 |
  | Tenant Administrator | all except `billing.manage`, `tenant.manage` |
  | IT Manager | all `devices.*`, all `assets.*`, `mdm.manage`, `reports.view`, `users.view`, `organization.view` |
  | IT Technician | `devices.view/enroll/remote_access`, `assets.view/edit`, `organization.view` |
  | Asset Manager | all `assets.*`, `devices.view`, `reports.view`, `organization.view` |
  | Security Administrator | `security.view/manage`, `audit.view`, `users.view`, `roles.view`, `devices.view`, `reports.view`, `organization.view` |
  | Read-Only Auditor | every `*.view` permission, including `audit.view` |

### Tenant scoping

Credentials (password, MFA, verification) belong to the platform-level **user**; everything a tenant controls is
tenant-owned and protected by the four Phase 01 layers (token-derived tenant, EF query filters, SaveChanges
enforcement, PostgreSQL FORCE RLS): `roles`, `role_permissions`, `membership_roles`, `user_sessions`,
`refresh_tokens`, `user_invitations`, `user_invitation_roles`, `tenant_security_policies`. Role assignments use
**composite foreign keys** `(tenant_id, membership_id)` / `(tenant_id, role_id)`, so "a member of A holds a role of
B" is unrepresentable even with RLS bypassed. `auth_audit_events` has a nullable tenant: tenant rows are visible to
that tenant only; rows without a tenant (e.g. failed sign-in for an unknown address) are platform-only.
A person in several workspaces has independent roles, sessions and administration in each; their password must
satisfy the **strictest** policy of all their workspaces.

### Brute-force and abuse protection

* **Account lockout** – a credential check first reserves an attempt with one atomic UPDATE
  (`failed_access_count < max`), then verifies. 40 parallel guesses verify exactly 5 passwords (tested). Applies to
  passwords, MFA codes, recovery codes and the current password on change. Lockout e-mails the owner.
* **MFA challenge** – 5 min, 5 wrong codes kills it; each TOTP time step is claimed atomically and never accepted
  twice (replay of a shoulder-surfed code fails).
* **Rate limiting** (ASP.NET Core, sliding window per client IP): credential checks 10/min, e-mail-sending 5/15 min,
  refresh/logout 120/min, registration 20/10 min; `429` with `Retry-After`. Configurable under
  `Solvix:RateLimiting`. `X-Forwarded-For` is honoured only from configured `Solvix:Network:TrustedProxies`.
* **No account enumeration** – unknown address, wrong password, locked and disabled accounts return the identical
  `401 invalid_credentials` after the same PBKDF2 work; forgot-password and resend-verification always answer `202`
  and send mail asynchronously so timing does not depend on whether a message was sent; per-account e-mail cooldown.

### Passwords and secrets

* PBKDF2-HMAC-SHA512, 210 000 iterations (configurable, floor enforced), per-password random salt
  (ASP.NET Core `PasswordHasher`); hashes with weaker parameters are upgraded transparently at sign-in.
* Policy: baseline min 10, lower-case + digit, history 5 (configurable); tenants may only tighten it; common
  passwords and passwords containing the user's name/e-mail are rejected; max age optional.
* TOTP secrets encrypted at rest with AES-256-GCM; recovery codes stored as HMAC-SHA256; keys derived with HKDF from
  `Solvix:Identity:DataProtectionKey` (separate from the JWT key). All bearer secrets (refresh, verification, reset,
  invitation, MFA challenge) are stored as SHA-256 hashes only.

### Browser security (Next.js BFF)

* Access and refresh tokens live **only in httpOnly cookies**, `SameSite=Lax`, and in production `Secure` with the
  `__Host-` prefix. Nothing is written to `localStorage`/`sessionStorage`; token responses never reach page JS.
* **CSRF** on every state-changing BFF request: `Origin` must be this site, `Sec-Fetch-Site` must not be
  cross-site/same-site, and the `X-CSRF-Token` header must equal the `__Host-solvix_csrf` cookie (double submit).
* The proxy refuses to forward token-returning API endpoints, renews expired access tokens with the refresh cookie
  (de-duplicated per token), and clears cookies only after the API confirms a session is dead
  (`/api/auth/session-ended`).
* One-time tokens travel in the URL **fragment** (`#token=…`): never sent to a server, removed from the address bar
  and history on load; those pages set `Referrer-Policy: no-referrer`. API responses carry `Cache-Control: no-store`,
  `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`.
* The UI hides controls the user cannot use (from `/auth/me` permissions), but every decision is made by the API.

## API reference (`/api/v1`)

| Method & path | Access |
|---------------|--------|
| `POST tenant/register` · `GET tenant/slug-availability` | anonymous, rate-limited |
| `POST auth/login` · `auth/mfa/verify` · `auth/refresh` · `auth/logout` | anonymous, rate-limited |
| `POST auth/verify-email` · `auth/resend-verification` · `auth/forgot-password` · `auth/reset-password` | anonymous, rate-limited |
| `GET auth/password-rules` | anonymous |
| `POST auth/invitations/preview` · `auth/invitations/accept` | anonymous (token), rate-limited |
| `GET auth/me` · `auth/workspaces` · `auth/me/password-rules` · `POST auth/switch-tenant` | signed in |
| `GET/DELETE auth/sessions[/{id}]` · `POST auth/sessions/revoke-others` | signed in (own sessions only) |
| `POST auth/change-password` · `GET auth/mfa` · `POST auth/mfa/totp/setup` · `auth/mfa/totp/confirm` · `auth/mfa/disable` · `auth/mfa/recovery-codes` | signed in (own account) |
| `GET users` · `GET users/{id}` · `GET invitations` | `users.view` |
| `PUT users/{id}/roles` · `POST users/{id}/activate` · `POST users/{id}/deactivate` · `DELETE users/{id}` · `POST/DELETE invitations[/{id}]` · `POST invitations/{id}/resend` | `users.manage` |
| `GET users/{id}/sessions` · `POST users/{id}/sessions/revoke` | `security.view` / `security.manage` |
| `GET roles` · `roles/permissions` · `roles/{id}` | `roles.view` |
| `POST/PUT/DELETE roles[/{id}]` | `roles.manage` |
| `GET/PUT security/policy` | `security.view` / `security.manage` |
| `GET security/audit-events?eventType=&outcome=&userId=&from=&to=&search=` | `audit.view` |
| Phase 01 endpoints | `organization.view` / `organization.manage`, `tenant.manage` for renaming the workspace |

Errors are RFC 9457 problems with a stable `code` (`invalid_credentials`, `email_not_verified`, `password_expired`,
`mfa_code_invalid`, `invalid_refresh_token`, `session_expired`, `permission_denied`, `mfa_enrollment_required`,
`insufficient_privilege`, `self_management_forbidden`, `last_super_administrator`, `system_role_immutable`,
`too_many_requests`, `csrf_rejected`, …).

## Database changes

Migrations `IdentityAccessManagement` (schema + data) and `IdentityAccessRowLevelSecurity` (RLS + append-only
triggers). The upgrade gives every existing tenant the built-in roles and a default policy and maps Phase 01
memberships: Owner → Super Administrator, Admin → Tenant Administrator, Member → a custom "Member" role
(`organization.view`, `users.view`); existing users are marked verified. `Down` restores the Phase 01 role column.
Both directions are covered by `Phase_01_tenants_and_members_are_carried_over_by_the_upgrade_and_back`.

## Configuration (new)

| Setting | Default | Notes |
|---------|---------|-------|
| `Solvix:Identity:DataProtectionKey` | — (required, ≥ 32 chars) | Encrypts MFA secrets; keep stable |
| `Solvix:Identity:WebBaseUrl` | `http://localhost:3000` | Links in e-mails |
| `Solvix:Identity:RequireEmailVerification` | `true` | |
| `Solvix:Identity:Lockout:*` | 5 attempts, 15 min, cap 1440 min | |
| `Solvix:Identity:Password:*` | 10 chars, lower+digit, history 5, 210 000 iterations | platform baseline |
| `Solvix:Identity:Session:*` | idle 480 min, absolute 168 h | defaults for new tenants |
| `Solvix:Jwt:AccessTokenMinutes` | 15 (max 60) | |
| `Solvix:Email:Smtp:*` | empty → mail is logged, not sent | Compose uses Mailpit (`http://localhost:8025`) |
| `Solvix:RateLimiting:*` | see above | |
| `Solvix:Network:TrustedProxies` | empty | proxies allowed to set `X-Forwarded-For` |
| `SOLVIX_WEB_ORIGIN`, `SOLVIX_COOKIE_SECURE` (web) | — / `false` | expected `Origin`; must be `true` behind HTTPS |

## Verification

Run in this environment against PostgreSQL 16 and Redis 7 (two-role layout, RLS enforced).

| Suite | Result | Notes |
|-------|--------|-------|
| `Solvix.UnitTests` | **116 / 116 passed** | RFC 6238 vectors, TOTP drift/replay, password policy & strictest-merge, permission catalog vs. constants, built-in role contents, AES-GCM tamper detection, keyed hashes, token generation, PBKDF2 parameters & re-hash, lockout back-off, session/role/policy/invitation domain rules (+ 44 Phase 01) |
| `Solvix.IntegrationTests` | **100 / 100 passed** (≈ 75 s) | real HTTP through the API host; e-mail captured at the last hop |
| Frontend `tsc` / `eslint` / `vitest` | clean / clean / **38 / 38 passed** | CSRF verifier, cookie names/flags, permission helpers, sidebar filtering, open-redirect guard |
| `next build` | succeeds | 24 routes |
| Browser end-to-end (Playwright, built app + API) | **21 / 21 checks passed** | see below |
| Production stack (`docker-compose.prod.yml`) | built and run; 21 / 21 browser checks over HTTPS | images built from the committed Dockerfiles; see `docs/DEPLOYMENT-VPS.md` |

Integration coverage by area:

* **Authentication** (17) – verification required before sign-in, single-use/replaced links, identical failures for
  unknown address vs. wrong password, short-lived access token without roles, refresh rotation, reuse detection
  revoking the session, concurrent refreshes within the grace window, logout via access or refresh token, password
  reset (silent for unknown accounts, weak/reused password rejected without burning the link, signs out everywhere),
  expired reset links, password change keeps current session only, own-session listing/revocation, idle and absolute
  expiry enforced server-side, tampered / `alg:none` / wrong-key / wrong-issuer / expired / unknown-session /
  other-tenant-session / other-user-session tokens rejected.
* **Brute force** (7) – lockout after 5 failures with identical responses, doubling lockouts, 40 parallel guesses →
  exactly 5 verifications, success resets the counter, password reset lifts a lockout, per-client rate limits across
  endpoints with `Retry-After`, e-mail cooldown.
* **MFA** (7) – enrolment needs password + valid code, secret encrypted and codes hashed at rest, challenge is not a
  session, code and challenge replay refused, 5 wrong codes kill the challenge and lock the account, recovery codes
  single-use, password reset does not bypass MFA, disabling needs password + code, tenant-required MFA blocks
  existing sessions until enrolment.
* **Invitations** (4) – exact invited roles regardless of request body, token never in API responses, existing
  accounts keep their credentials, revoked/expired/replaced links fail without creating accounts, one open
  invitation per address, deactivated members are reactivated rather than re-invited.
* **Privilege escalation** (19) – each built-in role grants exactly its catalogue permissions (7 cases × 10 probes);
  **every** API endpoint discovered from the routing table rejects anonymous callers and callers lacking its
  permission; grant ceiling for role assignment and invitations (including hiding a powerful role among allowed
  ones); no self-management; lower admins cannot touch higher ones; last Super Administrator protected, including a
  concurrent mutual-demotion race; role editors cannot create, widen, edit or delete roles above themselves
  (including via implied permissions); built-in roles immutable and not impersonable; role changes, deactivation and
  reactivation apply to already-issued tokens; forged role/permission/`amr` claims grant nothing; mass-assignment
  fields ignored.
* **Cross-tenant** (9) – roles, users, sessions, invitations of another tenant: 404 on read/update/delete/assign,
  including assigning B's Super Administrator role to A's member; a person in two tenants has separate permissions,
  sessions and administration (header spoofing and id guessing fail; switching yields the lesser role); security
  policies isolated while a shared account must meet the strictest; audit events isolated (and unknown-address events
  belong to no tenant); raw SQL under RLS sees/writes nothing of another tenant on all IAM tables; composite FKs
  reject cross-tenant role assignments even with RLS bypassed; audit trail append-only for every database role.
* **Policy & audit** (4), **migrations** (6, incl. Phase 01 → 02 data upgrade and back), plus the Phase 01 suites.

Browser end-to-end (production build, `__Host-` cookies): weak password rejected; sign-up asks to confirm e-mail;
unverified sign-in refused; verification link works and the token leaves the URL; access/refresh cookies are
`Secure`, `httpOnly`, `SameSite=Lax`; no token in `localStorage`, `sessionStorage` or `document.cookie`; BFF rejects
a POST without CSRF token (403) and does not expose token endpoints (404); invitation sent and accepted; seven
built-in roles shown; policy below baseline rejected; technician navigation hides admin screens, direct page shows
"no access" and direct API call gets `403 permission_denied`; TOTP enrolment with QR code and recovery codes; a role
change reaches the technician's open session immediately; deactivation signs them out on the next navigation;
audit log shows the trail; after sign-out the old access token is rejected by the API.

Defects found and fixed during verification: EF Core issued UPDATE instead of INSERT for children added to a
tracked parent (ids are now declared application-generated); `Retry-After` was missing on sliding-window
rejections; 403 bodies did not name the missing permission; a revoked-but-unexpired session caused a redirect loop
between `/login` and `/dashboard` (fixed with the server-verified `/api/auth/session-ended`); behind a reverse
proxy that route redirected to the container's bind address `0.0.0.0:3000` (now a relative redirect).

## Known limitations / next steps

* **Rate limits are per API instance** (in-memory). This is fine for the single-VPS deployment (one API
  container); scaling out to several API replicas would need a shared limiter (Redis) or limits at the gateway.
  Account lockout is database-backed and global either way.
* **Client address**: per-IP limits rely on `X-Forwarded-For`. In the VPS deployment Caddy overwrites it with
  the real address (tested with spoofed headers); exposing the web container without such a proxy would let a
  client choose its own address for rate limiting.
* **Platform administration** (operator console, tenant suspension endpoint, platform-level audit view, unlocking
  accounts by support) is not part of this phase; platform-level audit rows are queryable in the database only.
* **WebAuthn / passkeys and SMS** are not implemented; TOTP + recovery codes only. "Remember this device" is not offered.
* **Breached-password check** uses a built-in list of very common passwords, not a k-anonymity lookup (e.g. HIBP).
* **No SSO** (OIDC/SAML) or SCIM provisioning yet.
* **Audit retention/export** – the trail is append-only with no retention job or export endpoint.
* Frontend has no Playwright suite in the repository yet; the browser checks above were run from a script outside it.
