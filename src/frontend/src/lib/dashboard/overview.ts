import "server-only";
import { serverApi } from "@/lib/api/server";
import { settle } from "@/lib/api/settle";
import type { DeviceStatsDto } from "@/lib/api/types";
import { isDemoDataEnabled } from "@/lib/env";
import { buildOverview, type DashboardOverview } from "./model";

/** Loads the executive overview. A failing source degrades its own widgets instead of the whole page. */
export async function loadDashboardOverview(): Promise<DashboardOverview> {
  const stats = await settle(serverApi<DeviceStatsDto>("devices/stats"));
  // The demo module is only loaded in development with the explicit flag set.
  const demo = isDemoDataEnabled() ? (await import("@/lib/dev/demo-data")).demoDashboard : null;
  return buildOverview({ stats, demo, now: new Date() });
}
