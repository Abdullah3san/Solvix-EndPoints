import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { MaintenanceWindowDto } from "@/lib/api/endpoint-types";
import { PageHeader } from "@/components/ui/page-header";
import { MaintenanceWindowsView } from "@/components/features/endpoints/maintenance-windows-view";

export const metadata: Metadata = { title: "Maintenance windows" };

export default async function MaintenanceWindowsPage() {
  const windows = await serverApi<MaintenanceWindowDto[]>("maintenance-windows");
  return (
    <>
      <PageHeader title="Maintenance windows" description="When installations, restarts and enforced configuration changes may happen." />
      <MaintenanceWindowsView initial={windows} />
    </>
  );
}
