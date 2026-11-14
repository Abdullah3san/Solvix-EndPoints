import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import { adminOr404 } from "@/lib/api/require-admin";
import type { AssetSummaryReportDto, AuditLogEntryDto, DepreciationReportDto, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { ReportsView } from "@/components/features/assets/reports-view";

export const metadata: Metadata = { title: "Asset reports" };

export default async function ReportsPage() {
  const [summary, depreciation, auditLog] = await Promise.all([
    adminOr404(serverApi<AssetSummaryReportDto>("asset-reports/summary")),
    adminOr404(serverApi<DepreciationReportDto>("asset-reports/depreciation")),
    adminOr404(serverApi<PagedResult<AuditLogEntryDto>>("asset-reports/audit-log?pageSize=25")),
  ]);
  return (
    <>
      <PageHeader title="Asset reports" description="Inventory value, lifecycle, warranty, maintenance and managed-endpoint coverage." />
      <ReportsView summary={summary} depreciation={depreciation} auditLog={auditLog.items} />
    </>
  );
}
