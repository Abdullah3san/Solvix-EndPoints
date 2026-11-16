import "server-only";
import { unstable_rethrow } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import type { DeviceStatsDto } from "@/lib/api/types";
import { isDemoDataEnabled } from "@/lib/env";
import { messageFrom } from "@/lib/forms";
import { buildOverview, type DashboardOverview, type Settled } from "./model";

async function settle<T>(promise: Promise<T>): Promise<Settled<T>> {
  try {
    return { ok: true, value: await promise };
  } catch (error) {
    // Redirects (expired session) must keep propagating.
    unstable_rethrow(error);
    return { ok: false, message: messageFrom(error, "The request failed.") };
  }
}

/** Loads the executive overview. A failing source degrades its own widgets instead of the whole page. */
export async function loadDashboardOverview(): Promise<DashboardOverview> {
  const stats = await settle(serverApi<DeviceStatsDto>("devices/stats"));
  // The demo module is only loaded in development with the explicit flag set.
  const demo = isDemoDataEnabled() ? (await import("@/lib/dev/demo-data")).demoDashboard : null;
  return buildOverview({ stats, demo, now: new Date() });
}
