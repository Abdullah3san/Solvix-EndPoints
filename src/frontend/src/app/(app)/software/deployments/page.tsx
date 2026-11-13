import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { DeploymentDto, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { DeploymentsView } from "@/components/features/software/deployments-view";

export const metadata: Metadata = { title: "Software deployments" };

export default async function DeploymentsPage() {
  const deployments = await serverApi<PagedResult<DeploymentDto>>("software/deployments?pageSize=50");
  return (
    <>
      <PageHeader title="Software deployments" description="Install and remove software on managed devices. Every deployment is reviewed and approved before anything is sent." />
      <DeploymentsView initial={deployments.items} />
    </>
  );
}
