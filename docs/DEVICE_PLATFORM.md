# Device Platform: Agent, Gateway, Heartbeat and Monitoring

**Status:** Proposed for Phase 04+. Security details are in [SECURITY.md](SECURITY.md) §5–§9,
data model in [DATABASE.md](DATABASE.md) §6.

## 1. Scope and targets

| Item | Target |
|------|--------|
| Devices | 1,000 at launch, **10,000** without redesign; beyond that, scale out (§7) |
| Online detection | device shown offline ≤ 3 min after it stops responding |
| Command latency (online device) | p95 < 5 s from approval to agent acknowledgement |
| Agent footprint | idle CPU < 1%, memory < 80 MB, no inbound ports |
| Platforms (V1) | Windows 10 22H2, Windows 11, Server 2016+, x64 (ARM64 after V1) |

## 2. Components

```
┌──────────────── Windows endpoint ────────────────┐       ┌──────────── platform ─────────────┐
│ Solvix Agent service (LocalSystem, .NET 10)       │  WSS  │ gateway (ASP.NET Core)             │
│  ├ Identity (TPM key, device token)               │◄─────►│  ├ /agent/v1 REST (enroll, auth,   │
│  ├ Channel (SignalR WS + HTTPS fallback, backoff) │ HTTPS │  │   inventory, telemetry, results) │
│  ├ Scheduler (heartbeat, inventory, metrics,      │──────►│  ├ /agent/v1/hub (SignalR)          │
│  │   WUA scan, posture)                           │       │  ├ presence → Redis                 │
│  ├ Collectors (WMI/CIM, registry, WUA, EventLog)  │       │  ├ telemetry batch writer → PG      │
│  ├ Command engine (verify → handler registry)     │       │  └ remote tunnel endpoint (Ph. 09)  │
│  ├ Local store (SQLite: queue, nonces, sequence)  │       │ worker                              │
│  └ Tunnel (remote desktop, Phase 09)              │       │  ├ offline detector, flush, rollups │
│ Solvix Agent Updater service (separate)           │       │  ├ dynamic groups, alerts           │
└───────────────────────────────────────────────────┘       │  └ Hangfire: retention, partitions │
                                                            └─────────────────────────────────────┘
```

* The updater is a separate service so that a broken agent can always be replaced or
  rolled back.
* The local SQLite store is in `%ProgramData%\Solvix\` with an ACL for SYSTEM and
  Administrators only. It holds the outbound queue (results and telemetry while offline,
  capped at 50 MB, oldest metrics dropped first), the nonce cache and the last executed
  sequence.
* Configuration (server URL, intervals, collection switches) is delivered by the server
  and versioned. The agent applies it only from authenticated responses.

## 3. Communication model

| Flow | Transport | Frequency | Notes |
|------|-----------|-----------|-------|
| Auth token | HTTPS | every ~12 min (before 15 min expiry) | challenge/response with TPM key |
| Presence + heartbeat | SignalR WS (MessagePack) | keep-alive 15 s; heartbeat message 60 s | heartbeat carries uptime, agent health, pending reboot, inventory section hashes, config version |
| Command push | SignalR WS | on demand | signed envelope; agent acks immediately |
| Command result | WS (small) / HTTPS (large output) | on completion | bound to executionId |
| Inventory | HTTPS | full at enrollment and weekly; delta check daily; on demand | only sections whose hash changed are uploaded |
| Metrics | HTTPS | batch every 5 min (5-min aggregates from 30 s samples) | |
| Security events / posture | HTTPS | batch every 5 min / posture every 6 h | filtered event IDs only |
| Windows Update scan | HTTPS | daily (jittered) + on demand | WUA `IUpdateSearcher` |
| Fallback (WS blocked) | HTTPS long-poll | 60 s | commands then arrive ≤ 60 s late |

**Jitter everywhere:** every schedule is offset by a per-device random value derived from
the device id, so 10,000 devices never fire together.

**Reconnect policy:** exponential backoff 1 s → 5 min cap, with full jitter. The agent
reuses a still-valid token, so a gateway restart does not trigger 10k token requests at
once. The gateway applies a connection-accept rate limit (e.g. 200/s) and returns
`Retry-After`.

## 4. Heartbeat and presence architecture (ADR-008)

### 4.1 Load at 10,000 devices

| Operation | Rate | Where |
|-----------|------|-------|
| WebSocket connections | 10,000 concurrent | gateway memory ~0.5–1 GB |
| Heartbeat messages | ~167/s | gateway → Redis (pipelined) |
| Redis presence writes | ~170/s | trivial for Redis |
| PostgreSQL presence writes | transitions only (~1–5/s typical) + 1 batch flush per 5 min | negligible |
| Metric uploads | ~33 req/s, ~33 rows/s | batch insert |

### 4.2 Data flow

```
agent ─heartbeat─► gateway
                     ├─ Redis: HSET presence:{deviceId} {tid, node, lastSeen, agentVer, ...}  (TTL 10 min)
                     ├─ Redis: ZADD presence:lastseen {now} {tid}:{deviceId}
                     └─ if device was offline/unknown → publish PresenceChanged(online)

on connect    → same as heartbeat + record connection {deviceId → gateway node} for routing
on disconnect → mark "disconnected at"; device stays online until the 3-min grace expires
                (absorbs brief network flaps and gateway restarts)

worker: OfflineDetector (every 30 s, single active instance via Redis lock)
   ZRANGEBYSCORE presence:lastseen -inf (now-180s)  → candidates not already offline
   → for each tenant: write presence_transitions + update devices.presence (one batch)
   → publish PresenceChanged(offline) → SignalR to console users of that tenant, alert rules

worker: LastSeenFlusher (every 5 min)
   read lastSeen for online devices from Redis → per tenant one UPDATE … FROM unnest(...)
```

* **Tenant-safe batch writes:** batches are grouped by tenant and executed inside
  `ITenantJobRunner` scopes, so RLS applies even to platform background work.
* **Redis loss:** presence is reconstructed within one heartbeat interval. Devices are
  shown as `unknown` rather than offline until then, which prevents false offline alerts.
  Redis runs with AOF (`everysec`).
* **UI:** device lists read presence from the `devices` table (eventually consistent ≤ 5
  min for `last_seen_at`; immediate for transitions). Device detail reads live presence
  from Redis.

### 4.3 Telemetry pipeline

```
HTTPS POST /agent/v1/telemetry/* ─► validate & cap ─► bounded Channel<T> (per type)
   ─► batch writer (flush at 1,000 rows or 2 s) ─► COPY/INSERT into partitioned tables
   ─► 202 Accepted only after enqueue; channel full ⇒ 503 + Retry-After (agent keeps data queued)
```
* Metrics are tolerant to rare loss (a crash between enqueue and flush loses ≤ 2 s).
  Inventory, command results and security events are written **synchronously**
  (low volume, must not be lost).
* `ITelemetrySink` abstraction: if one gateway's in-process batching becomes a bottleneck
  or multiple gateways are deployed, the sink is swapped for Redis Streams consumed by
  the worker, without changing agents or the schema.
* Rollups: the worker aggregates `metrics_5m` → `metrics_1h` hourly; partitions expire per
  retention.

### 4.4 Alerts (Phase 08; offline alert in Phase 04)
Alert evaluation is event-driven in the worker: presence transitions, metric batches
(threshold rules evaluated on insert batches) and security events. Alerts are
deduplicated per (rule, device) with a cooldown. Notification fan-out (in-app, e-mail,
webhook) goes through the outbox.

## 5. Windows data collection (V1 scope, PR-6)

| Area | Source | Notes |
|------|--------|-------|
| Hardware | CIM: `Win32_ComputerSystem`, `Win32_BIOS`, `Win32_Processor`, `Win32_PhysicalMemory`, `Win32_DiskDrive`, `MSFT_PhysicalDisk`, `Win32_NetworkAdapterConfiguration`, `Win32_Tpm`, `Win32_EncryptableVolume` | promoted fields in DATABASE §6 |
| Software | Registry Uninstall keys (HKLM 64/32-bit; HKU per loaded profile if enabled), AppX packages (optional) | normalized server-side per tenant |
| OS | `Win32_OperatingSystem`, `Win32_QuickFixEngineering`, build/UBR from registry | |
| Windows Update | WUA COM API search `IsInstalled=0 and IsHidden=0`, plus history | records update service (WU/WSUS/WUfB); visibility only in V1 |
| Security posture | `MSFT_MpComputerStatus` (Defender), firewall profiles, BitLocker, UAC, local Administrators group | |
| Security events | Event log subscription for IDs 4625 (aggregated), 4720, 4732, 7045, 1102, Defender 1116 | minimized |
| Metrics | Performance counters: CPU, memory, system-disk free, network | 30 s samples → 5-min aggregates |

## 6. Agent build, packaging and release

* .NET 10 Worker Service, self-contained, single-file, trimmed where safe; WiX MSI with
  silent parameters (`SERVER`, `TOKEN`, optional `PROXY`) for GPO, Intune or existing
  RMM deployment.
* CI (GitHub Actions, Windows runner): build → unit tests → collectors' integration tests
  on Windows → MSI build → **signing job** (protected environment, cloud signing service
  via OIDC) → manifest generation and KMS signing → publish to object storage → ring
  rollout controlled from the platform (SECURITY §9).
* Agent ↔ gateway message contracts live in a shared `Solvix.Agent.Contracts` assembly
  with versioned DTOs. The gateway supports the current and previous agent protocol
  version.
* A **simulated agent** (Linux-runnable console app using the same contracts) is used for
  load tests: 10,000 virtual devices against a staging stack before Phase 04 exits.

## 7. Scaling beyond one node (designed now, deployed when needed)

| Concern | V1 single node | Scale-out (no code rewrite) |
|---------|----------------|-----------------------------|
| Command routing to the right gateway | in-process | SignalR **Redis backplane**; agents join group `device:{id}` |
| Offline detector, flusher, rollups | Redis lock | same lock, any worker instance |
| Hangfire | 1 server | multiple servers, same storage |
| Telemetry | in-process channel | Redis Streams sink |
| Sticky sessions | n/a | required only for long-polling fallback (Caddy cookie/IP hash) |
| Database | same VPS | dedicated host or managed PostgreSQL; read replica for reporting |

## 8. Phase 04 acceptance criteria (proposed)

1. An MSI installed with a valid token enrolls the device in the correct customer tenant.
   Invalid, expired, revoked or exhausted tokens are rejected and audited, and a partner
   tenant cannot create tokens.
2. The device key is TPM-backed where a TPM 2.0 exists, and `key_protection` is
   reported correctly.
3. A device authenticates with challenge/response. A retired device is disconnected
   within 30 s and cannot obtain a new token.
4. Online/offline state is correct within 3 minutes; brief disconnects (< 3 min) produce
   no transition.
5. Basic hardware, OS and software inventory is visible in the device list and detail
   views; deltas upload only changed sections.
6. **Cross-tenant tests:** a device token of tenant A cannot read or write anything of
   tenant B; console users of tenant A cannot see tenant B's devices, including through
   partner access without a grant.
7. 10,000 simulated devices are sustained on the staging VPS for 1 hour: gateway CPU
   < 50%, PostgreSQL write rate within the §4.1 budget, no false offline transitions.
   A gateway restart recovers all connections within 10 minutes without token-issuance
   errors.
8. The MSI and binaries are signed (test certificate acceptable until OD-2 is resolved)
   and the updater rejects a manifest or binary with an invalid signature.
