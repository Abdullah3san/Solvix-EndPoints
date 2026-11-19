import "server-only";
import { serverApi } from "@/lib/api/server";
import { settle, type Settled } from "@/lib/api/settle";
import type { DeviceAlertDto, DeviceOverviewDto, DeviceStatsDto, StagedComputerSummaryDto } from "@/lib/api/types";

export interface AgentOverviewData {
  stats: Settled<DeviceStatsDto>;
  overview: Settled<DeviceOverviewDto>;
  alerts: Settled<DeviceAlertDto[]>;
  staged: Settled<StagedComputerSummaryDto>;
  generatedAtUtc: string;
}

/** Loads the agent overview. Each source is independent: a failing one only affects its own widgets. */
export async function loadAgentOverview(): Promise<AgentOverviewData> {
  const [stats, overview, alerts, staged] = await Promise.all([
    settle(serverApi<DeviceStatsDto>("devices/stats")),
    settle(serverApi<DeviceOverviewDto>("devices/overview")),
    settle(serverApi<DeviceAlertDto[]>("devices/alerts")),
    settle(serverApi<StagedComputerSummaryDto>("staged-computers/summary")),
  ]);
  return { stats, overview, alerts, staged, generatedAtUtc: new Date().toISOString() };
}
