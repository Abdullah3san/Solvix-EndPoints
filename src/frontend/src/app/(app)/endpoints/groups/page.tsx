import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { DeviceGroupDto, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { DeviceGroupsView } from "@/components/features/devices/device-groups-view";

export const metadata: Metadata = { title: "Device groups" };

export default async function DeviceGroupsPage() {
  const groups = await serverApi<PagedResult<DeviceGroupDto>>("device-groups?pageSize=200");
  return (
    <>
      <PageHeader title="Device groups" description="Organise devices into static groups. Enrollment tokens can place new devices in a group automatically." />
      <DeviceGroupsView initial={groups.items} />
    </>
  );
}
