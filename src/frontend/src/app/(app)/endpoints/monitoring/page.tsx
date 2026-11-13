import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { MonitoringAlertDto, MonitoringSummaryDto, PagedResult } from "@/lib/api/types";
import { MonitoringDashboard } from "@/components/features/monitoring/monitoring-dashboard";

export const metadata: Metadata = { title: "Monitoring" };

export default async function MonitoringPage() {
  const [summary, alerts] = await Promise.all([
    serverApi<MonitoringSummaryDto>("monitoring/summary"),
    serverApi<PagedResult<MonitoringAlertDto>>("monitoring/alerts?status=Open&pageSize=20"),
  ]);
  return <MonitoringDashboard initial={summary} initialAlerts={alerts.items} />;
}
