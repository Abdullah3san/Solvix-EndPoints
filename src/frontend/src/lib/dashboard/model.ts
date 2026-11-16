import type { DeviceStatsDto } from "@/lib/api/types";
import type { DemoDashboardData } from "@/lib/dev/demo-data";

/**
 * A dashboard figure is never guessed. It is either a value from a real data
 * source, explicitly unavailable (no source exists yet), or an error.
 */
export type Metric =
  | { status: "ok"; value: number; demo?: boolean }
  | { status: "unavailable"; reason: string }
  | { status: "error"; message: string };

export type Settled<T> = { ok: true; value: T } | { ok: false; message: string };

export type MetricKey =
  | "managedDevices"
  | "onlineDevices"
  | "offlineDevices"
  | "attentionDevices"
  | "mobileDevices"
  | "itAssets"
  | "criticalAlerts"
  | "pendingDeployments";

export type ActivityKind = "enrollment" | "alert" | "deployment" | "assignment" | "retirement" | "other";

export interface ActivityItem {
  id: string;
  kind: ActivityKind;
  title: string;
  detail?: string;
  actor?: string;
  occurredAtUtc: string;
}

export type ActivityFeed =
  | { status: "ok"; items: ActivityItem[]; demo?: boolean }
  | { status: "unavailable"; reason: string }
  | { status: "error"; message: string };

export interface DashboardOverview {
  /** True when any widget shows development demo data. */
  demo: boolean;
  metrics: Record<MetricKey, Metric>;
  /** Raw device statistics for the charts; null when they could not be loaded. */
  fleet: DeviceStatsDto | null;
  fleetError: string | null;
  activity: ActivityFeed;
  generatedAtUtc: string;
}

/** Why a widget has no data source yet. Shown verbatim in the UI. */
export const NOT_CONNECTED: Record<"mobileDevices" | "itAssets" | "criticalAlerts" | "pendingDeployments" | "activity", string> = {
  mobileDevices: "Mobile management not connected",
  itAssets: "Asset inventory not connected",
  criticalAlerts: "Security alerts not connected",
  pendingDeployments: "Deployments not connected",
  activity: "The activity feed isn't available yet. It will list enrollments, assignments, alerts and deployments as those modules come online.",
};

/**
 * Builds the overview from what the API returned. `demo` is only ever passed
 * in development (see lib/env.ts) and fills the widgets that have no backend.
 */
export function buildOverview(input: { stats: Settled<DeviceStatsDto>; demo: DemoDashboardData | null; now: Date }): DashboardOverview {
  const { stats, demo, now } = input;

  const fromStats = (pick: (s: DeviceStatsDto) => number | undefined, missing: string): Metric => {
    if (!stats.ok) return { status: "error", message: stats.message };
    const value = pick(stats.value);
    return typeof value === "number" ? { status: "ok", value } : { status: "unavailable", reason: missing };
  };
  const fromDemo = (value: number | undefined, reason: string): Metric =>
    value === undefined ? { status: "unavailable", reason } : { status: "ok", value, demo: true };

  return {
    demo: demo !== null,
    metrics: {
      managedDevices: fromStats((s) => s.active, "Not reported by the API"),
      onlineDevices: fromStats((s) => s.online, "Not reported by the API"),
      offlineDevices: fromStats((s) => s.offline, "Not reported by the API"),
      attentionDevices: fromStats((s) => s.requiringAttention, "Requires a newer API version"),
      mobileDevices: fromDemo(demo?.mobileDevices, NOT_CONNECTED.mobileDevices),
      itAssets: fromDemo(demo?.itAssets, NOT_CONNECTED.itAssets),
      criticalAlerts: fromDemo(demo?.criticalAlerts, NOT_CONNECTED.criticalAlerts),
      pendingDeployments: fromDemo(demo?.pendingDeployments, NOT_CONNECTED.pendingDeployments),
    },
    fleet: stats.ok ? stats.value : null,
    fleetError: stats.ok ? null : stats.message,
    activity: demo ? { status: "ok", items: demo.activity(now), demo: true } : { status: "unavailable", reason: NOT_CONNECTED.activity },
    generatedAtUtc: now.toISOString(),
  };
}
