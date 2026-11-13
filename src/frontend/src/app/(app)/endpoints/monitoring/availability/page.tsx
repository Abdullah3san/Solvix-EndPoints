import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { AvailabilityReportDto, BranchDto, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { AvailabilityReport } from "@/components/features/monitoring/availability-report";

export const metadata: Metadata = { title: "Availability" };

export default async function AvailabilityPage() {
  const [report, branches] = await Promise.all([
    serverApi<AvailabilityReportDto>("monitoring/availability"),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
  ]);
  return (
    <>
      <PageHeader
        title="Device availability"
        description="Share of monitored time each device's agent was reachable. Warning time counts as available; downtime is time declared offline."
      />
      <AvailabilityReport initial={report} branches={branches.items} />
    </>
  );
}
