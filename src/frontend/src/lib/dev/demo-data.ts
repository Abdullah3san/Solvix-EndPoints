/**
 * DEMO DATA — DEVELOPMENT ONLY.
 *
 * Placeholder figures for dashboard widgets whose backend modules (mobile
 * device management, IT asset inventory, security alerts, deployments, the
 * activity feed) do not exist yet. Nothing here is real.
 *
 * It is only ever read through `isDemoDataEnabled()` (lib/env.ts), which
 * requires `next dev` AND NEXT_PUBLIC_SOLVIX_DEMO_DATA=true, and every widget
 * that shows it is labelled "Demo". Never import this file from a component.
 */
import type { ActivityItem } from "@/lib/dashboard/model";

export interface DemoDashboardData {
  mobileDevices: number;
  itAssets: number;
  criticalAlerts: number;
  pendingDeployments: number;
  activity: (now: Date) => ActivityItem[];
}

const minutesAgo = (now: Date, minutes: number) => new Date(now.getTime() - minutes * 60_000).toISOString();

export const demoDashboard: DemoDashboardData = {
  mobileDevices: 38,
  itAssets: 412,
  criticalAlerts: 3,
  pendingDeployments: 5,
  activity: (now) => [
    { id: "demo-1", kind: "enrollment", title: "FIN-LT-0142 enrolled", detail: "Enrollment token “Finance laptops”", actor: "System", occurredAtUtc: minutesAgo(now, 4) },
    { id: "demo-2", kind: "alert", title: "Disk encryption disabled on HR-PC-0031", detail: "Critical security alert opened", actor: "Policy engine", occurredAtUtc: minutesAgo(now, 26) },
    { id: "demo-3", kind: "deployment", title: "Agent 1.4.2 rollout started", detail: "Ring 1 · 24 devices", actor: "Demo Admin", occurredAtUtc: minutesAgo(now, 71) },
    { id: "demo-4", kind: "assignment", title: "OPS-WS-0007 assigned to Riyadh HQ", detail: "Branch changed", actor: "Demo Admin", occurredAtUtc: minutesAgo(now, 188) },
    { id: "demo-5", kind: "retirement", title: "LAB-PC-0002 retired", detail: "Credentials revoked", actor: "Demo Admin", occurredAtUtc: minutesAgo(now, 1460) },
  ],
};
