import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import { loadTargetOptions } from "@/lib/api/endpoint-server";
import type { PagedResult } from "@/lib/api/types";
import type { DeploymentDto, MaintenanceWindowDto, PatchDto } from "@/lib/api/endpoint-types";
import { PageHeader } from "@/components/ui/page-header";
import { DeploymentsView } from "@/components/features/endpoints/deployments-view";

export const metadata: Metadata = { title: "Deployments" };

export default async function DeploymentsPage() {
  const [deployments, patches, windows, targets] = await Promise.all([
    serverApi<PagedResult<DeploymentDto>>("patch-deployments?pageSize=50"),
    serverApi<PagedResult<PatchDto>>("patches?approvalState=Approved&pageSize=200"),
    serverApi<MaintenanceWindowDto[]>("maintenance-windows"),
    loadTargetOptions(),
  ]);
  return (
    <>
      <PageHeader title="Deployments" description="Pilot-first rollouts of approved patches inside maintenance windows." />
      <DeploymentsView initial={deployments} approvedPatches={patches.items} windows={windows} targets={targets} />
    </>
  );
}
