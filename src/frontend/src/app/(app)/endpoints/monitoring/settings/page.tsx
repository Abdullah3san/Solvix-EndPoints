import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { MonitoringSettingsDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { MonitoringSettingsForm } from "@/components/features/monitoring/monitoring-settings-form";

export const metadata: Metadata = { title: "Monitoring settings" };

export default async function MonitoringSettingsPage() {
  const settings = await serverApi<MonitoringSettingsDto>("monitoring/settings");
  return (
    <>
      <PageHeader title="Monitoring settings" description="Thresholds, heartbeat interval and notifications for this workspace." />
      <MonitoringSettingsForm initial={settings} />
    </>
  );
}
