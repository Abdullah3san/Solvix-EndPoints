import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { DeviceGroupDto, DeviceStatsDto, DeviceSummaryDto, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { DevicesView } from "@/components/features/devices/devices-view";

export const metadata: Metadata = { title: "Devices" };

export default async function DevicesPage({ searchParams }: { searchParams: Promise<{ groupId?: string }> }) {
  const { groupId: rawGroupId } = await searchParams;
  const groupId = rawGroupId && /^[0-9a-f-]{36}$/i.test(rawGroupId) ? rawGroupId : "";
  const [devices, stats, groups] = await Promise.all([
    serverApi<PagedResult<DeviceSummaryDto>>(`devices?pageSize=25${groupId ? `&groupId=${groupId}` : ""}`),
    serverApi<DeviceStatsDto>("devices/stats"),
    serverApi<PagedResult<DeviceGroupDto>>("device-groups?pageSize=200"),
  ]);
  return (
    <>
      <PageHeader title="Devices" description="Windows endpoints enrolled in this workspace and the inventory they report." />
      <DevicesView initial={devices} stats={stats} groups={groups.items} initialGroupId={groupId} />
    </>
  );
}
