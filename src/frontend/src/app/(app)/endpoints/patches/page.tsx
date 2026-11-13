import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { PagedResult } from "@/lib/api/types";
import type { PatchComplianceReportDto, PatchDto, PatchFailureDto } from "@/lib/api/endpoint-types";
import { PageHeader } from "@/components/ui/page-header";
import { PatchesView } from "@/components/features/endpoints/patches-view";

export const metadata: Metadata = { title: "Patches" };

export default async function PatchesPage() {
  const [patches, compliance, failures] = await Promise.all([
    serverApi<PagedResult<PatchDto>>("patches?onlyNeeded=true&pageSize=100"),
    serverApi<PatchComplianceReportDto>("patches/compliance"),
    serverApi<PagedResult<PatchFailureDto>>("patches/failures"),
  ]);
  return (
    <>
      <PageHeader title="Patches" description="Missing updates, approvals, failures and compliance against approved patches." />
      <PatchesView initialPatches={patches} initialCompliance={compliance} initialFailures={failures} />
    </>
  );
}
