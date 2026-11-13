import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { BranchDto, DeploymentGroupDto, DeviceGroupDto, PackageDto, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { NewDeploymentView } from "@/components/features/software/new-deployment-view";

export const metadata: Metadata = { title: "New deployment" };

export default async function NewDeploymentPage({ searchParams }: { searchParams: Promise<{ packageId?: string }> }) {
  const { packageId } = await searchParams;
  const [packages, groups, deviceGroups, branches] = await Promise.all([
    serverApi<PagedResult<PackageDto>>("software/packages?pageSize=100"),
    serverApi<DeploymentGroupDto[]>("software/deployment-groups"),
    serverApi<PagedResult<DeviceGroupDto>>("device-groups?pageSize=200"),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
  ]);
  return (
    <>
      <PageHeader title="New deployment" description="Choose a package and targets, review exactly what will happen, then submit it for approval." />
      <NewDeploymentView
        packages={packages.items.filter((p) => p.status === "Ready" && p.isConfigured)}
        groups={groups}
        deviceGroups={deviceGroups.items}
        branches={branches.items}
        initialPackageId={packageId}
      />
    </>
  );
}
