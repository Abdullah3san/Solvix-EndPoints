import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { DeviceConnectivity, DeviceGroupDto, DeviceStatsDto, DeviceStatus, DeviceSummaryDto, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { DevicesView } from "@/components/features/devices/devices-view";

export const metadata: Metadata = { title: "Devices" };

const CONNECTIVITY: DeviceConnectivity[] = ["Online", "Warning", "Offline"];
const STATUSES: DeviceStatus[] = ["Active", "Retired"];

export default async function DevicesPage({
  searchParams,
}: {
  searchParams: Promise<{ groupId?: string; connectivity?: string; status?: string }>;
}) {
  const { groupId: rawGroupId, connectivity: rawConnectivity, status: rawStatus } = await searchParams;
  const groupId = rawGroupId && /^[0-9a-f-]{36}$/i.test(rawGroupId) ? rawGroupId : "";
  const connectivity = CONNECTIVITY.find((c) => c === rawConnectivity) ?? "";
  const status = STATUSES.find((s) => s === rawStatus) ?? "";
  const query = new URLSearchParams({ pageSize: "25" });
  if (groupId) query.set("groupId", groupId);
  if (connectivity) query.set("connectivity", connectivity);
  if (status) query.set("status", status);
  const [devices, stats, groups] = await Promise.all([
    serverApi<PagedResult<DeviceSummaryDto>>(`devices?${query.toString()}`),
    serverApi<DeviceStatsDto>("devices/stats"),
    serverApi<PagedResult<DeviceGroupDto>>("device-groups?pageSize=200"),
  ]);
  return (
    <>
      <PageHeader title="Devices" description="Windows endpoints enrolled in this workspace and the inventory they report." />
      <DevicesView initial={devices} stats={stats} groups={groups.items} initialGroupId={groupId} initialConnectivity={connectivity} initialStatus={status} />
    </>
  );
}
