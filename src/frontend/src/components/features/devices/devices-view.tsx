"use client";

import * as React from "react";
import Link from "next/link";
import { Eye, KeyRound, MonitorOff, MoreHorizontal, RefreshCw, SearchX, ShieldOff, Trash2, TriangleAlert } from "lucide-react";
import { api } from "@/lib/api/client";
import type { BranchDto, DeviceGroupDto, DeviceSortField, DeviceStatsDto, DeviceSummaryDto, PagedResult } from "@/lib/api/types";
import { activeFilterCount, emptyDeviceFilters, PAGE_SIZES, toApiQuery, type DeviceFilters, type DeviceListState, type DeviceStateFilter } from "@/lib/device-query";
import { connectivityTone, deviceLabel, formatRelative, healthTone } from "@/lib/devices";
import { messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Avatar } from "@/components/ui/avatar";
import { Badge, StatusBadge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import {
  DataTable,
  DataTableColumnHeader,
  DataTableViewOptions,
  FacetedFilter,
  ResetFiltersButton,
  type ColumnDef,
  type RowSelectionState,
  type SortingState,
  type VisibilityState,
} from "@/components/ui/data-table";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { EmptyState } from "@/components/ui/empty-state";
import { SearchInput } from "@/components/ui/input";
import { Tooltip } from "@/components/ui/tooltip";
import { useNotifications } from "@/components/providers/notifications-provider";
import { useCan } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";

const COLUMNS_STORAGE_KEY = "solvix.devices.columns.v1";
const DEFAULT_VISIBILITY: VisibilityState = { serialNumber: false, hardware: false, enrolledAt: false };
const SEARCH_DEBOUNCE_MS = 300;

const SORT_FIELDS: Record<string, DeviceSortField> = { name: "Name", os: "OperatingSystem", lastSeen: "LastSeen", enrolledAt: "EnrolledAt" };
const SORT_COLUMNS: Record<DeviceSortField, string> = { Name: "name", OperatingSystem: "os", LastSeen: "lastSeen", EnrolledAt: "enrolledAt" };

type BulkAction = "inventory" | "retire" | "delete";
type Pending = { action: BulkAction; devices: DeviceSummaryDto[] } | null;

const dash = <span className="text-muted-foreground">—</span>;

function readStoredVisibility(): VisibilityState {
  try {
    const raw = localStorage.getItem(COLUMNS_STORAGE_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : null;
    if (parsed && typeof parsed === "object") return { ...DEFAULT_VISIBILITY, ...(parsed as VisibilityState) };
  } catch {
    // unreadable preference: fall back to the defaults
  }
  return DEFAULT_VISIBILITY;
}

/** Runs one API call per device and reports how many succeeded (there is no bulk endpoint). */
export async function runForEach(devices: DeviceSummaryDto[], call: (device: DeviceSummaryDto) => Promise<unknown>) {
  const results = await Promise.allSettled(devices.map(call));
  const failed = results.filter((r): r is PromiseRejectedResult => r.status === "rejected");
  return { succeeded: results.length - failed.length, failed: failed.length, firstError: failed[0] ? messageFrom(failed[0].reason) : undefined };
}

export function DevicesView({
  initial,
  initialState,
  stats,
  groups,
  branches,
}: {
  initial: PagedResult<DeviceSummaryDto>;
  initialState: DeviceListState;
  stats: DeviceStatsDto;
  groups: DeviceGroupDto[];
  branches: BranchDto[];
}) {
  const { toast } = useToast();
  const { notify } = useNotifications();
  const { isAdmin } = useCan();

  const [data, setData] = React.useState(initial);
  const [counts, setCounts] = React.useState(stats);
  const [query, setQuery] = React.useState(initialState);
  const [searchInput, setSearchInput] = React.useState(initialState.filters.search);
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [selection, setSelection] = React.useState<RowSelectionState>({});
  const [visibility, setVisibility] = React.useState<VisibilityState>(DEFAULT_VISIBILITY);
  const [pending, setPending] = React.useState<Pending>(null);
  const requestId = React.useRef(0);
  const loadedQuery = React.useRef(initialState);

  // Column preferences are per browser; read after mount to keep SSR markup stable.
  React.useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setVisibility(readStoredVisibility());
  }, []);

  const changeVisibility = React.useCallback((updater: React.SetStateAction<VisibilityState>) => {
    setVisibility((current) => {
      const next = typeof updater === "function" ? updater(current) : updater;
      try {
        localStorage.setItem(COLUMNS_STORAGE_KEY, JSON.stringify(next));
      } catch {
        // storage unavailable: preference lasts for this visit
      }
      return next;
    });
  }, []);

  const load = React.useCallback(async (state: DeviceListState) => {
    const id = ++requestId.current;
    setLoading(true);
    try {
      const result = await api.get<PagedResult<DeviceSummaryDto>>("devices", toApiQuery(state));
      if (id !== requestId.current) return; // a newer request superseded this one
      // A deleted last row can leave the page past the end: step back once.
      if (result.items.length === 0 && result.totalCount > 0 && state.page > 1) {
        setQuery((q) => ({ ...q, page: Math.max(1, result.totalPages) }));
        return;
      }
      setData(result);
      setError(null);
    } catch (e) {
      if (id !== requestId.current) return;
      setError(messageFrom(e, "The device list could not be loaded."));
    } finally {
      if (id === requestId.current) setLoading(false);
    }
  }, []);

  React.useEffect(() => {
    if (loadedQuery.current === query) return; // already loaded (the server rendered the initial state)
    loadedQuery.current = query;
    void load(query);
  }, [query, load]);

  // Debounce typing into the search box before it becomes a query.
  React.useEffect(() => {
    if (searchInput === query.filters.search) return;
    const timer = window.setTimeout(() => {
      setSelection({});
      setQuery((q) => ({ ...q, page: 1, filters: { ...q.filters, search: searchInput } }));
    }, SEARCH_DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [searchInput, query.filters.search]);

  /** Selection only ever refers to rows on screen, so it is cleared whenever the result set changes. */
  function updateQuery(patch: Partial<Omit<DeviceListState, "filters">> & { filters?: Partial<DeviceFilters> }) {
    setSelection({});
    setQuery((q) => ({ ...q, page: 1, ...patch, filters: { ...q.filters, ...patch.filters } }));
  }

  function resetFilters() {
    setSearchInput("");
    updateQuery({ filters: emptyDeviceFilters });
  }

  async function refreshAfterChange() {
    setSelection({});
    await Promise.all([load(query), api.get<DeviceStatsDto>("devices/stats").then(setCounts).catch(() => undefined)]);
  }

  async function requestInventory(devices: DeviceSummaryDto[]) {
    const active = devices.filter((d) => d.status === "Active");
    if (active.length === 0) {
      toast({ tone: "info", title: "Nothing to refresh", description: "Retired devices no longer report inventory." });
      return;
    }
    const result = await runForEach(active, (d) => api.post(`devices/${d.id}/inventory-request`));
    if (result.failed > 0) {
      toast({ tone: "error", title: `Inventory request failed for ${result.failed} of ${active.length} devices`, description: result.firstError });
    } else {
      toast({
        tone: "success",
        title: active.length === 1 ? `Inventory requested from ${deviceLabel(active[0])}` : `Inventory requested from ${active.length} devices`,
        description: "Each agent sends a fresh inventory on its next check-in.",
      });
    }
    setSelection({});
  }

  async function confirmPending() {
    if (!pending) return;
    const { action, devices } = pending;
    const verb = action === "retire" ? "retired" : "deleted";
    const result = await runForEach(devices, (d) => (action === "retire" ? api.post(`devices/${d.id}/retire`) : api.delete(`devices/${d.id}`)));
    const subject = devices.length === 1 ? deviceLabel(devices[0]) : `${result.succeeded} devices`;
    if (result.succeeded > 0) {
      toast({ tone: "success", title: `${subject} ${verb}` });
      notify({ tone: "success", title: `${devices.length === 1 ? "Device" : `${result.succeeded} devices`} ${verb}`, description: devices.length === 1 ? deviceLabel(devices[0]) : undefined });
    }
    await refreshAfterChange();
    if (result.failed > 0) {
      toast({ tone: "error", title: `${result.failed} of ${devices.length} could not be ${verb}`, description: result.firstError });
      if (result.succeeded === 0) throw new Error("bulk action failed"); // keeps the dialog open for a retry
    }
  }

  const sorting: SortingState = [{ id: SORT_COLUMNS[query.sortBy], desc: query.sortDirection === "Desc" }];
  const onSortingChange = (updater: React.SetStateAction<SortingState>) => {
    const next = typeof updater === "function" ? updater(sorting) : updater;
    const sort = next[0];
    if (sort && SORT_FIELDS[sort.id]) updateQuery({ sortBy: SORT_FIELDS[sort.id], sortDirection: sort.desc ? "Desc" : "Asc" });
  };

  const columns = React.useMemo<ColumnDef<DeviceSummaryDto, unknown>[]>(
    () => [
      {
        id: "name",
        accessorFn: (d) => deviceLabel(d),
        enableHiding: false,
        meta: { label: "Device" },
        header: ({ column }) => <DataTableColumnHeader column={column} title="Device" />,
        cell: ({ row: { original: d } }) => (
          <div className="min-w-40">
            <Link href={`/endpoints/devices/${d.id}`} className="font-medium text-foreground underline-offset-2 hover:text-link hover:underline">
              {deviceLabel(d)}
            </Link>
            {d.displayName?.trim() ? <p className="font-mono text-xs text-muted-foreground">{d.hostname}</p> : null}
          </div>
        ),
      },
      {
        id: "assignedUser",
        enableSorting: false,
        meta: { label: "Assigned user" },
        header: "Assigned user",
        cell: ({ row: { original: d } }) =>
          d.assignedUserName ? (
            <span className="flex items-center gap-2 whitespace-nowrap">
              <Avatar name={d.assignedUserName} size="sm" /> {d.assignedUserName}
            </span>
          ) : d.assignedUserId ? (
            <span className="text-muted-foreground">Former member</span>
          ) : (
            <span className="text-muted-foreground">Unassigned</span>
          ),
      },
      {
        id: "os",
        accessorFn: (d) => d.osName,
        meta: { label: "Operating system" },
        header: ({ column }) => <DataTableColumnHeader column={column} title="Operating system" />,
        cell: ({ row: { original: d } }) =>
          d.osName ? (
            <span className="whitespace-nowrap">
              {d.osName}
              {d.osDisplayVersion ? <span className="ml-1.5 text-xs text-muted-foreground">{d.osDisplayVersion}</span> : null}
            </span>
          ) : (
            dash
          ),
      },
      {
        id: "ip",
        enableSorting: false,
        meta: { label: "IP address" },
        header: "IP address",
        cell: ({ row: { original: d } }) =>
          d.ipAddresses.length > 0 ? (
            <span className="whitespace-nowrap font-mono text-xs">
              {d.ipAddresses[0]}
              {d.ipAddresses.length > 1 ? (
                <Tooltip content={d.ipAddresses.slice(1).join(", ")}>
                  <span tabIndex={0} className="ml-1.5 cursor-default rounded font-sans text-muted-foreground">
                    +{d.ipAddresses.length - 1}
                  </span>
                </Tooltip>
              ) : null}
            </span>
          ) : (
            dash
          ),
      },
      {
        id: "branch",
        enableSorting: false,
        meta: { label: "Branch" },
        header: "Branch",
        cell: ({ row: { original: d } }) => (d.branchName ? <span className="whitespace-nowrap">{d.branchName}</span> : dash),
      },
      {
        id: "status",
        enableSorting: false,
        meta: { label: "Status" },
        header: "Status",
        cell: ({ row: { original: d } }) => (
          <div className="flex flex-nowrap items-center gap-1">
            <StatusBadge tone={connectivityTone(d.status, d.connectivity)} pulse={d.status === "Active" && d.connectivity === "Online"}>
              {d.status === "Retired" ? "Retired" : d.connectivity}
            </StatusBadge>
            {d.status === "Active" && d.healthStatus && d.healthStatus !== "Healthy" ? (
              <Tooltip content={`Agent health: ${d.healthStatus}`}>
                <Badge tone={healthTone(d.healthStatus)} tabIndex={0} className="px-1.5">
                  <TriangleAlert className="size-3" aria-hidden />
                  <span className="sr-only">Agent {d.healthStatus}</span>
                </Badge>
              </Tooltip>
            ) : null}
          </div>
        ),
      },
      {
        id: "lastSeen",
        accessorFn: (d) => d.lastSeenAtUtc,
        meta: { label: "Last check-in" },
        header: ({ column }) => <DataTableColumnHeader column={column} title="Last check-in" />,
        cell: ({ row: { original: d } }) =>
          d.lastSeenAtUtc ? (
            <time dateTime={d.lastSeenAtUtc} title={formatDate(d.lastSeenAtUtc)} className="whitespace-nowrap text-muted-foreground">
              {formatRelative(d.lastSeenAtUtc)}
            </time>
          ) : (
            <span className="text-muted-foreground">Never</span>
          ),
      },
      {
        id: "agent",
        enableSorting: false,
        meta: { label: "Agent version" },
        header: "Agent version",
        cell: ({ row: { original: d } }) => (
          <span className="flex items-center gap-1.5 whitespace-nowrap">
            <span className="font-mono text-xs">{d.agentVersion}</span>
            {d.agentUpdateAvailable ? <Badge tone="warning">Update</Badge> : null}
          </span>
        ),
      },
      {
        id: "serialNumber",
        enableSorting: false,
        meta: { label: "Serial number" },
        header: "Serial number",
        cell: ({ row: { original: d } }) => (d.serialNumber ? <span className="font-mono text-xs">{d.serialNumber}</span> : dash),
      },
      {
        id: "hardware",
        enableSorting: false,
        meta: { label: "Hardware" },
        header: "Hardware",
        cell: ({ row: { original: d } }) => <span className="whitespace-nowrap">{[d.manufacturer, d.model].filter(Boolean).join(" ") || dash}</span>,
      },
      {
        id: "enrolledAt",
        accessorFn: (d) => d.enrolledAtUtc,
        meta: { label: "Enrolled" },
        header: ({ column }) => <DataTableColumnHeader column={column} title="Enrolled" />,
        cell: ({ row: { original: d } }) => <span className="whitespace-nowrap text-muted-foreground">{formatDate(d.enrolledAtUtc)}</span>,
      },
      {
        id: "actions",
        enableSorting: false,
        enableHiding: false,
        meta: { label: "Actions", align: "right", className: "sticky right-0 w-12 bg-card [thead_&]:bg-[color-mix(in_srgb,var(--muted)_50%,var(--card))] [tr[data-state=selected]_&]:bg-[color-mix(in_srgb,var(--accent)_60%,var(--card))]" },
        header: () => <span className="sr-only">Actions</span>,
        cell: ({ row: { original: d } }) => (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon-sm" className="text-muted-foreground" aria-label={`Actions for ${deviceLabel(d)}`}>
                <MoreHorizontal />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent className="w-52">
              <DropdownMenuItem asChild>
                <Link href={`/endpoints/devices/${d.id}`}>
                  <Eye /> View details
                </Link>
              </DropdownMenuItem>
              {isAdmin ? (
                <>
                  <DropdownMenuItem disabled={d.status !== "Active"} onSelect={() => void requestInventory([d])}>
                    <RefreshCw /> Refresh inventory
                  </DropdownMenuItem>
                  <DropdownMenuSeparator />
                  <DropdownMenuItem tone="danger" disabled={d.status !== "Active"} onSelect={() => setPending({ action: "retire", devices: [d] })}>
                    <ShieldOff /> Retire device…
                  </DropdownMenuItem>
                  <DropdownMenuItem tone="danger" onSelect={() => setPending({ action: "delete", devices: [d] })}>
                    <Trash2 /> Delete record…
                  </DropdownMenuItem>
                </>
              ) : null}
            </DropdownMenuContent>
          </DropdownMenu>
        ),
      },
    ],
    // Row actions close over the current query through refs/state setters only.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [isAdmin],
  );

  const { filters } = query;
  const filtered = activeFilterCount(filters) > 0 || searchInput.trim().length > 0;
  const selectedDevices = (ids: string[]) => data.items.filter((d) => ids.includes(d.id));

  const stateOptions = [
    { value: "Online", label: "Online", count: counts.online },
    { value: "Offline", label: "Offline", count: counts.offline },
    { value: "Retired", label: "Retired", count: counts.retired },
  ];

  const pendingCount = pending?.devices.length ?? 0;
  const pendingName = pending && pendingCount === 1 ? deviceLabel(pending.devices[0]) : `${pendingCount} devices`;

  return (
    <>
      <DataTable
        label="Devices"
        columns={columns}
        data={data.items}
        getRowId={(d) => d.id}
        loading={loading}
        error={error}
        onRetry={() => void load(query)}
        sorting={sorting}
        onSortingChange={onSortingChange}
        enableSelection={isAdmin}
        rowSelection={selection}
        onRowSelectionChange={setSelection}
        columnVisibility={visibility}
        onColumnVisibilityChange={changeVisibility}
        toolbar={(table) => (
          <>
            <SearchInput
              value={searchInput}
              onValueChange={setSearchInput}
              placeholder="Search name, serial, model, IP or MAC…"
              aria-label="Search devices"
              className="w-full sm:w-72"
            />
            <FacetedFilter title="Status" options={stateOptions} value={filters.state ? [filters.state] : []} onChange={(v) => updateQuery({ filters: { state: (v[0] ?? "") as DeviceStateFilter } })} />
            <FacetedFilter
              title="Branch"
              options={branches.map((b) => ({ value: b.id, label: b.name }))}
              value={filters.branchId ? [filters.branchId] : []}
              onChange={(v) => updateQuery({ filters: { branchId: v[0] ?? "" } })}
              emptyText="No branches yet"
            />
            <FacetedFilter
              title="Group"
              options={groups.map((g) => ({ value: g.id, label: g.name, count: g.deviceCount }))}
              value={filters.groupId ? [filters.groupId] : []}
              onChange={(v) => updateQuery({ filters: { groupId: v[0] ?? "" } })}
              emptyText="No device groups yet"
            />
            <FacetedFilter
              title="Agent"
              options={counts.agentVersions.map((v) => ({ value: v.version, label: `v${v.version}${v.isLatest ? " (latest)" : ""}`, count: v.deviceCount }))}
              value={filters.agentVersion ? [filters.agentVersion] : []}
              onChange={(v) => updateQuery({ filters: { agentVersion: v[0] ?? "" } })}
              emptyText="No agent versions reported"
            />
            <Button
              variant="outline"
              size="sm"
              aria-pressed={filters.attention}
              onClick={() => updateQuery({ filters: { attention: !filters.attention } })}
              className={filters.attention ? "border-primary/40 bg-accent/60" : "border-dashed"}
            >
              <TriangleAlert /> Needs attention
              {counts.requiringAttention > 0 ? <span className="tabular-nums text-muted-foreground">{counts.requiringAttention}</span> : null}
            </Button>
            {filtered ? <ResetFiltersButton onClick={resetFilters} /> : null}
            <div className="ml-auto">
              <DataTableViewOptions table={table} onReset={() => changeVisibility(DEFAULT_VISIBILITY)} />
            </div>
          </>
        )}
        bulkActions={(ids) => {
          const devices = selectedDevices(ids);
          const active = devices.filter((d) => d.status === "Active");
          return (
            <>
              <Button variant="outline" size="sm" disabled={active.length === 0} onClick={() => void requestInventory(devices)}>
                <RefreshCw /> Refresh inventory
              </Button>
              <Button variant="outline" size="sm" disabled={active.length === 0} onClick={() => setPending({ action: "retire", devices: active })}>
                <ShieldOff /> Retire
              </Button>
              <Button variant="outline" size="sm" className="text-destructive" onClick={() => setPending({ action: "delete", devices })}>
                <Trash2 /> Delete
              </Button>
            </>
          );
        }}
        empty={
          filtered ? (
            <EmptyState icon={SearchX} title="No devices match these filters" description="Try a different search term or remove a filter." action={<Button variant="outline" size="sm" onClick={resetFilters}>Reset filters</Button>} />
          ) : (
            <EmptyState
              icon={MonitorOff}
              title="No devices enrolled yet"
              description="Create an enrollment token and install the agent on a Windows computer. It appears here as soon as it checks in."
              action={
                isAdmin ? (
                  <Button asChild size="sm">
                    <Link href="/endpoints/enrollment">
                      <KeyRound /> Enroll a device
                    </Link>
                  </Button>
                ) : undefined
              }
            />
          )
        }
        pagination={{
          page: data.page,
          pageSize: data.pageSize,
          totalCount: data.totalCount,
          itemLabel: "devices",
          pageSizeOptions: PAGE_SIZES,
          onPageChange: (page) => updateQuery({ page }),
          onPageSizeChange: (pageSize) => updateQuery({ pageSize }),
        }}
      />

      <ConfirmDialog
        open={pending !== null && pending.action !== "inventory"}
        onOpenChange={(open) => (open ? undefined : setPending(null))}
        title={pending?.action === "retire" ? `Retire ${pendingName}?` : `Delete ${pendingName}?`}
        description={
          pending?.action === "retire"
            ? "All device credentials are revoked immediately and the agent can no longer report. The record is kept for reference, and the machine cannot re-enroll until the record is deleted."
            : "The record, its inventory and its credentials are permanently deleted. This cannot be undone. The machine can enroll again as a new device."
        }
        confirmLabel={pending?.action === "retire" ? (pendingCount > 1 ? `Retire ${pendingCount} devices` : "Retire device") : pendingCount > 1 ? `Delete ${pendingCount} devices` : "Delete record"}
        confirmText={pending?.action === "delete" && pendingCount > 1 ? "delete" : undefined}
        onConfirm={confirmPending}
      >
        {pending && pendingCount > 1 ? (
          <ul className="scrollbar-thin max-h-32 overflow-y-auto rounded-md border border-border bg-muted/40 px-3 py-2 text-sm">
            {pending.devices.map((d) => (
              <li key={d.id} className="truncate py-0.5">
                {deviceLabel(d)}
              </li>
            ))}
          </ul>
        ) : null}
      </ConfirmDialog>
    </>
  );
}
