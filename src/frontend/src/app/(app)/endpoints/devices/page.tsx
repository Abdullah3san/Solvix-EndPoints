import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { BranchDto, CurrentUserDto, PagedResult } from "@/lib/api/types";
import type { DeviceDto, DeviceGroupDto, EnrollmentTokenDto } from "@/lib/api/endpoint-types";
import { PageHeader } from "@/components/ui/page-header";
import { DevicesView } from "@/components/features/endpoints/devices-view";

export const metadata: Metadata = { title: "Devices" };

export default async function DevicesPage() {
  const me = await serverApi<CurrentUserDto>("auth/me");
  const isAdmin = me.tenant.role !== "Member";
  const [devices, groups, tokens, branches] = await Promise.all([
    serverApi<PagedResult<DeviceDto>>("devices?pageSize=100"),
    serverApi<DeviceGroupDto[]>("device-groups"),
    isAdmin ? serverApi<EnrollmentTokenDto[]>("enrollment-tokens") : Promise.resolve([]),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
  ]);
  return (
    <>
      <PageHeader title="Devices" description="Windows endpoints enrolled with the Solvix agent." />
      <DevicesView initialDevices={devices} initialGroups={groups} initialTokens={tokens} branches={branches.items.map((b) => ({ id: b.id, name: b.name }))} />
    </>
  );
}
