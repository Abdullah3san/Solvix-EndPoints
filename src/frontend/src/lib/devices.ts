import type { AgentHealthStatus, DeviceConnectivity, DeviceStatus, EnrollmentTokenState } from "@/lib/api/types";

type Tone = "neutral" | "brand" | "success" | "warning" | "danger";

/** 1536 -> "1.5 KB"; binary units, as Windows reports memory and disks. */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return "—";
  const units = ["B", "KB", "MB", "GB", "TB", "PB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value >= 10 || unit === 0 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`;
}

/** "just now", "5 min ago", "3 h ago", "2 d ago" (falls back to "—"). */
export function formatRelative(value: string | null | undefined, now: Date = new Date()): string {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  const seconds = Math.max(0, Math.round((now.getTime() - date.getTime()) / 1000));
  if (seconds < 45) return "just now";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} h ago`;
  return `${Math.round(hours / 24)} d ago`;
}

export function connectivityTone(status: DeviceStatus, connectivity: DeviceConnectivity): Tone {
  if (status === "Retired") return "neutral";
  return connectivity === "Online" ? "success" : "warning";
}

export function healthTone(health: AgentHealthStatus | null): Tone {
  switch (health) {
    case "Healthy":
      return "success";
    case "Degraded":
      return "warning";
    case "Unhealthy":
      return "danger";
    default:
      return "neutral";
  }
}

export function tokenStateTone(state: EnrollmentTokenState): Tone {
  return state === "Active" ? "success" : state === "Revoked" ? "danger" : "neutral";
}

export function deviceLabel(device: { displayName: string | null; hostname: string }): string {
  return device.displayName?.trim() ? device.displayName : device.hostname;
}
