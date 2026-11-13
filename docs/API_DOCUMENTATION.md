# API Documentation

The authoritative, always-current contract is the generated OpenAPI document
(`/openapi/v1.json`, Scalar UI at `/scalar/v1` in non-production environments). This
file documents surfaces, conventions and the current endpoint inventory.

## 1. Surfaces

| Surface | Base | Host | Auth | Status |
|---------|------|------|------|--------|
| Management API | `/api/v1` | api (via BFF `/api/proxy/v1`) | Bearer user token | Implemented |
| BFF auth routes | `/api/auth/*` | web | form/JSON → sets httpOnly cookie | Implemented |
| Console real-time | `/hubs/console` | api | short-lived hub ticket from BFF | Phase 04 |
| Agent API | `/agent/v1` | gateway | device token (enrollment endpoints: enrollment token) | Phase 04 |
| Agent real-time | `/agent/v1/hub` | gateway | device token | Phase 04 |
| Integration API | `/api/v1` | public api host | API keys / client credentials | post-V1 |

## 2. Conventions

* Versioning in the URL segment (`/api/v1`). A version stays supported ≥ 12 months
  after its successor, announced with `Deprecation` and `Sunset` headers.
* kebab-case paths, camelCase JSON, enums as strings, ISO-8601 UTC timestamps.
* Errors: RFC 9457 `application/problem+json` with stable `code`, `traceId` and
  `correlationId`. Validation errors carry an `errors` map keyed by camelCase field.

  | Status | `code` examples |
  |--------|-----------------|
  | 400 | `validation_failed` |
  | 401 | `unauthorized` |
  | 403 | `forbidden`, `cross_tenant_access`, `tenant_inactive`, `step_up_required` *(Phase 02)* |
  | 404 | `not_found` (also returned for other tenants' ids) |
  | 409 | `conflict`, `duplicate` |
  | 429 | `rate_limited` *(Phase 02)* |
* Pagination: `page`/`pageSize` (max 100) returning `PagedResult<T>`
  {items, page, pageSize, totalCount} for admin lists. **[Phase 04]** Large collections
  (devices, events) use cursor pagination: `cursor`, `limit` → {items, nextCursor}.
* Filtering and sorting **[Phase 03/04]**: `filter` with a whitelisted field/operator
  grammar per resource (no dynamic SQL), `sort=field,-field`.
* **[Phase 02]** `If-Match` with ETag on updates of concurrency-sensitive resources;
  `Idempotency-Key` header required on command/deployment creation.
* Long-running or bulk operations return `202 Accepted` with a job resource.
* The tenant is never a request parameter. An optional `X-Tenant-Id` header is accepted
  only as a consistency check (mismatch ⇒ 403 `cross_tenant_access`).

## 3. Current endpoints (Phase 01)

All require an authenticated tenant member unless marked otherwise.

| Method | Path | Policy | Purpose |
|--------|------|--------|---------|
| POST | `/api/v1/tenant/register` | anonymous | Register a workspace (tenant, profile, HQ branch, owner) |
| GET | `/api/v1/tenant/slug-availability` | anonymous | Check slug availability |
| GET | `/api/v1/tenant` | member | Current tenant |
| GET | `/api/v1/tenant/overview` | member | Tenant overview counters |
| PUT | `/api/v1/tenant` | owner | Update tenant |
| GET | `/api/v1/tenant/members` | member | List members (paged) |
| POST | `/api/v1/tenant/members` | admin | Add member |
| PATCH | `/api/v1/tenant/members/{membershipId}/role` | admin | Change role |
| DELETE | `/api/v1/tenant/members/{membershipId}` | admin | Remove member |
| POST | `/api/v1/auth/login` | anonymous | Login (tenant-bound token) |
| POST | `/api/v1/auth/switch-tenant` | member | Token for another tenant of the caller |
| GET | `/api/v1/auth/me` | member | Caller and resolved tenant |
| GET/PUT | `/api/v1/company-profile` | member / admin | Company profile |
| GET/POST | `/api/v1/branches` | member / admin | List / create branches |
| GET/PUT/DELETE | `/api/v1/branches/{id}` | member / admin | Branch |
| POST | `/api/v1/branches/{id}/activate` · `/deactivate` | admin | Branch status |
| GET/POST | `/api/v1/departments` | member / admin | List / create departments |
| GET | `/api/v1/departments/tree/{branchId}` | member | Department tree |
| GET/PUT/DELETE | `/api/v1/departments/{id}` | member / admin | Department |
| POST | `/api/v1/departments/{id}/activate` · `/deactivate` | admin | Department status |
| GET | `/health/live`, `/health/ready` | anonymous | Health |

**[Phase 02]** Policies `member/admin/owner` are replaced by named permissions. The
endpoints keep their paths and behaviour for existing roles (Owner → Owner, Admin →
Administrator, Member → Viewer).

## 4. Planned management endpoints (Phase 02)

| Area | Endpoints (indicative) |
|------|------------------------|
| Sessions | `POST /auth/refresh`, `POST /auth/logout`, `GET/DELETE /auth/sessions[/{id}]` |
| MFA | `POST /auth/mfa/totp/enroll`, `/verify`, `POST /auth/mfa/challenge`, `POST /auth/step-up` |
| Tenants | `GET /auth/tenants` (switch list incl. partner-delegated) |
| Partner | `GET/POST /partner/customers`, `POST /partner/customers/{id}/revoke`, `GET/POST/DELETE /partner/grants` |
| Customer side | `GET /tenant/partner-link`, `POST /tenant/partner-link/codes`, `POST /tenant/partner-link/revoke` |
| Roles | `GET /roles`, `GET/PUT /tenant/members/{id}/assignments` |
| Audit | `GET /audit/events` (cursor, filters), `POST /audit/exports` |

## 5. Agent API (Phase 04, gateway)

| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| POST | `/agent/v1/enrollment/challenge` | none (rate-limited) | Nonce for enrollment |
| POST | `/agent/v1/enrollment` | enrollment token + key proof | Register device and public key |
| POST | `/agent/v1/auth/challenge` | none (rate-limited) | Nonce for device auth |
| POST | `/agent/v1/auth/token` | key signature | 15-min device token |
| GET | `/agent/v1/config` | device | Versioned agent configuration |
| GET | `/agent/v1/keys` | device | Signed command-signing key set |
| POST | `/agent/v1/inventory` | device | Full or delta inventory sections |
| POST | `/agent/v1/telemetry/metrics` | device | 5-min metric aggregates (batch) |
| POST | `/agent/v1/telemetry/security` | device | Posture + filtered security events |
| POST | `/agent/v1/commands/{executionId}/result` | device | Result and output upload reference |
| GET | `/agent/v1/updates/manifest` | device | Signed update manifest for the device's ring |
| WS | `/agent/v1/hub` | device | `Heartbeat`, `Ack`, `Result` (agent → server); `Execute`, `RefreshInventory`, `ConfigChanged`, `Disconnect` (server → agent) |

Message schemas are defined in `Solvix.Agent.Contracts`. The gateway accepts the current
and previous protocol versions.
