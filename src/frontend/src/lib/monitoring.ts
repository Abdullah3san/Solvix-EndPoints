import type {
  ConnectionEventDto,
  ConnectionEventType,
  DeviceConnectivity,
  MonitoringSummaryDto,
  MonitoringUpdateDto,
  NetworkStatus,
  OsHealthStatus,
  StateCountsDto,
} from "@/lib/api/types";

type Tone = "neutral" | "brand" | "success" | "warning" | "danger";

export const REALTIME_HUB_URL = "/api/realtime/hubs/monitoring";

export function stateTone(state: DeviceConnectivity): Tone {
  return state === "Online" ? "success" : state === "Warning" ? "warning" : "danger";
}

export function networkTone(status: NetworkStatus, stale = false): Tone {
  if (stale || status === "Unknown") return "neutral";
  return status === "Reachable" ? "success" : status === "Degraded" ? "warning" : "danger";
}

export function osTone(status: OsHealthStatus, stale = false): Tone {
  if (stale || status === "Unknown") return "neutral";
  return status === "Healthy" ? "success" : status === "Warning" ? "warning" : "danger";
}

/** 45 -> "45 s", 3720 -> "1 h 2 min", 266400 -> "3 d 2 h". */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds < 0) return "—";
  const s = Math.floor(seconds);
  if (s < 60) return `${s} s`;
  if (s < 3600) return `${Math.floor(s / 60)} min`;
  if (s < 86400) {
    const m = Math.floor((s % 3600) / 60);
    return m ? `${Math.floor(s / 3600)} h ${m} min` : `${Math.floor(s / 3600)} h`;
  }
  const h = Math.floor((s % 86400) / 3600);
  return h ? `${Math.floor(s / 86400)} d ${h} h` : `${Math.floor(s / 86400)} d`;
}

export function formatPercent(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return `${value >= 99.995 ? 100 : value.toFixed(2)}%`;
}

const EVENT_LABELS: Record<ConnectionEventType, string> = {
  Enrolled: "Enrolled",
  HeartbeatDelayed: "Heartbeat delayed",
  HeartbeatRestored: "Heartbeat restored",
  WentOffline: "Went offline",
  Recovered: "Recovered",
  Reconnected: "Reconnected",
  AgentRestarted: "Agent restarted",
  DeviceRebooted: "Device rebooted",
  NetworkStatusChanged: "Network changed",
  OsHealthChanged: "OS health changed",
};

export function eventLabel(type: ConnectionEventType): string {
  return EVENT_LABELS[type] ?? type;
}

export function eventTone(event: Pick<ConnectionEventDto, "type" | "toStatus">): Tone {
  switch (event.type) {
    case "WentOffline":
      return "danger";
    case "HeartbeatDelayed":
      return "warning";
    case "Recovered":
    case "HeartbeatRestored":
    case "Enrolled":
      return "success";
    case "NetworkStatusChanged":
    case "OsHealthChanged":
      return event.toStatus === "Reachable" || event.toStatus === "Healthy" ? "success" : "warning";
    default:
      return "neutral";
  }
}

function moveCount(counts: StateCountsDto, from: DeviceConnectivity, to: DeviceConnectivity): StateCountsDto {
  const key = (s: DeviceConnectivity) => (s === "Online" ? "online" : s === "Warning" ? "warning" : "offline");
  const next = { ...counts };
  next[key(from)] = Math.max(0, next[key(from)] - 1);
  next[key(to)] = next[key(to)] + 1;
  return next;
}

/**
 * Applies a live update to the dashboard snapshot so counters and the event
 * feed move instantly; the page still refetches the summary (debounced) for
 * the derived figures (availability, signals) that only the server computes.
 */
export function applyUpdate(summary: MonitoringSummaryDto, update: MonitoringUpdateDto, maxEvents = 15): MonitoringSummaryDto {
  let devices = summary.devices;
  let branches = summary.branches;
  for (const change of update.statusChanges) {
    devices = moveCount(devices, change.from, change.to);
    branches = branches.map((b) => (b.branchId === change.branchId ? { ...b, devices: moveCount(b.devices, change.from, change.to) } : b));
  }

  const known = new Set(summary.recentEvents.map((e) => e.id));
  const fresh = update.events.filter((e) => !known.has(e.id));
  const recentEvents = [...fresh, ...summary.recentEvents]
    .sort((a, b) => b.occurredAtUtc.localeCompare(a.occurredAtUtc))
    .slice(0, maxEvents);

  let openAlerts = summary.openAlerts;
  for (const alert of update.alerts) {
    openAlerts += alert.status === "Open" ? 1 : alert.resolvedAtUtc ? -1 : 0;
  }

  return { ...summary, devices, branches, recentEvents, openAlerts: Math.max(0, openAlerts) };
}
