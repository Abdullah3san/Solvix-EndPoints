# Phase 08 – Mobile device management (Android Enterprise, Apple MDM)

Module: `src/backend/Modules/Solvix.Modules.Mdm`. API prefix: `/api/v1/mobile-*`
(administrators) and `/api/v1/mdm/apple/*` (Apple devices).

## 1. What was built

| Area | Android Enterprise | Apple (iOS / iPadOS) |
|------|--------------------|----------------------|
| Management mechanism | Android Management API (AMAPI), Google hosts the device channel | Apple MDM protocol; Solvix **is** the MDM server (check-in + command endpoints, APNs wake-up) |
| Enrollment | Enterprise signup, single-use enrollment tokens (QR / token) | Enrollment profile (`.mobileconfig`) via single-use link; SCEP identity; User Enrollment (BYOD) |
| Modes | Fully managed, Dedicated (kiosk), Work profile (company-owned and BYOD) | Device Enrollment, Automated Device Enrollment (protocol surface only, see §8), User Enrollment |
| Inventory | AMAPI `devices.list` sync | `DeviceInformation` + `InstalledApplicationList` commands |
| Applications | Policy `applications` (force/available/blocked/kiosk) + managed configuration | `InstallApplication` / `RemoveApplication`, managed app configuration, allow/block lists on supervised devices |
| Wi-Fi | ONC `openNetworkConfiguration` (open, WPA-PSK, WPA-EAP/PEAP + CA) | `com.apple.wifi.managed` (+ `com.apple.security.root` for enterprise) |
| VPN | Always-on VPN app + lockdown (client force-installed) | `com.apple.vpn.managed` IKEv2 |
| Password policy | `passwordPolicies` (device or work-profile scope) | `com.apple.mobiledevice.passwordpolicy` |
| Compliance | Own evaluator + AMAPI verdict, posture, status reports | Own evaluator over reported inventory |
| Commands | Lock, reset passcode, reboot, retire, wipe | Lock, clear passcode, restart, erase, retire (remove MDM), install/remove app |

Architecture rules that were followed:

* **Separate providers.** `AndroidMdmProvider` and `AppleMdmProvider` implement a small shared
  contract (`IMdmProvider`: create enrollment, deploy policy, dispatch command). There is no
  shared protocol code, and nothing is shared with any desktop endpoint agent. (Phases 02–07 are not
  present in this repository; the module depends only on `Solvix.SharedKernel`, so no agent logic
  can leak into it.)
* **Official mechanisms only.** AMAPI for Android; the documented Apple MDM check-in/command
  protocol, configuration profile payloads and APNs for Apple. No device-admin APIs, no
  screen-scraping, no unsupported tricks.
* **Tenant isolation as in Phase 01.** All 8 new tables are tenant-owned: query filter, `SaveChanges`
  enforcement, composite tenant-safe foreign keys, and forced PostgreSQL RLS (migration
  `MobileDeviceManagementRls`). The anonymous Apple device endpoints resolve the tenant from the
  device handle under an explicit platform scope and then run **in a tenant-bound scope**, exactly
  like a background job.

```
                           ┌──────────── Solvix.Modules.Mdm ─────────────┐
 admin UI / API ──JWT──►   │ Policies  Apps  Enrollment  Devices  Commands │
                           │ Compliance (pure evaluator)  Sync worker      │
                           │            │                                  │
                           │     IMdmProviderRegistry                      │
                           │      │                  │                     │
                           │ AndroidMdmProvider  AppleMdmProvider          │
                           └──────┬──────────────────┬─────────────────────┘
                                  │ AMAPI (HTTPS)    │ APNs push
                                  ▼                  ▼
                            Google / device     iPhone ──signed plist──►  /api/v1/mdm/apple/{handle}/checkin|server
```

## 2. Management scenarios and ownership boundary

A *scenario* is `(platform, mode, ownership)`. Policies, enrollment tokens and devices each carry
one; a policy can only be assigned to a device of the **same** scenario, so a kiosk policy can never
reach a BYOD work profile. Only these combinations exist (anything else is rejected with a
`validation_failed` explaining why):

| Platform | Mode | Ownership |
|----------|------|-----------|
| Android | FullyManaged | CompanyOwned |
| Android | Dedicated (kiosk) | CompanyOwned |
| Android | WorkProfile | CompanyOwned **or** PersonallyOwned (BYOD) |
| iOS | AutomatedDeviceEnrollment | CompanyOwned |
| iOS | DeviceEnrollment | CompanyOwned |
| iOS | UserEnrollment | PersonallyOwned (BYOD) |

Notably, **a personally owned iPhone cannot be enrolled with full Device Enrollment** – that would
hand the organisation control of personal data. BYOD iOS is User Enrollment only.

### Personal vs corporate data (BYOD)

| Concern | What Solvix does |
|---------|------------------|
| Data separation | Android work profile / Apple managed apps and accounts. Policies for personal scenarios always block managed→personal sharing (Android `crossProfilePolicies`, iOS `allowOpenFromManagedToUnmanaged=false`, no managed contacts into unmanaged). The flag is forced on, it is not optional. |
| Identifiers | Serial numbers/IMEI are **not stored** for personally owned devices (`MobileDevice.ApplyInventory` discards them even if a provider reports them). The enrollment-time serial from an iOS `Authenticate` message is dropped too. |
| App inventory | Only managed apps are recorded on personal devices; unmanaged apps are discarded in the domain model. |
| Reporting | Android status reporting requests only application reports, device settings and software info; hardware, memory, network, display and power telemetry are switched off. |
| Removal | `Retire` removes management and corporate data only (selective wipe). A **full wipe is not offered** for any personally owned device – not hidden in the UI, rejected by the API (`422 unsupported_operation`). |
| Device-wide controls | Camera/screenshot/USB/factory-reset restrictions are applied only where the platform scopes them to the work profile, and are otherwise *skipped and reported* (see "skipped settings" below). |

### "Skipped settings" report

Publishing a policy (`POST /mobile-policies/{id}/publish`) returns `applied[]` and `skipped[]`:
every setting that was **not** applied because the scenario's ownership boundary or the platform
forbids it, with the reason. Example for iOS User Enrollment: *"camera disabled: device-wide
restrictions are not available on User Enrollment (personal device)"*. Administrators therefore
see exactly what a BYOD policy did not do instead of silently assuming it did.

## 3. Capability matrix (what each scenario can do)

Single source of truth: `MdmCapabilities`. Also served at `GET /api/v1/mobile-capabilities` and
echoed per device. ✔ = offered. "approval" = needs a second administrator.

| Command | Android fully managed | Android dedicated | Android work profile (company) | Android work profile (BYOD) | iOS ADE | iOS Device Enrollment | iOS User Enrollment |
|---|---|---|---|---|---|---|---|
| Sync inventory / apply policy | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Lock | ✔ | ✔ | ✔ | ✔ (work profile) | ✔ | ✔ | – |
| Reset/clear passcode | ✔ | ✔ | ✔ | – | ✔ | ✔ | – |
| Reboot | ✔ | ✔ | – | – | ✔ | – | – |
| Install/remove app (command) | policy-driven | policy-driven | policy-driven | policy-driven | ✔ | ✔ | ✔ (managed) |
| **Retire** (approval) | ✔ = factory reset | ✔ = factory reset | ✔ selective (relinquish ownership) | ✔ selective (delete work profile) | ✔ remove MDM | ✔ remove MDM | ✔ selective |
| **Wipe** (approval) | ✔ | ✔ | – | – | ✔ | ✔ | – |

### Platform-specific limitations (read before promising anything to a customer)

*Android*

* The matrix is deliberately conservative: an operation is offered only when its effect is
  unambiguous. Full wipe on a **company-owned work profile** device is not offered (use Retire,
  which relinquishes ownership and keeps the personal profile).
* On fully managed / dedicated devices there is no personal profile, so Retire and Wipe both end in
  a factory reset (deleting the device in AMAPI).
* Lock, passcode policy and reset on a **BYOD work profile** act on the work challenge, not the
  personal lock screen.
* `RESET_PASSWORD` is sent without a new password; what the OS then does depends on the Android
  version (see Google's documentation). Solvix never handles a new password.
* Reboot requires Android 7+ and fully managed mode.
* `Retire`/`Wipe` through `devices.delete` complete on Google's side synchronously; the device is
  reset when it next reaches Google. Solvix records the state change at dispatch.
* Kiosk: an Android Enterprise *dedicated* device is configured with `KIOSK` apps and
  `kioskCustomization`; kiosk apps are rejected for any other mode.
* Android VPN = a single always-on VPN **app**. Android has no built-in IKEv2 configuration through AMAPI.
* Detecting "rooted" uses Google's device posture (`AT_RISK`/`POTENTIALLY_COMPROMISED`); it requires Play Integrity-capable devices.

*Apple*

* **User Enrollment** blocks lock, passcode clearing, restart, erase, device-wide restrictions,
  hardware identifiers and the personal app list. Solvix refuses to queue these commands. VPN is
  per-app only on User Enrollment and is **not implemented**; a VPN in a User Enrollment policy is skipped and reported.
  Users need a Managed Apple ID (Apple Business/School Manager).
* Supervised-only settings (blocking/allow-listing apps, preventing erase) apply only to
  `AutomatedDeviceEnrollment`; for unsupervised enrollment they are skipped and reported.
* There is **no jailbreak indicator** in Apple MDM. The "block compromised devices" rule is recorded
  as *not evaluated* on iOS (a note, never a violation).
* `Retire` removes the MDM enrollment profile. A device enrolled with a non-removable profile
  (supervised) will refuse; use Wipe.
* Apple has no "Reboot" for unsupervised devices.
* Commands are *delivered when the device collects them*. APNs only wakes the device; if a device is
  offline the command stays queued until it expires (default 24 h) and is re-pushed every sync pass
  after 5 minutes.
* Enrollment profiles are served unsigned, so iOS shows "Not Signed". Serve them from a TLS endpoint
  you control and consider signing the profile at the reverse proxy.

## 4. Security model

| Topic | Design |
|-------|--------|
| Authorization | Read = any member. Create/update/publish/command/approve = Owner or Admin (`TenantAdmin`). Android Enterprise signup = Owner. |
| **Destructive operations** | `Retire` and `Wipe` are created `PendingApproval`. Nothing is sent to the platform until a **different** Owner/Admin approves (`POST /mobile-commands/{id}/approve`). Self-approval is rejected (`403`) unless `Solvix:Mdm:AllowSelfApproval=true` (off by default; for single-admin workspaces). A reason is mandatory; approval expires with the command (24 h); capability and provider configuration are re-checked at approval; only one pending destructive command per device. Rejection and cancellation are audited. |
| Audit trail | Append-only `mobile_device_events` (enrolled, inventory, compliance change, policy, command requested/approved/rejected/completed/failed, unenrolled) with actor ids. |
| Secrets in policies | Wi-Fi PSK / VPN shared secrets are **AES-256-GCM** encrypted (`enc:v1:`) with `Solvix:Mdm:SecretEncryptionKey`, write-only in the API (`passphraseSet: true`, never returned), decrypted only when a provider pushes the policy. Editing a policy keeps stored secrets if omitted. The key is **required outside Development** and validated at startup (fail-fast). |
| Apple device authentication | Every check-in/command message must carry `Mdm-Signature`: a detached CMS signature made with the device identity certificate (issued by your SCEP CA). The signature is verified, the certificate must chain to `Apple:IdentityCaCertificatePem`, and the first verified certificate is **bound** to the device – later messages from another certificate get `403`. Unsigned/tampered/untrusted → `401`. Production start-up fails if `AllowUntrustedDeviceIdentity` is on or no CA is configured. |
| Enrollment secrets | Apple links carry a 256-bit single-use, expiring secret; only its SHA-256 is stored. The link is in the query string (not the path) so request logging does not record it, but intermediaries may. Android tokens are single-use AMAPI tokens; the Solvix token id travels in `additionalData` so devices are matched exactly. QR codes/links are shown once at creation. |
| Plist parsing | XML property lists only, DTD ignored and never resolved (no XXE), size/depth limited. |
| Device handle | The routing handle is random (144 bits) and **not** a credential; authentication is the signature. |
| Isolation | See §1. Cross-tenant ids are `404`; tests cover API, enrollment links and device protocol. |

## 5. API overview

All admin endpoints are under `/api/v1`, JSON, camelCase, string enums, problem-details errors.

| Resource | Endpoints |
|----------|-----------|
| Capabilities | `GET mobile-capabilities` |
| Android Enterprise | `GET mobile-enterprise/{platform}`, `POST mobile-enterprise/android/signup`, `POST mobile-enterprise/android/complete` |
| Apps | `GET/POST mobile-apps`, `PUT/DELETE mobile-apps/{id}` |
| Policies | `GET/POST mobile-policies`, `GET/PUT/DELETE mobile-policies/{id}`, `PUT …/{id}/apps`, `POST …/{id}/publish` |
| Enrollment | `GET/POST mobile-enrollments`, `POST mobile-enrollments/{id}/revoke` |
| Devices | `GET mobile-devices`, `GET mobile-devices/{id}`, `PUT …/{id}/policy`, `POST …/{id}/evaluate-compliance`, `POST …/{id}/commands`, `POST mobile-devices/sync` |
| Commands | `GET mobile-commands`, `GET/…/{id}`, `POST …/{id}/approve|reject|cancel` |
| Compliance | `GET mobile-compliance/summary` |
| Apple device protocol (anonymous) | `GET mdm/apple/enroll?token=…`, `PUT mdm/apple/{handle}/checkin`, `PUT mdm/apple/{handle}/server` |

### Typical flows

**Android (fully managed)**
1. Owner: `POST mobile-enterprise/android/signup` → open the Google URL → `POST …/complete` with the returned enterprise token.
2. Admin: create apps → create a `FullyManaged/CompanyOwned` policy → `PUT …/apps` → `POST …/publish` (review `skipped`).
3. Admin: `POST mobile-enrollments` → show the QR code once → user scans it on a factory-reset device.
4. Solvix discovers the device on the next sync (`POST mobile-devices/sync` or the worker), matches the token, applies the policy, evaluates compliance.

**iOS (company device or BYOD)**
1. Create an iOS policy for the right scenario, publish, `POST mobile-enrollments` → send `enrollmentUrl`.
2. The user opens the link, installs the profile → device `Authenticate` / `TokenUpdate` → enrolled, pushed, and receives policy profile, apps and inventory queries.

**Retire a BYOD device**: `POST mobile-devices/{id}/commands {type: Retire, reason}` → pending →
another admin approves → work profile / managed data removed; personal data untouched; audit event says so.

## 5a. Web UI

Sidebar section **Mobile devices** (`src/frontend`, routes under `/mobile`):

| Page | What it does |
|------|--------------|
| `/mobile/devices` | Fleet compliance cards; filter by platform, ownership, compliance, state; search; "Sync now". |
| `/mobile/devices/{id}` | Inventory, compliance violations, what the device allows (and why not more), commands, apps, audit trail; re-evaluate, change policy, send command. Personal devices show a data-boundary notice and "not collected" for identifiers. |
| `/mobile/approvals` | Pending retire/wipe requests with approve/reject and a note. The server enforces "different administrator"; the UI only warns when you decided your own request. |
| `/mobile/policies` | Create/edit policies (passcode, restrictions, compliance, Wi-Fi, VPN, kiosk, app control), assign apps with managed configuration, publish and read the applied/skipped report. Secrets are write-only fields. |
| `/mobile/apps` | App catalogue. |
| `/mobile/enrollments` | Connect Android Enterprise (owner), create enrollments, show the QR code / link **once** (QR rendered in the browser), revoke. |

Only commands the device's capability set offers appear in the command dialog; destructive ones require a reason
and explain that nothing happens until approval. Members can read everything; mutating controls are hidden unless Owner/Admin
(the API enforces this independently). Tests: `src/frontend/src/__tests__/mobile.test.tsx`.

## 6. Configuration

Section `Solvix:Mdm` (environment variables `Solvix__Mdm__…`, see `.env.example`).

| Key | Purpose |
|-----|---------|
| `SecretEncryptionKey` | base64, 32 bytes. Required outside Development. |
| `PublicBaseUrl` | https base URL devices use (enrollment profiles). |
| `CommandTimeToLiveHours` | Queue/approval window (default 24). |
| `AllowSelfApproval` | Default `false`. |
| `Android:ProjectId`, `Android:ServiceAccountJson` | Google Cloud project with the Android Management API enabled + service account key. Provider is disabled until both are set. |
| `Apple:PushTopic`, `PushCertificatePath/Password` | APNs MDM push certificate. |
| `Apple:ScepUrl`, `ScepChallenge`, `IdentityCaCertificatePem` | Device identity issuance and its trust anchor. |
| `Sync:Enabled`, `IntervalMinutes` | Background inventory sync, command reconciliation/expiry, Apple re-push, compliance re-evaluation (per tenant, tenant-scoped). Off by default; `POST mobile-devices/sync` does the same on demand. |

An unconfigured provider answers `422 unsupported_operation` ("… not configured on this server") instead of failing obscurely.

## 7. Testing

| Suite | What it proves |
|-------|----------------|
| Unit (`tests/Solvix.UnitTests/Mdm`, 81 tests) | capability matrix and scenario validity; Android policy translation (password scope, BYOD boundary, kiosk gating, Wi-Fi/ONC, VPN, API-level mapping); Apple profile translation (User Enrollment filtering, supervised-only settings, enterprise Wi-Fi roots, constant policy identifier); plist round-trip + XXE/malformed input; CMS signature verification incl. CA chain; compliance rules; command/approval state machine; BYOD data minimisation in the domain model; secret encryption and tamper detection; options validation. |
| Integration (`MobileDeviceManagement*Tests`, 15 tests, real PostgreSQL with RLS + Redis) | **Enrollment**: Android token → discovered device, Apple link → `Authenticate`/`TokenUpdate`; single-use/expired/revoked tokens. **Unenrollment**: approved retire/wipe on every path, Apple `CheckOut`, device deleted outside Solvix, `410 Gone` after removal. **Policy deployment**: publish → provider receives translated policy; applied/skipped report; Apple device receives and acknowledges the profile + required apps; secrets never leave the API. **Compliance**: drift to non-compliant, violations, fleet summary, filters. **Approvals**: pending → self-approval denied → second admin approves; reject; cancel; duplicates. **Isolation**: cross-tenant 404s, member vs admin. **Device protocol security**: unsigned, garbage, tampered, wrong-identity, unknown handle. |
| Migration | `MigrationTests` (RLS forced on every tenant table, including the Phase 08 ones; rollback to 0 and reapply). |

Provider network calls (Google, APNs) are replaced by in-memory fakes in tests; devices are
simulated by a client that signs plists with its own certificate and speaks the real protocol
against the real API host.

```bash
dotnet test        # needs PostgreSQL (superuser) + Redis, see README
```

## 8. Not implemented / follow-ups (be honest with customers)

* **Not verified against live Google/Apple.** AMAPI request/field names and the Apple payload keys were
  written from the public documentation and covered by tests against fakes; they have **not** been
  exercised with a real Google Cloud project, an APNs MDM certificate or physical devices. Expect to
  adjust field names/enum values on first contact (the translators are isolated and unit-tested
  for exactly that reason). Run a pilot with one device per scenario before rollout.
* **Apple Business Manager / ADE sync** (assigning serials to the MDM server, DEP profile
  delivery, server-token handling) is not implemented. ADE devices use the same check-in protocol and
  `AutomatedDeviceEnrollment` policies once they reach the server.
* **SCEP CA** is external: Solvix points devices at `Apple:ScepUrl` and verifies against
  `Apple:IdentityCaCertificatePem`; it does not run a CA. **One platform-level APNs certificate**
  serves all tenants (per-tenant certificates are a follow-up).
* Android push notifications (Pub/Sub) are not consumed; state converges through the sync worker/on-demand sync.
  Per-app VPN (iOS), Apple declarative device management, VPP/Apps-and-Books licence assignment, app
  catalogue search against Google Play/App Store, certificate payloads beyond the Wi-Fi trust anchor, and
  Android `RESET_PASSWORD` with a new password are not implemented.
* No rate limiting on the anonymous Apple endpoints (add at the reverse proxy); Apple's protocol has no
  nonce, so rely on TLS (replayed signed messages are idempotent state reports, but terminate TLS properly).
* The web UI covers the administrator workflows below; it has no Android/iOS enrollment *client*
  and no bulk actions (multi-select commands, CSV export) yet.
