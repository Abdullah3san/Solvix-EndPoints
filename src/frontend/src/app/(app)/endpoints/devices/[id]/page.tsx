import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import { ApiError } from "@/lib/api/errors";
import type { BranchDto, DepartmentDto, DeviceDetailsDto, DeviceGroupDto, DeviceInventoryDto, MemberDto, PagedResult } from "@/lib/api/types";
import { DeviceDetailsView } from "@/components/features/devices/device-details-view";

export const metadata: Metadata = { title: "Device" };

async function orNull<T>(promise: Promise<T>): Promise<T | null> {
  try {
    return await promise;
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) return null;
    throw e;
  }
}

export default async function DevicePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!/^[0-9a-f-]{36}$/i.test(id)) notFound();
  const device = await orNull(serverApi<DeviceDetailsDto>(`devices/${id}`));
  if (!device) notFound();

  const [inventory, groups, members, branches, departments] = await Promise.all([
    orNull(serverApi<DeviceInventoryDto>(`devices/${id}/inventory`)),
    serverApi<PagedResult<DeviceGroupDto>>("device-groups?pageSize=200"),
    serverApi<PagedResult<MemberDto>>("tenant/members?pageSize=200"),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
    serverApi<PagedResult<DepartmentDto>>("departments?pageSize=200"),
  ]);

  return (
    <DeviceDetailsView
      initial={device}
      inventory={inventory}
      groups={groups.items}
      members={members.items}
      branches={branches.items}
      departments={departments.items}
    />
  );
}
