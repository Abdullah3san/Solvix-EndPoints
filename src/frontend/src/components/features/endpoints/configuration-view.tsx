"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Plus } from "lucide-react";
import { api } from "@/lib/api/client";
import type { PagedResult } from "@/lib/api/types";
import type { BaselineDto, ConfigurationComplianceDto, ConfigurationPolicyDto, ConfigurationPolicySummaryDto, DeviceConflictsDto, DriftEventDto } from "@/lib/api/endpoint-types";
import { riskTone } from "@/lib/api/endpoint-types";
import { messageFrom } from "@/lib/forms";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { StatTile, formatRelative, useNow } from "./common";

export function ConfigurationView({
  policies,
  compliance,
  conflicts,
  drift,
  baselines,
}: {
  policies: ConfigurationPolicySummaryDto[];
  compliance: ConfigurationComplianceDto;
  conflicts: DeviceConflictsDto[];
  drift: PagedResult<DriftEventDto>;
  baselines: BaselineDto[];
}) {
  const { isAdmin } = useCan();
  const now = useNow();
  const [creating, setCreating] = React.useState(false);
  const unresolved = conflicts.reduce((n, d) => n + d.conflicts.filter((c) => !c.isResolved).length, 0);

  return (
    <div className="space-y-6">
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
        <StatTile label="Compliance" value={`${compliance.compliancePercent}%`} hint={`${compliance.devicesEvaluated} devices evaluated`} tone={compliance.compliancePercent >= 95 ? "success" : "warning"} />
        <StatTile label="Compliant" value={compliance.devicesCompliant} tone="success" />
        <StatTile label="Drifted" value={compliance.devicesWithDrift} tone="warning" />
        <StatTile label="Errors / refused" value={compliance.devicesWithErrors} tone="danger" />
        <StatTile label="Unresolved conflicts" value={unresolved} tone={unresolved ? "danger" : "neutral"} hint="Withheld until resolved" />
      </div>

      {unresolved > 0 ? (
        <Alert tone="error" title="Policy conflicts">
          Some settings are set differently by policies with the same priority and target specificity, so they are not applied. Change an assignment priority to resolve them.
        </Alert>
      ) : null}

      <Card>
        <CardHeader className="flex flex-row items-center justify-between">
          <div>
            <CardTitle>Policies</CardTitle>
            <CardDescription>Versioned desired-state configuration for Windows devices.</CardDescription>
          </div>
          {isAdmin ? (
            <Button onClick={() => setCreating(true)}>
              <Plus className="h-4 w-4" /> From baseline
            </Button>
          ) : null}
        </CardHeader>
        <Table>
          <THead>
            <TR>
              <TH>Policy</TH>
              <TH>Active version</TH>
              <TH>Latest</TH>
              <TH>Assignments</TH>
              <TH>Devices</TH>
            </TR>
          </THead>
          <TBody>
            {policies.length === 0 ? (
              <EmptyRow colSpan={5}>No policies yet. Start from an approved baseline.</EmptyRow>
            ) : (
              policies.map((p) => {
                const c = compliance.policies.find((x) => x.policyId === p.id);
                return (
                  <TR key={p.id}>
                    <TD>
                      <Link href={`/endpoints/configuration/${p.id}`} className="font-medium text-primary hover:underline">
                        {p.name}
                      </Link>
                      {p.description ? <span className="block max-w-md truncate text-xs text-muted-foreground">{p.description}</span> : null}
                    </TD>
                    <TD>
                      {p.activeVersion ? (
                        <span className="flex items-center gap-2">
                          v{p.activeVersion}
                          {p.activeVersionRisk ? <Badge tone={riskTone[p.activeVersionRisk]}>{p.activeVersionRisk}</Badge> : null}
                        </span>
                      ) : (
                        <span className="text-muted-foreground">Not published</span>
                      )}
                    </TD>
                    <TD>
                      v{p.latestVersion} <span className="text-xs text-muted-foreground">{p.latestVersionStatus}</span>
                    </TD>
                    <TD>{p.assignmentCount}</TD>
                    <TD className="text-sm">
                      {c ? (
                        <>
                          {c.compliant}/{c.devices} compliant
                          {c.drifted ? <span className="text-warning"> · {c.drifted} drifted</span> : null}
                          {c.failed ? <span className="text-destructive"> · {c.failed} failed</span> : null}
                        </>
                      ) : (
                        "—"
                      )}
                    </TD>
                  </TR>
                );
              })
            )}
          </TBody>
        </Table>
      </Card>

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Conflicts</CardTitle>
          </CardHeader>
          <Table>
            <THead>
              <TR>
                <TH>Device</TH>
                <TH>Setting</TH>
                <TH>Resolution</TH>
              </TR>
            </THead>
            <TBody>
              {conflicts.length === 0 ? (
                <EmptyRow colSpan={3}>No conflicts.</EmptyRow>
              ) : (
                conflicts.flatMap((d) =>
                  d.conflicts.map((c) => (
                    <TR key={`${d.deviceId}-${c.key}`}>
                      <TD className="font-medium">{d.hostname}</TD>
                      <TD className="font-mono text-xs">{c.key}</TD>
                      <TD className="text-xs">
                        <Badge tone={c.isResolved ? "success" : "danger"}>{c.isResolved ? "Resolved" : "Withheld"}</Badge> {c.reason}
                      </TD>
                    </TR>
                  )),
                )
              )}
            </TBody>
          </Table>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Recent drift</CardTitle>
          </CardHeader>
          <Table>
            <THead>
              <TR>
                <TH>Setting</TH>
                <TH>Outcome</TH>
                <TH>Found</TH>
                <TH>When</TH>
              </TR>
            </THead>
            <TBody>
              {drift.items.length === 0 ? (
                <EmptyRow colSpan={4}>No drift detected.</EmptyRow>
              ) : (
                drift.items.map((e) => (
                  <TR key={e.id}>
                    <TD className="font-mono text-xs">{e.settingKey}</TD>
                    <TD>
                      <Badge tone={e.status === "Remediated" ? "success" : "warning"}>{e.status}</Badge>
                    </TD>
                    <TD className="text-xs text-muted-foreground">{e.status === "Remediated" ? `now ${e.actualValue}` : e.actualValue}</TD>
                    <TD className="text-muted-foreground">{formatRelative(e.detectedAtUtc, now)}</TD>
                  </TR>
                ))
              )}
            </TBody>
          </Table>
        </Card>
      </div>

      <FromBaselineDialog open={creating} onClose={() => setCreating(false)} baselines={baselines} />
    </div>
  );
}

function FromBaselineDialog({ open, onClose, baselines }: { open: boolean; onClose: () => void; baselines: BaselineDto[] }) {
  const router = useRouter();
  const { toast } = useToast();
  const [baselineId, setBaselineId] = React.useState(baselines[0]?.id ?? "");
  const [name, setName] = React.useState("Workstation security");
  const [busy, setBusy] = React.useState(false);
  const baseline = baselines.find((b) => b.id === baselineId);

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Create policy from an approved baseline"
      description="The policy starts as a draft and goes through the normal approval workflow."
      className="max-w-2xl"
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button
            loading={busy}
            onClick={async () => {
              setBusy(true);
              try {
                const policy = await api.post<ConfigurationPolicyDto>("configuration/baselines/policies", { baselineId, name, description: null });
                router.push(`/endpoints/configuration/${policy.id}`);
              } catch (e) {
                toast({ tone: "error", title: "Could not create policy", description: messageFrom(e) });
              } finally {
                setBusy(false);
              }
            }}
          >
            Create draft
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        <Field label="Baseline" htmlFor="baseline">
          <Select id="baseline" value={baselineId} onChange={(e) => setBaselineId(e.target.value)}>
            {baselines.map((b) => (
              <option key={b.id} value={b.id}>
                {b.name}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Policy name" htmlFor="baseline-name" required>
          <Input id="baseline-name" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        {baseline ? (
          <ul className="max-h-60 space-y-1 overflow-y-auto rounded-md border border-border p-2 text-sm">
            {baseline.settings.map((s) => (
              <li key={s.key} className="flex items-center justify-between gap-2">
                <span>{s.description}</span>
                <Badge tone={riskTone[s.risk]}>{s.risk}</Badge>
              </li>
            ))}
          </ul>
        ) : null}
      </div>
    </Dialog>
  );
}
