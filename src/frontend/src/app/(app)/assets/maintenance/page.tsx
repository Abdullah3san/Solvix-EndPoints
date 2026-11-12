import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import { adminOr404 } from "@/lib/api/require-admin";
import type { MaintenanceRecordDto, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { MaintenanceView } from "@/components/features/assets/maintenance-view";

export const metadata: Metadata = { title: "Maintenance" };

export default async function MaintenancePage() {
  const records = await adminOr404(serverApi<PagedResult<MaintenanceRecordDto>>("asset-maintenance?status=InProgress&pageSize=50"));
  return (
    <>
      <PageHeader title="Maintenance" description="Repairs, upgrades and inspections across all assets. Each asset's page shows its full maintenance timeline." />
      <MaintenanceView initial={records} />
    </>
  );
}
