import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { BranchDto, DeviceGroupDto, DeviceStatsDto, DeviceSummaryDto, PagedResult } from "@/lib/api/types";
import { DEFAULT_PAGE_SIZE, filtersFromSearchParams, toQueryString, type DeviceListState } from "@/lib/device-query";
import { PageHeader } from "@/components/ui/page-header";
import { DevicesView } from "@/components/features/devices/devices-view";

export const metadata: Metadata = { title: "Devices" };

export default async function DevicesPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const initialState: DeviceListState = {
    filters: filtersFromSearchParams(await searchParams),
    sortBy: "Name",
    sortDirection: "Asc",
    page: 1,
    pageSize: DEFAULT_PAGE_SIZE,
  };
  const [devices, stats, groups, branches] = await Promise.all([
    serverApi<PagedResult<DeviceSummaryDto>>(`devices?${toQueryString(initialState)}`),
    serverApi<DeviceStatsDto>("devices/stats"),
    serverApi<PagedResult<DeviceGroupDto>>("device-groups?pageSize=200"),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
  ]);
  return (
    <>
      <PageHeader title="Devices" description="Windows endpoints enrolled in this workspace and the inventory they report." />
      <DevicesView initial={devices} initialState={initialState} stats={stats} groups={groups.items} branches={branches.items} />
    </>
  );
}
