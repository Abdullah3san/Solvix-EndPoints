# Phase 05 – IT asset management: technical summary

A professional asset register that tracks every IT asset through its lifecycle — with or without
an installed agent — and relates physical assets to managed Windows endpoints through reviewed,
evidence-based associations. Built on Phase 03/04 (endpoint management and monitoring). Design
details: `docs/ARCHITECTURE.md`, section *IT asset management*.

## Scope delivered

### Asset categories

Desktop, Laptop, Server, Printer, Monitor, Mobile phone, Tablet, Zebra handheld, Network switch,
Router, Firewall, Access point, Storage device, Other IT equipment. Each has a tag code
(`DSK`, `LAP`, `SRV`, `PRN`, `MON`, `MOB`, `TAB`, `HHD`, `SWT`, `RTR`, `FWL`, `WAP`, `STO`, `OTH`),
a default useful life, and whether it can run the Windows agent (desktops, laptops, servers,
tablets). Agent-less equipment (switches, printers, monitors, …) is fully supported; linking to an
endpoint is simply not expected.

### Features

| # | Feature | Implementation |
|---|---------|----------------|
| 1 | Asset registration | `POST /assets` (optional initial assignment), *Register asset* form with "save and add another" |
| 2 | Automatic numbering | `{prefix}-{code}-{000001}` from an atomic per-tenant counter; configurable prefix; manual tags allowed and skipped by the generator |
| 3 | Categories | 14 categories above (`GET /assets/categories`) |
| 4 | Manufacturer and model | Stored as entered plus normalised keys used for duplicate detection and matching |
| 5 | Serial number tracking | Unique per tenant and manufacturer (API pre-check + DB unique index); placeholders ignored |
| 6 | Barcode generation | Code 128 SVG (`/assets/{id}/barcode.svg`) |
| 7 | QR code generation | QR SVG (`/assets/{id}/qr.svg`); printable label sheet for one or many assets |
| 8 | Asset assignment | Assign / re-assign / check in, expected return date, condition on return |
| 9 | Employee association | Workspace members or employees without a login (name + employee number); *My assets* for members |
| 10 | Department association | Department implies branch; validated within the tenant |
| 11 | Branch association | Branch, department and free-text location; changed through transfers |
| 12 | Purchase information | Vendor, date, cost, currency (tenant default), PO and invoice numbers |
| 13 | Warranty management | Start/end, provider, contract reference; Active / Expiring soon (configurable days) / Expired; filter and KPI |
| 14 | Vendor management | `/asset-vendors` CRUD, deactivate, delete only when unreferenced |
| 15 | Maintenance history | Scheduled → in progress → completed/cancelled with vendor, cost, resolution; asset status follows; timeline |
| 16 | Asset transfers | From/to branch, department, location with reason and actor; single and bulk |
| 17 | Physical asset audits | Scope by branch/department/category, scan tag / QR / serial (USB scanners work), unexpected assets, completion marks missing |
| 18 | Asset retirement | Reason required, closes the open assignment, reinstatement possible |
| 19 | Asset disposal | Method, date, proceeds, certificate reference; record becomes read-only |
| 20 | Asset depreciation | Straight-line and double-declining balance, monthly, salvage value, yearly schedule, book value |
| 21 | Asset reporting | Summary (value, status, category, branch, warranty, maintenance cost, audit coverage, endpoint coverage), depreciation report + CSV, audit log |

### Endpoint management integration

| Requirement | How |
|-------------|-----|
| Attempt to match on enrollment | `AgentEnrollmentService` publishes `Enrolled`/`ReEnrolled` after commit; `AgentService` publishes `HardwareChanged` when inventory changes serial/manufacturer/model; `AssetEndpointObserver` evaluates matches |
| Serial number and manufacturer/model as evidence | `MatchEvaluator`: serial required; manufacturer match/mismatch; model supporting (partial matches count); category sanity check; evidence stored per suggestion |
| No automatic merge of ambiguous matches | Ambiguity = several assets or endpoints share the serial, or either side is already linked; ambiguous pairs are never approved automatically; records are never merged |
| Administrator review and approval | *Endpoint matching* queue: evidence side by side, confidence, ambiguity warning, approve/reject with note; manual link/unlink from the asset; rejected pairs remembered |
| Separate records | `asset_device_links` relates `assets` and `devices`; deleting a device removes only the link; discrepancies are displayed, never synchronised |
| Assets without an agent | Every category works without a link; filter "No agent (switches, printers…)" |
| Both directions | Assets registered after enrollment are matched too; *rescan* re-evaluates everything; unregistered endpoints can be registered as assets (pre-filled, linked) |
| Optional automation | Tenant setting (off by default): auto-approve only High-confidence, unambiguous pairs; audited as `System` |

### UI (Next.js)

*Assets → Asset register*: KPI tiles (click to filter), search, advanced filters (category, status,
condition, branch, department, vendor, warranty, managed-endpoint state, assignment), sortable
columns, pagination with page size, row selection with bulk actions (transfer, check in, set
condition, mark missing, retire, print labels), CSV export of the current filter, shareable URLs.
*Asset details*: overview (asset, holder and location, purchase and warranty, managed endpoint with
connectivity and discrepancies, label preview) and tabs for assignment history, maintenance
timeline, transfers, depreciation schedule and audit history; lifecycle actions in dialogs.
Also *Endpoint matching*, *Physical audits* (live scanning session), *Maintenance*, *Vendors*,
*Reports*, *My assets* (all members), *Settings → Asset management*, printable label sheet, and a
*Physical asset* card on each device page.

## Security and data integrity

| Requirement | How |
|-------------|-----|
| Tenant isolation | All 11 tables tenant-owned: query filter, write interceptor, FK to `tenants`, forced RLS (covered by `MigrationTests`); tenant-safe composite FKs between asset tables and to branches, departments and devices |
| Authorization | Register and all management endpoints `TenantAdmin`; members only `GET /my-assets` (own assets, no costs); device certificates rejected; foreign ids → 404 |
| Database constraints | Unique tag, unique (manufacturer, serial), one open assignment, one maintenance in progress, one approved link per asset and per device, check constraints for amounts, dates, depreciation inputs, status invariants, tag format |
| Concurrency | Optimistic concurrency on assets (`xmin`; stale `version` → 409); unique-index races mapped to domain error codes |
| Audit logs | `asset_audit_log` for every change (field-level diffs, actor, correlation id), written in the same transaction; UPDATE/TRUNCATE rejected and DELETE only in platform scope by trigger |
| Safe outputs | CSV formula-injection protection; label SVGs XML-escaped, `nosniff`, sandboxing CSP |

## Database

Migrations `20261112224251_AssetManagement` (EF: 11 tables, `devices.serial_number_key` generated
column + index) and `20261112224300_AssetManagementSecurity` (RLS, cross-module FKs, append-only
trigger). Both roll back (`MigrationTests.Migrations_can_be_rolled_back_and_reapplied`).

## Verification

All suites run against real PostgreSQL 16 and Redis 7.

| Suite | Result |
|-------|--------|
| `Solvix.UnitTests` | **126 / 126** (64 new: identity normalisation, match evaluator, depreciation, lifecycle state machine, CSV escaping, barcode/QR decode round-trips) |
| `Solvix.IntegrationTests` | **111 / 111** (26 new) |
| `Solvix.Agent.Tests` | **71 / 71** (unchanged) |
| Frontend `npm run check` | typecheck + lint clean, **35 / 35** tests (13 new); `next build` succeeds |
| `dotnet build` | 0 warnings (warnings are errors) |

Requested test areas:

| Area | Tests |
|------|-------|
| Duplicate serial numbers | Case/whitespace/manufacturer-alias duplicates → 409; other manufacturer allowed; placeholders allowed; edit into duplicate → 409; other tenant independent; 6 racing requests → exactly one created; raw SQL → unique violation |
| Transfers | Branch/department/location history with names; department implies branch; mismatched department, no-op, unknown and other-tenant branch rejected; DB rejects cross-tenant branch FK; bulk transfer all-or-nothing |
| Asset matching | Enrollment → High suggestion, linked only after approval, both records kept; asset registered after enrollment; model mismatch → Medium; ambiguous serial (two assets / cloned endpoints) never auto-linked, approval supersedes rivals; auto-approval only when enabled and unambiguous; rejected pairs survive re-enrollment and rescan; placeholder serials never match; manual link conflicts and unlink/relink; inventory serial change withdraws and re-suggests; register endpoint as asset; device deletion keeps the asset |
| Unauthorized asset access | Anonymous 401 everywhere; members 403 on register/details/labels/bulk/settings, *my assets* only theirs without costs; device certificates rejected; tenant B gets 404 for A's assets, vendors, maintenance, audits, suggestions and devices (reads and 13 kinds of writes), cannot reference A's vendor/device or scan A's serial, never sees A in lists/stats/exports/audit log; raw-SQL RLS on asset tables; audit log append-only even for the schema owner |

Also covered: numbering (per category, per tenant, manual tags, custom prefix, 8 parallel
registrations), assignment history, maintenance/retirement/disposal lifecycle with a full audit
trail, optimistic concurrency, DB check constraints, depreciation API and report, register
search/filters/sort/paging/export, physical audit by tag/QR/serial with unexpected and missing
assets, label endpoints.

Manual end-to-end run in this environment: API + Next.js production build + seeded tenant with
14 assets and four real agent enrollments (EC P-256 CSRs over the agent API). In Chromium: register
with filters, selection and bulk toolbar; endpoint matching showed High, Medium (Lenovo
machine-type model) and ambiguous suggestions, and approval linked the asset; details tabs, audit
scanning by serial and tag, reports, labels (barcode and QR images loaded through the BFF), device
page asset card; a member could not see or open the register (404) and saw *My assets*. No console
errors and no horizontal overflow at 390 px width. This run found and fixed: inconsistent CSV line
endings, link-prefetch storms on data-heavy lists, non-wrapping page-header actions and a
breadcrumb 404 for `/settings`.

## Notes and follow-ups

* Asset tags are unique per tenant; tag numbers are not reused, gaps can occur when a registration fails after numbering.
* Depreciation uses calendar months from the purchase date; fiscal-year schedules and currency conversion are not implemented (amounts are reported per asset currency; report totals assume one currency).
* Warranty expiry is shown in the register, KPIs and reports; e-mail reminders belong with the notification phase.
* Matching considers Windows endpoints (the only managed platform so far); MAC-address evidence for network equipment could be added when agent-less discovery exists.
* Users are never hard-deleted by the platform; the holder FK (`NO ACTION`) would block such a deletion until assets are returned.
