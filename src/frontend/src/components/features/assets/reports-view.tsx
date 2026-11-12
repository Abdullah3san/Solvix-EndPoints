"use client";

import * as React from "react";
import Link from "next/link";
import { Download } from "lucide-react";
import { api, PROXY_PREFIX } from "@/lib/api/client";
import type { AssetSummaryReportDto, AuditLogEntryDto, CountValueDto, DepreciationReportDto } from "@/lib/api/types";
import { categoryLabel, formatDay, formatMoney, statusLabel } from "@/lib/assets";
import { messageFrom } from "@/lib/forms";
import { buttonClasses } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { AuditLogList } from "./asset-details";

function Kpi({ label, value, hint, href }: { label: string; value: React.ReactNode; hint?: string; href?: string }) {
  const body = (
    <CardContent className="p-4">
      <p className="text-xs text-muted-foreground">{label}</p>
      <p className="mt-1 text-xl font-semibold tabular-nums">{value}</p>
      {hint ? <p className="mt-0.5 text-xs text-muted-foreground">{hint}</p> : null}
    </CardContent>
  );
  return <Card className={href ? "transition-colors hover:border-primary/50" : undefined}>{href ? <Link href={href} prefetch={false}>{body}</Link> : body}</Card>;
}

/** Horizontal bar list: one hue, labels and values in text so the chart works without colour. */
function BarList({ title, rows, href }: { title: string; rows: CountValueDto[]; href?: (row: CountValueDto) => string | null }) {
  const max = Math.max(1, ...rows.map((r) => r.count));
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
      </CardHeader>
      <CardContent>
        {rows.length === 0 ? (
          <p className="text-sm text-muted-foreground">No data.</p>
        ) : (
          <ul className="space-y-2">
            {rows.map((r) => {
              const link = href?.(r);
              const label = r.label ?? r.key;
              return (
                <li key={r.key} className="text-sm">
                  <div className="mb-1 flex justify-between gap-2">
                    {link ? (
                      <Link href={link} prefetch={false} className="truncate hover:underline">
                        {label}
                      </Link>
                    ) : (
                      <span className="truncate">{label}</span>
                    )}
                    <span className="tabular-nums text-muted-foreground">{r.count}</span>
                  </div>
                  <div className="h-2 rounded-full bg-muted">
                    <div className="h-2 rounded-full bg-primary" style={{ width: `${(r.count / max) * 100}%` }} />
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

export function ReportsView({ summary, depreciation, auditLog }: { summary: AssetSummaryReportDto; depreciation: DepreciationReportDto; auditLog: AuditLogEntryDto[] }) {
  const { toast } = useToast();
  const [dep, setDep] = React.useState(depreciation);
  const [asOf, setAsOf] = React.useState(depreciation.asOf);
  const c = summary.endpointCoverage;

  async function changeAsOf(value: string) {
    setAsOf(value);
    if (!value) return;
    try {
      setDep(await api.get<DepreciationReportDto>("asset-reports/depreciation", { asOf: value }));
    } catch (e) {
      toast({ tone: "error", title: "Could not load depreciation", description: messageFrom(e) });
    }
  }

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Kpi label="Assets in service" value={summary.activeAssets} hint={`${summary.totalAssets} registered in total`} href="/assets" />
        <Kpi label="Purchase cost" value={formatMoney(summary.totalPurchaseCost)} hint="Excluding disposed assets" />
        <Kpi label="Book value" value={formatMoney(summary.totalBookValue)} hint={`As of ${formatDay(summary.asOf)}`} />
        <Kpi label="Maintenance cost this year" value={formatMoney(summary.maintenanceCostThisYear)} hint={`${summary.openMaintenance} open work orders`} href="/assets/maintenance" />
        <Kpi label="Warranty expiring soon" value={summary.warrantyExpiringSoon} hint={`${summary.warrantyExpired} expired · ${summary.warrantyNone} without warranty`} href="/assets?warranty=ExpiringSoon" />
        <Kpi label="Not audited in 12 months" value={summary.notAuditedIn365Days} href="/assets/audits" />
        <Kpi label="Endpoint coverage" value={`${c.linkedAssets} / ${c.manageableAssets}`} hint={`agent-capable assets linked · ${c.pendingSuggestions} to review`} href="/assets/matching" />
        <Kpi label="Endpoints without asset" value={c.endpointsWithoutAsset} hint={`of ${c.activeEndpoints} active managed endpoints`} href="/assets/matching" />
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        <BarList title="By status" rows={summary.byStatus.map((r) => ({ ...r, label: statusLabel(r.key as never) }))} href={(r) => `/assets?status=${r.key}`} />
        <BarList title="By category" rows={summary.byCategory} href={(r) => `/assets?category=${r.key}`} />
        <BarList title="By branch (in service)" rows={summary.byBranch} href={(r) => (r.key === "none" ? null : `/assets?branchId=${r.key}`)} />
      </div>

      <Card>
        <CardHeader className="flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
          <div>
            <CardTitle>Depreciation</CardTitle>
            <CardDescription>Accumulated depreciation and book value by category.</CardDescription>
          </div>
          <div className="flex items-center gap-2">
            <label className="text-sm text-muted-foreground" htmlFor="dep-asof">
              As of
            </label>
            <Input id="dep-asof" type="date" className="w-40" value={asOf} onChange={(e) => changeAsOf(e.target.value)} />
            <a href={`${PROXY_PREFIX}/asset-reports/depreciation/export?asOf=${asOf}`} className={buttonClasses({ variant: "outline" })} download>
              <Download className="h-4 w-4" /> CSV
            </a>
          </div>
        </CardHeader>
        <Table>
          <THead>
            <TR>
              <TH>Category</TH>
              <TH className="text-right">Assets</TH>
              <TH className="text-right">Cost</TH>
              <TH className="text-right">Accumulated</TH>
              <TH className="text-right">Book value</TH>
            </TR>
          </THead>
          <TBody>
            {dep.rows.length === 0 ? (
              <EmptyRow colSpan={5}>No depreciated assets (set a method, life, cost and purchase date on assets).</EmptyRow>
            ) : (
              <>
                {dep.rows.map((r) => (
                  <TR key={r.category}>
                    <TD>{categoryLabel(r.category)}</TD>
                    <TD className="text-right tabular-nums">{r.assets}</TD>
                    <TD className="text-right tabular-nums">{formatMoney(r.cost)}</TD>
                    <TD className="text-right tabular-nums">{formatMoney(r.accumulatedDepreciation)}</TD>
                    <TD className="text-right tabular-nums">{formatMoney(r.bookValue)}</TD>
                  </TR>
                ))}
                <TR className="font-semibold">
                  <TD>Total</TD>
                  <TD className="text-right tabular-nums">{dep.rows.reduce((n, r) => n + r.assets, 0)}</TD>
                  <TD className="text-right tabular-nums">{formatMoney(dep.cost)}</TD>
                  <TD className="text-right tabular-nums">{formatMoney(dep.accumulatedDepreciation)}</TD>
                  <TD className="text-right tabular-nums">{formatMoney(dep.bookValue)}</TD>
                </TR>
              </>
            )}
          </TBody>
        </Table>
      </Card>

      <div>
        <h2 className="mb-2 text-base font-semibold">Recent activity (audit log)</h2>
        <AuditLogList entries={auditLog} />
      </div>
    </div>
  );
}
