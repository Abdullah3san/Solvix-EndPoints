"use client";

import * as React from "react";
import Link from "next/link";
import { RefreshCw } from "lucide-react";
import { api } from "@/lib/api/client";
import type { ComplianceState, ComplianceSummaryDto, DeviceDto, DeviceOwnership, DeviceState, MobilePlatform, SyncResultDto } from "@/lib/api/mdm-types";
import type { PagedResult } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { complianceLabel, deviceTitle, formatDate, modeLabel, ownershipLabel, platformLabel, stateLabel } from "@/lib/mdm";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input, Select } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { ComplianceBadge, OwnershipBadge, PlatformIcon, StateBadge } from "./badges";

interface Filters {
  platform: "" | MobilePlatform;
  state: "" | DeviceState;
  compliance: "" | ComplianceState;
  ownership: "" | DeviceOwnership;
  search: string;
  page: number;
}

const initialFilters: Filters = { platform: "", state: "", compliance: "", ownership: "", search: "", page: 1 };

export function SummaryCards({ summary }: { summary: ComplianceSummaryDto }) {
  const cards = [
    { label: "Managed devices", value: summary.total, href: "/mobile/devices" },
    { label: "Compliant", value: summary.compliant, tone: "text-success" },
    { label: "Non-compliant", value: summary.nonCompliant, tone: summary.nonCompliant > 0 ? "text-destructive" : undefined },
    { label: "Not evaluated", value: summary.unknown },
    { label: "Awaiting approval", value: summary.pendingApprovals, href: "/mobile/approvals", tone: summary.pendingApprovals > 0 ? "text-warning" : undefined },
  ];
  return (
    <div className="mb-6 grid grid-cols-2 gap-3 lg:grid-cols-5">
      {cards.map((c) => (
        <Card key={c.label} className="p-4">
          <p className="text-xs uppercase tracking-wide text-muted-foreground">{c.label}</p>
          <p className={`mt-1 text-2xl font-semibold ${c.tone ?? ""}`}>
            {c.href ? (
              <Link href={c.href} className="hover:underline">
                {c.value}
              </Link>
            ) : (
              c.value
            )}
          </p>
        </Card>
      ))}
    </div>
  );
}

export function DevicesView({ initial, summary: initialSummary }: { initial: PagedResult<DeviceDto>; summary: ComplianceSummaryDto }) {
  const { toast } = useToast();
  const { isAdmin } = useCan();
  const [data, setData] = React.useState(initial);
  const [summary, setSummary] = React.useState(initialSummary);
  const [filters, setFilters] = React.useState<Filters>(initialFilters);
  const [syncing, setSyncing] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  const load = React.useCallback(async (f: Filters) => {
    try {
      const [devices, sum] = await Promise.all([
        api.get<PagedResult<DeviceDto>>("mobile-devices", { ...f, pageSize: 25 }),
        api.get<ComplianceSummaryDto>("mobile-compliance/summary"),
      ]);
      setData(devices);
      setSummary(sum);
      setError(null);
    } catch (e) {
      setError(messageFrom(e));
    }
  }, []);

  function update(patch: Partial<Filters>) {
    const next = { ...filters, ...patch, page: patch.page ?? 1 };
    setFilters(next);
    void load(next);
  }

  async function sync() {
    setSyncing(true);
    try {
      const r = await api.post<SyncResultDto>("mobile-devices/sync");
      toast({
        tone: "success",
        title: "Inventory synchronised",
        description: `${r.discovered} new, ${r.updated} updated, ${r.removed} removed, ${r.inventoryRequested} iOS refreshes requested.`,
      });
      await load(filters);
    } catch (e) {
      toast({ tone: "error", title: "Sync failed", description: messageFrom(e) });
    } finally {
      setSyncing(false);
    }
  }

  return (
    <>
      <SummaryCards summary={summary} />
      <div className="mb-4 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <form
          className="flex flex-1 flex-wrap items-center gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            update({});
          }}
        >
          <Input placeholder="Search name, model, serial…" value={filters.search} onChange={(e) => setFilters({ ...filters, search: e.target.value })} className="max-w-xs" aria-label="Search devices" />
          <Select className="w-auto" aria-label="Platform" value={filters.platform} onChange={(e) => update({ platform: e.target.value as Filters["platform"] })}>
            <option value="">All platforms</option>
            {(Object.keys(platformLabel) as MobilePlatform[]).map((p) => (
              <option key={p} value={p}>{platformLabel[p]}</option>
            ))}
          </Select>
          <Select className="w-auto" aria-label="Ownership" value={filters.ownership} onChange={(e) => update({ ownership: e.target.value as Filters["ownership"] })}>
            <option value="">All ownership</option>
            {(Object.keys(ownershipLabel) as DeviceOwnership[]).map((o) => (
              <option key={o} value={o}>{ownershipLabel[o]}</option>
            ))}
          </Select>
          <Select className="w-auto" aria-label="Compliance" value={filters.compliance} onChange={(e) => update({ compliance: e.target.value as Filters["compliance"] })}>
            <option value="">Any compliance</option>
            {(Object.keys(complianceLabel) as ComplianceState[]).map((c) => (
              <option key={c} value={c}>{complianceLabel[c]}</option>
            ))}
          </Select>
          <Select className="w-auto" aria-label="State" value={filters.state} onChange={(e) => update({ state: e.target.value as Filters["state"] })}>
            <option value="">Active devices</option>
            {(["Enrolled", "RemovalPending", "Retired", "Wiped"] as DeviceState[]).map((s) => (
              <option key={s} value={s}>{stateLabel[s]}</option>
            ))}
          </Select>
          <Button type="submit" variant="outline">Search</Button>
        </form>
        {isAdmin ? (
          <Button variant="outline" onClick={sync} loading={syncing}>
            <RefreshCw className="h-4 w-4" /> Sync now
          </Button>
        ) : null}
      </div>

      {error ? <p role="alert" className="mb-3 text-sm text-destructive">{error}</p> : null}

      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Device</TH>
              <TH>Management</TH>
              <TH>OS</TH>
              <TH>State</TH>
              <TH>Compliance</TH>
              <TH>Last seen</TH>
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={6}>No devices match. Create an enrollment to add devices.</EmptyRow>
            ) : (
              data.items.map((d) => (
                <TR key={d.id}>
                  <TD>
                    <div className="flex items-center gap-2">
                      <PlatformIcon platform={d.platform} />
                      <Link href={`/mobile/devices/${d.id}`} className="font-medium hover:underline">
                        {deviceTitle(d)}
                      </Link>
                    </div>
                    {d.serialNumber ? <p className="font-mono text-xs text-muted-foreground">{d.serialNumber}</p> : null}
                  </TD>
                  <TD>
                    <div className="flex flex-col items-start gap-1">
                      <span className="text-sm">{modeLabel[d.mode]}</span>
                      <OwnershipBadge value={d.ownership} />
                    </div>
                  </TD>
                  <TD className="text-muted-foreground">{d.osVersion ?? "—"}</TD>
                  <TD><StateBadge value={d.state} /></TD>
                  <TD><ComplianceBadge value={d.compliance} /></TD>
                  <TD className="text-muted-foreground">{formatDate(d.lastSeenAtUtc)}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
        {data.totalPages > 1 ? (
          <div className="flex items-center justify-between border-t border-border px-3 py-2 text-sm text-muted-foreground">
            <span>Page {data.page} of {data.totalPages} · {data.totalCount} devices</span>
            <div className="flex gap-1">
              <Button variant="outline" size="sm" disabled={data.page <= 1} onClick={() => update({ page: data.page - 1 })}>Previous</Button>
              <Button variant="outline" size="sm" disabled={data.page >= data.totalPages} onClick={() => update({ page: data.page + 1 })}>Next</Button>
            </div>
          </div>
        ) : null}
      </Card>
    </>
  );
}
