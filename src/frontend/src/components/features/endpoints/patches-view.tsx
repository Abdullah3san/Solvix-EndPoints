"use client";

import * as React from "react";
import { Check, Download, X } from "lucide-react";
import { api, PROXY_PREFIX } from "@/lib/api/client";
import type { PagedResult } from "@/lib/api/types";
import type { PatchComplianceReportDto, PatchDto, PatchFailureDto, PatchApprovalState } from "@/lib/api/endpoint-types";
import { complianceTone } from "@/lib/api/endpoint-types";
import { messageFrom } from "@/lib/forms";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox, Input, Select } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { StatTile, formatRelative, useNow } from "./common";

const severityTone = { Critical: "danger", Important: "warning", Moderate: "brand", Low: "neutral", Unspecified: "neutral" } as const;
const approvalTone: Record<PatchApprovalState, "success" | "danger" | "neutral"> = { Approved: "success", Declined: "danger", NotReviewed: "neutral" };

export function PatchesView({
  initialPatches,
  initialCompliance,
  initialFailures,
}: {
  initialPatches: PagedResult<PatchDto>;
  initialCompliance: PatchComplianceReportDto;
  initialFailures: PagedResult<PatchFailureDto>;
}) {
  const { toast } = useToast();
  const { isAdmin } = useCan();
  const now = useNow();
  const [patches, setPatches] = React.useState(initialPatches);
  const [compliance, setCompliance] = React.useState(initialCompliance);
  const [failures, setFailures] = React.useState(initialFailures);
  const [selected, setSelected] = React.useState<Set<string>>(new Set());
  const [search, setSearch] = React.useState("");
  const [approval, setApproval] = React.useState("");
  const [onlyNeeded, setOnlyNeeded] = React.useState(true);
  const [busy, setBusy] = React.useState(false);

  const reload = React.useCallback(async () => {
    const [p, c, f] = await Promise.all([
      api.get<PagedResult<PatchDto>>("patches", { search, approvalState: approval || undefined, onlyNeeded, pageSize: 100 }),
      api.get<PatchComplianceReportDto>("patches/compliance"),
      api.get<PagedResult<PatchFailureDto>>("patches/failures"),
    ]);
    setPatches(p);
    setCompliance(c);
    setFailures(f);
  }, [search, approval, onlyNeeded]);

  async function decide(action: "approve" | "decline") {
    setBusy(true);
    try {
      await api.post(`patches/${action}`, { patchIds: [...selected], note: null });
      toast({ tone: "success", title: `${selected.size} patch(es) ${action === "approve" ? "approved" : "declined"}` });
      setSelected(new Set());
      await reload();
    } catch (e) {
      toast({ tone: "error", title: `Could not ${action}`, description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  function toggle(id: string) {
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  return (
    <div className="space-y-6">
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
        <StatTile label="Compliance" value={`${compliance.compliancePercent}%`} hint={`${compliance.devices} devices`} tone={compliance.compliancePercent >= 95 ? "success" : compliance.compliancePercent >= 80 ? "warning" : "danger"} />
        <StatTile label="Compliant" value={compliance.compliant} tone="success" />
        <StatTile label="Pending restart" value={compliance.pendingRestart} tone="warning" />
        <StatTile label="Non-compliant" value={compliance.nonCompliant} tone="danger" />
        <StatTile label="Unknown / stale" value={compliance.unknown} hint="No successful scan in 7 days" />
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        {[
          { title: "By branch", rows: compliance.byBranch },
          { title: "By department", rows: compliance.byDepartment },
          { title: "By device group", rows: compliance.byDeviceGroup },
        ].map((block) => (
          <Card key={block.title}>
            <CardHeader>
              <CardTitle className="text-sm">{block.title}</CardTitle>
            </CardHeader>
            <Table>
              <TBody>
                {block.rows.length === 0 ? (
                  <EmptyRow colSpan={2}>No data.</EmptyRow>
                ) : (
                  block.rows.map((r) => (
                    <TR key={r.id ?? r.name}>
                      <TD>{r.name}</TD>
                      <TD className="text-right tabular-nums">
                        {r.compliant}/{r.devices} · {r.compliancePercent}%
                      </TD>
                    </TR>
                  ))
                )}
              </TBody>
            </Table>
          </Card>
        ))}
      </div>

      <Card>
        <CardHeader className="flex flex-row flex-wrap items-center justify-between gap-2">
          <div>
            <CardTitle>Patch inventory</CardTitle>
            <CardDescription>Updates reported by the Windows Update Agent on your devices.</CardDescription>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Input placeholder="Title, KB or CVE…" value={search} onChange={(e) => setSearch(e.target.value)} className="w-48" aria-label="Search patches" />
            <Select aria-label="Approval" value={approval} onChange={(e) => setApproval(e.target.value)} className="w-40">
              <option value="">Any approval</option>
              <option value="NotReviewed">Not reviewed</option>
              <option value="Approved">Approved</option>
              <option value="Declined">Declined</option>
            </Select>
            <label className="flex items-center gap-2 text-sm text-muted-foreground">
              <Checkbox checked={onlyNeeded} onChange={(e) => setOnlyNeeded(e.target.checked)} /> Needed only
            </label>
            <Button variant="outline" onClick={() => void reload()}>
              Apply
            </Button>
            <a href={`${PROXY_PREFIX}/patches/compliance/report.csv`} className="inline-flex h-9 items-center gap-2 rounded-md border border-border px-3 text-sm hover:bg-muted">
              <Download className="h-4 w-4" /> Compliance CSV
            </a>
          </div>
        </CardHeader>
        {isAdmin && selected.size > 0 ? (
          <div className="flex items-center gap-2 border-y border-border bg-muted/40 px-4 py-2 text-sm">
            <span>{selected.size} selected</span>
            <Button size="sm" loading={busy} onClick={() => void decide("approve")}>
              <Check className="h-4 w-4" /> Approve
            </Button>
            <Button size="sm" variant="outline" loading={busy} onClick={() => void decide("decline")}>
              <X className="h-4 w-4" /> Decline
            </Button>
          </div>
        ) : null}
        <Table>
          <THead>
            <TR>
              {isAdmin ? <TH className="w-8" /> : null}
              <TH>Update</TH>
              <TH>Classification</TH>
              <TH>Severity</TH>
              <TH className="text-right">Missing</TH>
              <TH className="text-right">Failed</TH>
              <TH className="text-right">Installed</TH>
              <TH>Approval</TH>
            </TR>
          </THead>
          <TBody>
            {patches.items.length === 0 ? (
              <EmptyRow colSpan={8}>No patches match.</EmptyRow>
            ) : (
              patches.items.map((p) => (
                <TR key={p.id}>
                  {isAdmin ? (
                    <TD>
                      <Checkbox aria-label={`Select ${p.title}`} checked={selected.has(p.id)} onChange={() => toggle(p.id)} />
                    </TD>
                  ) : null}
                  <TD>
                    <span className="font-medium">{p.title}</span>
                    <span className="block text-xs text-muted-foreground">
                      {p.kbArticleIds.map((kb) => `KB${kb}`).join(", ")} {p.cveIds.length ? `· ${p.cveIds.length} CVE` : ""}
                    </span>
                  </TD>
                  <TD className="text-muted-foreground">{p.classification}</TD>
                  <TD>
                    <Badge tone={severityTone[p.severity]}>{p.severity}</Badge>
                  </TD>
                  <TD className="text-right tabular-nums">{p.devicesMissing}</TD>
                  <TD className="text-right tabular-nums">{p.devicesFailed}</TD>
                  <TD className="text-right tabular-nums">{p.devicesInstalled + p.devicesPendingRestart}</TD>
                  <TD>
                    <Badge tone={approvalTone[p.approvalState]}>{p.autoApprovalRuleId ? "Auto-approved" : p.approvalState}</Badge>
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Devices needing attention</CardTitle>
          </CardHeader>
          <Table>
            <THead>
              <TR>
                <TH>Device</TH>
                <TH>Status</TH>
                <TH className="text-right">Missing approved</TH>
                <TH>Last scan</TH>
              </TR>
            </THead>
            <TBody>
              {compliance.deviceRows.filter((r) => r.status !== "Compliant").length === 0 ? (
                <EmptyRow colSpan={4}>Every device is compliant.</EmptyRow>
              ) : (
                compliance.deviceRows
                  .filter((r) => r.status !== "Compliant")
                  .slice(0, 25)
                  .map((r) => (
                    <TR key={r.deviceId}>
                      <TD className="font-medium">{r.hostname}</TD>
                      <TD>
                        <Badge tone={complianceTone[r.status]}>{r.status}</Badge>
                      </TD>
                      <TD className="text-right tabular-nums">
                        {r.missingApproved}
                        {r.missingCritical ? <span className="text-destructive"> ({r.missingCritical} critical)</span> : null}
                      </TD>
                      <TD className="text-muted-foreground">{formatRelative(r.lastSuccessfulScanUtc, now)}</TD>
                    </TR>
                  ))
              )}
            </TBody>
          </Table>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Failed updates</CardTitle>
            <CardDescription>From Windows Update history and deployment results.</CardDescription>
          </CardHeader>
          <Table>
            <THead>
              <TR>
                <TH>Device</TH>
                <TH>Update</TH>
                <TH>Error</TH>
                <TH className="text-right">Failures</TH>
              </TR>
            </THead>
            <TBody>
              {failures.items.length === 0 ? (
                <EmptyRow colSpan={4}>No failed updates.</EmptyRow>
              ) : (
                failures.items.map((f) => (
                  <TR key={`${f.deviceId}-${f.patchId}`}>
                    <TD className="font-medium">{f.hostname}</TD>
                    <TD className="max-w-xs truncate" title={f.title}>
                      {f.title}
                    </TD>
                    <TD className="font-mono text-xs">{f.lastFailureHex ?? "—"}</TD>
                    <TD className="text-right">
                      <Badge tone={f.repeatedFailure ? "danger" : "warning"}>{f.failureCount}</Badge>
                    </TD>
                  </TR>
                ))
              )}
            </TBody>
          </Table>
        </Card>
      </div>
    </div>
  );
}
