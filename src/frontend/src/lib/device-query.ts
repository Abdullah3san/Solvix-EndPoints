import type { AgentHealthStatus, DeviceSortField, DeviceStatus, DeviceConnectivity, SortDirection } from "@/lib/api/types";

/** "State" facet in the device table: connectivity for active devices, plus retired. */
export type DeviceStateFilter = "" | "Online" | "Offline" | "Retired";

export interface DeviceFilters {
  search: string;
  state: DeviceStateFilter;
  branchId: string;
  groupId: string;
  agentVersion: string;
  attention: boolean;
  /** Agent health, e.g. from an alert's "View all" link. */
  health: AgentHealthStatus | "";
  /** Active devices that have not checked in for at least this many hours (0 = off). */
  notSeenHours: number;
  /** Only devices running an agent older than the latest release. */
  updateAvailable: boolean;
}

export const emptyDeviceFilters: DeviceFilters = {
  search: "",
  state: "",
  branchId: "",
  groupId: "",
  agentVersion: "",
  attention: false,
  health: "",
  notSeenHours: 0,
  updateAvailable: false,
};

export interface DeviceListState {
  filters: DeviceFilters;
  sortBy: DeviceSortField;
  sortDirection: SortDirection;
  page: number;
  pageSize: number;
}

export const DEFAULT_PAGE_SIZE = 25;
export const PAGE_SIZES = [10, 25, 50, 100];

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const first = (value: string | string[] | undefined) => (Array.isArray(value) ? value[0] : value) ?? "";

export function activeFilterCount(filters: DeviceFilters): number {
  return [filters.search.trim(), filters.state, filters.branchId, filters.groupId, filters.agentVersion, filters.attention, filters.health, filters.notSeenHours, filters.updateAvailable].filter(Boolean).length;
}

/** Query-string parameters for `GET /devices`. Empty values are dropped by the API client. */
export function toApiQuery(state: DeviceListState): Record<string, string | number | boolean | undefined> {
  const { filters } = state;
  let status: DeviceStatus | undefined;
  let connectivity: DeviceConnectivity | undefined;
  if (filters.state === "Retired") status = "Retired";
  else if (filters.state) connectivity = filters.state;
  return {
    search: filters.search.trim() || undefined,
    status,
    connectivity,
    branchId: filters.branchId || undefined,
    groupId: filters.groupId || undefined,
    agentVersion: filters.agentVersion || undefined,
    attention: filters.attention ? true : undefined,
    health: filters.health || undefined,
    notSeenHours: filters.notSeenHours > 0 ? filters.notSeenHours : undefined,
    updateAvailable: filters.updateAvailable ? true : undefined,
    sortBy: state.sortBy,
    sortDirection: state.sortDirection,
    page: state.page,
    pageSize: state.pageSize,
  };
}

export function toQueryString(state: DeviceListState): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(toApiQuery(state))) {
    if (value !== undefined && value !== "") params.set(key, String(value));
  }
  return params.toString();
}

/**
 * Reads deep-link filters from the page URL (untrusted input: everything is
 * validated). Supports the links used by the dashboard, global search and
 * device groups: ?search=, ?status=, ?connectivity=, ?attention=true, ?groupId=, ?branchId=.
 */
export function filtersFromSearchParams(params: Record<string, string | string[] | undefined>): DeviceFilters {
  const connectivity = first(params.connectivity);
  const status = first(params.status);
  const state: DeviceStateFilter = status === "Retired" ? "Retired" : connectivity === "Online" || connectivity === "Offline" ? connectivity : "";
  const id = (value: string) => (UUID.test(value) ? value : "");
  return {
    search: first(params.search).slice(0, 100),
    state,
    branchId: id(first(params.branchId)),
    groupId: id(first(params.groupId)),
    agentVersion: /^[\w.+-]{1,40}$/.test(first(params.agentVersion)) ? first(params.agentVersion) : "",
    attention: first(params.attention) === "true",
    health: ["Healthy", "Degraded", "Unhealthy"].includes(first(params.health)) ? (first(params.health) as AgentHealthStatus) : "",
    notSeenHours: /^\d{1,5}$/.test(first(params.notSeenHours)) ? Math.min(Number(first(params.notSeenHours)), 24 * 365) : 0,
    updateAvailable: first(params.updateAvailable) === "true",
  };
}
