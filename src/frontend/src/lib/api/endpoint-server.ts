import "server-only";
import { serverApi } from "./server";
import type { BranchDto, DepartmentDto, PagedResult } from "./types";
import type { DeviceDto, DeviceGroupDto } from "./endpoint-types";
import type { TargetOptions } from "@/components/features/endpoints/common";

/** Everything a target picker needs: branches, departments, device groups and devices. */
export async function loadTargetOptions(): Promise<TargetOptions> {
  const [branches, departments, groups, devices] = await Promise.all([
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
    serverApi<PagedResult<DepartmentDto>>("departments?pageSize=200"),
    serverApi<DeviceGroupDto[]>("device-groups"),
    serverApi<PagedResult<DeviceDto>>("devices?pageSize=200"),
  ]);
  return {
    branches: branches.items.map((b) => ({ id: b.id, name: b.name })),
    departments: departments.items.map((d) => ({ id: d.id, name: `${d.branchName} / ${d.name}` })),
    groups: groups.map((g) => ({ id: g.id, name: g.name })),
    devices: devices.items.map((d) => ({ id: d.id, hostname: d.hostname })),
  };
}
