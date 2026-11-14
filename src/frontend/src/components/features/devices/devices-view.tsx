"use client";

import * as React from "react";
import Link from "next/link";
import { ArrowUpCircle, CircleCheck, CircleSlash, Monitor, WifiOff } from "lucide-react";
import { api } from "@/lib/api/client";
import type { DeviceConnectivity, DeviceGroupDto, DeviceStatsDto, DeviceStatus, DeviceSummaryDto, PagedResult } from "@/lib/api/types";
import { connectivityTone, deviceLabel, formatRelative, healthTone } from "@/lib/devices";
import { messageFrom } from "@/lib/forms";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input, Select } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";

interface Filters {
  search: string;
  connectivity: DeviceConnectivity | "";
  status: DeviceStatus | "";
  groupId: string;
}

export function DevicesView({
  initial,
  stats,
  groups,
  initialGroupId = "",
}: {
  initial: PagedResult<DeviceSummaryDto>;
  stats: DeviceStatsDto;
  groups: DeviceGroupDto[];
  initialGroupId?: string;
}) {
  const { toast } = useToast();
  const [data, setData] = React.useState(initial);
  const [filters, setFilters] = React.useState<Filters>({ search: "", connectivity: "", status: "", groupId: initialGroupId });
  const [loading, setLoading] = React.useState(false);

  const reload = React.useCallback(
    async (next: Filters, page = 1) => {
      setLoading(true);
      try {
        setData(
          await api.get<PagedResult<DeviceSummaryDto>>("devices", {
            search: next.search,
            connectivity: next.connectivity,
            status: next.status,
            groupId: next.groupId,
            page,
            pageSize: data.pageSize,
          }),
        );
      } catch (e) {
        toast({ tone: "error", title: "Could not load devices", description: messageFrom(e) });
      } finally {
        setLoading(false);
      }
    },
    [data.pageSize, toast],
  );

  function update(patch: Partial<Filters>) {
    const next = { ...filters, ...patch };
    setFilters(next);
    void reload(next);
  }

  const tiles = [
    { label: "Enrolled", value: stats.active, icon: Monitor, onClick: () => update({ connectivity: "", status: "Active" }) },
    { label: "Online", value: stats.online, icon: CircleCheck, onClick: () => update({ connectivity: "Online", status: "" }) },
    { label: "Offline", value: stats.offline, icon: WifiOff, onClick: () => update({ connectivity: "Offline", status: "" }) },
    { label: "Agent update available", value: stats.updateAvailable, icon: ArrowUpCircle, onClick: undefined },
    { label: "Retired", value: stats.retired, icon: CircleSlash, onClick: () => update({ connectivity: "", status: "Retired" }) },
  ];

  return (
    <>
      <div className="mb-6 grid grid-cols-2 gap-3 lg:grid-cols-5">
        {tiles.map((tile) => (
          <Card key={tile.label} className={tile.onClick ? "cursor-pointer transition-colors hover:border-primary/50" : undefined} onClick={tile.onClick}>
            <CardContent className="flex items-center gap-3 p-4">
              <tile.icon className="h-5 w-5 text-muted-foreground" aria-hidden />
              <div>
                <p className="text-xl font-semibold leading-none">{tile.value}</p>
                <p className="mt-1 text-xs text-muted-foreground">{tile.label}</p>
              </div>
            </CardContent>
          </Card>
        ))}
      </div>

      <form
        className="mb-4 flex flex-col gap-2 sm:flex-row sm:items-center"
        onSubmit={(e) => {
          e.preventDefault();
          void reload(filters);
        }}
      >
        <Input
          placeholder="Search hostname, serial, model, IP or MAC…"
          value={filters.search}
          onChange={(e) => setFilters((f) => ({ ...f, search: e.target.value }))}
          className="sm:max-w-sm"
          aria-label="Search devices"
        />
        <Select aria-label="Connectivity" value={filters.connectivity} onChange={(e) => update({ connectivity: e.target.value as Filters["connectivity"] })} className="sm:w-44">
          <option value="">Any connectivity</option>
          <option value="Online">Online</option>
          <option value="Offline">Offline</option>
        </Select>
        <Select aria-label="Status" value={filters.status} onChange={(e) => update({ status: e.target.value as Filters["status"] })} className="sm:w-32">
          <option value="">Any status</option>
          <option value="Active">Active</option>
          <option value="Retired">Retired</option>
        </Select>
        <Select aria-label="Group" value={filters.groupId} onChange={(e) => update({ groupId: e.target.value })} className="sm:w-44">
          <option value="">All groups</option>
          {groups.map((g) => (
            <option key={g.id} value={g.id}>
              {g.name}
            </option>
          ))}
        </Select>
        <Button type="submit" variant="outline" loading={loading}>
          Search
        </Button>
      </form>

      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Device</TH>
              <TH>Status</TH>
              <TH>Operating system</TH>
              <TH>Hardware</TH>
              <TH>IP address</TH>
              <TH>Agent</TH>
              <TH>Last seen</TH>
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={7}>No devices match. Create an enrollment token to add Windows devices.</EmptyRow>
            ) : (
              data.items.map((d) => (
                <TR key={d.id}>
                  <TD className="font-medium">
                    <Link href={`/endpoints/devices/${d.id}`} className="hover:underline">
                      {deviceLabel(d)}
                    </Link>
                    {d.displayName ? <p className="text-xs font-normal text-muted-foreground">{d.hostname}</p> : null}
                  </TD>
                  <TD>
                    <div className="flex flex-wrap gap-1">
                      <Badge tone={connectivityTone(d.status, d.connectivity)}>{d.status === "Retired" ? "Retired" : d.connectivity}</Badge>
                      {d.healthStatus && d.healthStatus !== "Healthy" ? <Badge tone={healthTone(d.healthStatus)}>{d.healthStatus}</Badge> : null}
                    </div>
                  </TD>
                  <TD className="text-muted-foreground">
                    {d.osName ?? "—"}
                    {d.osDisplayVersion ? <span className="ml-1 text-xs">({d.osDisplayVersion})</span> : null}
                  </TD>
                  <TD className="text-muted-foreground">{[d.manufacturer, d.model].filter(Boolean).join(" ") || "—"}</TD>
                  <TD className="font-mono text-xs">{d.ipAddresses[0] ?? "—"}</TD>
                  <TD>
                    <span className="font-mono text-xs">{d.agentVersion}</span>
                    {d.agentUpdateAvailable ? (
                      <Badge tone="warning" className="ml-1">
                        update
                      </Badge>
                    ) : null}
                  </TD>
                  <TD className="text-muted-foreground" title={d.lastSeenAtUtc ?? undefined}>
                    {formatRelative(d.lastSeenAtUtc)}
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
        {data.totalPages > 1 ? (
          <div className="flex items-center justify-between border-t border-border px-3 py-2 text-sm text-muted-foreground">
            <span>
              Page {data.page} of {data.totalPages} · {data.totalCount} devices
            </span>
            <div className="flex gap-1">
              <Button variant="outline" size="sm" disabled={data.page <= 1} onClick={() => reload(filters, data.page - 1)}>
                Previous
              </Button>
              <Button variant="outline" size="sm" disabled={data.page >= data.totalPages} onClick={() => reload(filters, data.page + 1)}>
                Next
              </Button>
            </div>
          </div>
        ) : null}
      </Card>
    </>
  );
}
