# Phase 04 – Real-time endpoint monitoring: technical summary

Administrators see accurate online / warning / offline state for every enrolled device, with
history, availability reports, automatic alerts and a live dashboard. Built on Phase 03
(Windows endpoint management). Design details: `docs/ARCHITECTURE.md`, section *Real-time
monitoring*.

## Scope delivered

| # | Feature | Implementation |
|---|---------|----------------|
| 1 | Device heartbeat system | Existing agent heartbeat + `PresenceTracker`, `PresenceBuffer`, `PresenceMonitor` |
| 2–4 | Online / Warning / Offline | `PresencePolicy.Evaluate`; persisted in `device_presence.state` |
| 5 | Last seen | `device_presence.last_seen_at_utc` (any authenticated agent request) |
| 6 | Last successful agent communication | `last_communication_at_utc` (2xx/3xx), `last_heartbeat_at_utc` |
| 7 | Device uptime | current connected period (`online_since_utc`), OS uptime from boot time, uptime totals in reports |
| 8 | Device downtime | `offline_since_utc` (last evidence of life), downtime totals in reports and alerts |
| 9 | Connection history | `device_connection_events`, `GET /monitoring/events` |
| 10 | Branch monitoring | dashboard by branch (counts + 7-day availability), branch filter / grouping in reports |
| 11 | Availability reports | `GET /monitoring/availability` (device or branch, ≤ 366 UTC days), CSV export, UI with daily chart |
| 12 | Automatic offline alerts | `monitoring_alerts`, opened on Offline, one open alert per device, acknowledge |
| 13 | Recovery notifications | alert resolved with downtime, pushed live, in-app notification + toast |
| 14 | Configurable intervals | per tenant: heartbeat interval, warning and offline thresholds, alerts/recovery toggles; platform: sweep, flush, grace, retention |
| 15 | Live dashboard updates | SignalR hub `/hubs/monitoring` via the BFF (`/api/realtime`, SSE), `Endpoints → Monitoring` |

Default thresholds: < 2 min online, 2–5 min warning, > 5 min offline (timeout must be exceeded).

### Three separate signals

| Signal | Evidence |
|--------|----------|
| A. Agent connectivity | authenticated agent contact only |
| B. Network reachability | agent probes on the device: adapter, gateway ping, DNS, locally configured TCP targets |
| C. OS health | available memory, system-drive free space, pending restart, boot time |

A heartbeat never upgrades B or C; when the agent is not online they are reported as stale /
Unknown. New, optional heartbeat fields (`systemBootedAtUtc`, `system`, `network`) keep older
agents compatible.

### Dashboard

Total, online, warning, offline devices; devices by branch and by operating system; availability
(7 and 30 days); network and OS signal counts; open alerts; recent connection events — all
updated live.

## Efficiency

* Routine heartbeat: **no synchronous database write** (verified by test). Contact is buffered and
  written in one `UPDATE … FROM unnest` per tenant every 5 s to a narrow, HOT-updatable table.
* The device row and stored snapshots are written only on change or every 5 minutes.
* Writes on transitions only: event, alert and daily availability rows in the same transaction.
* One indexed platform query per 15-s sweep regardless of tenant count; one sweeping node
  (advisory lock); live pushes only on transitions, one message per unit of work.

## False-offline protection

* Offline only when the timeout is **exceeded**, plus flush tolerance (never earlier).
* Platform outage/restart detection: silence counts only after a grace of min(outage, 15 min).
* Agent: retries every heartbeat interval at most while the server is unreachable (was up to
  15 min exponential backoff), so short network interruptions don't look like outages.
* Any agent call (not only heartbeats) proves life; recoveries found by the batched flush are
  applied immediately; the sweep self-heals races.
* Precision bug found by the tests and fixed: PostgreSQL microsecond timestamps made every
  heartbeat look like an agent restart (and a write).

## Database

Migrations `20261112215101_RealTimeMonitoring` (tables `device_presence`,
`device_connection_events`, `device_availability_days`, `monitoring_alerts`,
`monitoring_settings`, `monitor_checkpoints`; backfills presence from `devices.last_seen_at_utc`,
then drops that column) and `20261112215200_RealTimeMonitoringSecurity` (forced RLS on the five
tenant tables). Both roll back (`MigrationTests`).

## Verification

| Suite | Result |
|-------|--------|
| `Solvix.UnitTests` | **62 / 62** (18 new: thresholds, tolerance, evidence floor, signals, day split, settings, precision) |
| `Solvix.Agent.Tests` | **71 / 71** (3 new: retry cap, signals in heartbeat, boot-time stability) |
| `Solvix.IntegrationTests` | **85 / 85** (14 new monitoring scenarios, real PostgreSQL 16 + Redis 7) |
| Frontend `npm run check` | typecheck + lint clean, **22 / 22** tests; `next build` succeeds |
| `dotnet build` | 0 warnings |

Requested scenarios (`MonitoringTests`, simulated time against the real API):

| Scenario | Test |
|----------|------|
| Thresholds / no early offline | `Default_thresholds_online_below_2_min_warning_2_to_5_offline_after_5` (still Warning at exactly 300 s) |
| Delayed heartbeats | `Delayed_heartbeat_returns_to_online_without_offline_alert` |
| Network interruptions | `Short_network_interruption_is_recorded_as_reconnection_without_state_change`; agent retry-cap test |
| Agent restarts / reboots | `Agent_restart_and_device_reboot_are_history_not_outages` |
| Reconnections | `Offline_device_recovers_with_downtime_and_resolved_alert`, `Contact_through_any_agent_endpoint_counts_and_is_flushed_in_batches` |
| False offline (platform outage) | `Platform_outage_does_not_declare_devices_offline` |
| Write avoidance | `Routine_heartbeats_do_not_write_to_the_database_until_the_batched_flush` |
| Signals separation | `Network_and_os_health_are_separate_signals_from_agent_connectivity` |
| Settings, permissions | `Administrators_configure_thresholds_and_heartbeat_interval` |
| Dashboard, branches, OS, reports, CSV | `Dashboard_groups_by_branch_and_os_and_reports_availability` |
| Live updates + tenant isolation | `Live_updates_are_pushed_over_signalr_to_the_owning_tenant_only`, `Hub_rejects_anonymous_connections` |

Manual end-to-end run in this environment: API + Next.js production build + three simulated
agents; in Chromium the dashboard moved a silent device to Offline and back to Online **without
reloading** (through the SSE proxy), with offline/recovery toasts and no console errors; no
horizontal overflow at 390 px width.

### Not verified here

* Windows-only probe code (`GlobalMemoryStatusEx`, pending-reboot registry keys, ICMP as the
  service account) was compiled but not executed (no Windows machine); the portable probes ran on Linux.
* Multi-node operation (Redis backplane, sticky sessions) is configured and documented but was not
  run with more than one API node.
* No load test at 10k devices; the design keeps the per-heartbeat cost at one buffered entry.

## Notes and follow-ups

* Days in availability reports are UTC days; tenant time zones are not applied yet.
* Settings changes reach other API nodes within the 30-s settings cache.
* A device moved between branches keeps its history under its current branch in reports.
* Alerts are in-app (live + persisted); e-mail/webhook delivery is a later phase.
