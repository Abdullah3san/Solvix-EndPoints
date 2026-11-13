"use client";

import * as React from "react";
import Link from "next/link";
import { Download } from "lucide-react";
import { api, PROXY_PREFIX } from "@/lib/api/client";
import type { AvailabilityGrouping, AvailabilityReportDto, BranchDto } from "@/lib/api/types";
import { formatDuration, formatPercent } from "@/lib/monitoring";
import { messageFrom } from "@/lib/forms";
import { buttonClasses } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Field } from "@/components/ui/field";
import { Input, Select } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";

interface Filters {
  from: string;
  to: string;
  branchId: string;
  groupBy: AvailabilityGrouping;
}

export function AvailabilityReport({ initial, branches }: { initial: AvailabilityReportDto; branches: BranchDto[] }) {
  const [report, setReport] = React.useState(initial);
  const [filters, setFilters] = React.useState<Filters>({ from: initial.from, to: initial.to, branchId: "", groupBy: initial.groupBy });
  const [error, setError] = React.useState<string | null>(null);
  const query = { from: filters.from, to: filters.to, branchId: filters.branchId, groupBy: filters.groupBy };

  async function update(next: Partial<Filters>) {
    const merged = { ...filters, ...next };
    setFilters(merged);
    try {
      setReport(await api.get<AvailabilityReportDto>("monitoring/availability", { ...merged }));
      setError(null);
    } catch (e) {
      setError(messageFrom(e));
    }
  }

  const exportUrl = `${PROXY_PREFIX}/monitoring/availability/export?${new URLSearchParams(Object.entries(query).filter(([, v]) => v)).toString()}`;
  const maxDay = Math.max(1, ...report.days.map((d) => d.totals.onlineSeconds + d.totals.warningSeconds + d.totals.offlineSeconds));

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="grid gap-4 p-5 sm:grid-cols-5 sm:items-end">
          <Field label="From" htmlFor="from">
            <Input id="from" type="date" value={filters.from} max={filters.to} onChange={(e) => update({ from: e.target.value })} />
          </Field>
          <Field label="To (UTC days)" htmlFor="to">
            <Input id="to" type="date" value={filters.to} min={filters.from} onChange={(e) => update({ to: e.target.value })} />
          </Field>
          <Field label="Branch" htmlFor="branch">
            <Select id="branch" value={filters.branchId} onChange={(e) => update({ branchId: e.target.value })}>
              <option value="">All branches</option>
              {branches.map((b) => (
                <option key={b.id} value={b.id}>
                  {b.name}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Group by" htmlFor="groupBy">
            <Select id="groupBy" value={filters.groupBy} onChange={(e) => update({ groupBy: e.target.value as AvailabilityGrouping })}>
              <option value="Device">Device</option>
              <option value="Branch">Branch</option>
            </Select>
          </Field>
          <a href={exportUrl} className={buttonClasses({ variant: "outline" })} download>
            <Download className="h-4 w-4" /> Export CSV
          </a>
        </CardContent>
      </Card>
      {error ? <Alert tone="error">{error}</Alert> : null}

      <div className="grid gap-4 sm:grid-cols-4">
        <Stat label="Availability" value={formatPercent(report.totals.availabilityPercent)} />
        <Stat label="Uptime (online + warning)" value={formatDuration(report.totals.uptimeSeconds)} />
        <Stat label="Downtime (offline)" value={formatDuration(report.totals.downtimeSeconds)} />
        <Stat label="Offline incidents" value={String(report.totals.offlineCount)} />
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Daily availability</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="flex h-32 items-end gap-1" role="img" aria-label="Daily online, warning and offline time">
            {report.days.map((d) => {
              const t = d.totals;
              const total = t.onlineSeconds + t.warningSeconds + t.offlineSeconds;
              return (
                <div key={d.day} className="flex flex-1 flex-col-reverse overflow-hidden rounded-sm" style={{ height: `${(100 * total) / maxDay}%`, minHeight: 2 }} title={`${d.day}: ${formatPercent(t.availabilityPercent)}`}>
                  <div className="bg-success" style={{ flexGrow: t.onlineSeconds }} />
                  <div className="bg-warning" style={{ flexGrow: t.warningSeconds }} />
                  <div className="bg-destructive" style={{ flexGrow: t.offlineSeconds }} />
                </div>
              );
            })}
          </div>
          <div className="mt-2 flex justify-between text-xs text-muted-foreground">
            <span>{report.from}</span>
            <span>{report.to}</span>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="p-0">
          <Table>
            <THead>
              <TR>
                <TH>{report.groupBy === "Branch" ? "Branch" : "Device"}</TH>
                {report.groupBy === "Device" ? <TH>Branch</TH> : null}
                <TH className="text-right">Availability</TH>
                <TH className="text-right">Uptime</TH>
                <TH className="text-right">Downtime</TH>
                <TH className="text-right">Offline incidents</TH>
              </TR>
            </THead>
            <TBody>
              {report.rows.length === 0 ? <EmptyRow colSpan={6}>No monitoring data in this period.</EmptyRow> : null}
              {report.rows.map((r) => (
                <TR key={r.id}>
                  <TD className="font-medium">
                    {report.groupBy === "Device" ? (
                      <Link href={`/endpoints/devices/${r.id}`} className="hover:underline">
                        {r.name}
                      </Link>
                    ) : (
                      r.name
                    )}
                  </TD>
                  {report.groupBy === "Device" ? <TD className="text-muted-foreground">{r.branchName ?? "—"}</TD> : null}
                  <TD className="text-right tabular-nums">{formatPercent(r.totals.availabilityPercent)}</TD>
                  <TD className="text-right tabular-nums">{formatDuration(r.totals.uptimeSeconds)}</TD>
                  <TD className={`text-right tabular-nums ${r.totals.downtimeSeconds ? "text-destructive" : ""}`}>{formatDuration(r.totals.downtimeSeconds)}</TD>
                  <TD className="text-right tabular-nums">{r.totals.offlineCount}</TD>
                </TR>
              ))}
            </TBody>
          </Table>
        </CardContent>
      </Card>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <Card>
      <CardContent className="p-4">
        <p className="text-2xl font-semibold tabular-nums">{value}</p>
        <p className="text-xs text-muted-foreground">{label}</p>
      </CardContent>
    </Card>
  );
}
