"use client";

import * as React from "react";
import { Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import type { AssignmentDto, ConfigurationPolicyDto, EnforcementMode, MaintenanceWindowDto, PolicyVersionDto, RemovalBehavior, TargetDto } from "@/lib/api/endpoint-types";
import { riskTone, versionActions } from "@/lib/api/endpoint-types";
import { messageFrom } from "@/lib/forms";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Select } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { ConfirmDialog, NumberInput, TargetPicker, describeTarget, type TargetOptions } from "./common";

const versionTone = { Draft: "neutral", PendingApproval: "warning", Approved: "brand", Rejected: "danger", Published: "success", Superseded: "neutral" } as const;
const actionLabel: Record<string, string> = { submit: "Submit for approval", approve: "Approve", reject: "Reject", publish: "Publish", restore: "Restore as new draft" };

export function PolicyDetailView({ initial, targets, windows }: { initial: ConfigurationPolicyDto; targets: TargetOptions; windows: MaintenanceWindowDto[] }) {
  const { toast } = useToast();
  const { isAdmin } = useCan();
  const [policy, setPolicy] = React.useState(initial);
  const [selected, setSelected] = React.useState(initial.versions[0]?.number ?? 1);
  const [pending, setPending] = React.useState<{ action: string; version: PolicyVersionDto } | null>(null);
  const [assigning, setAssigning] = React.useState(false);
  const [busy, setBusy] = React.useState(false);

  const version = policy.versions.find((v) => v.number === selected) ?? policy.versions[0];
  const reload = React.useCallback(async () => setPolicy(await api.get<ConfigurationPolicyDto>(`configuration-policies/${initial.id}`)), [initial.id]);

  async function run(action: string, v: PolicyVersionDto, note: string, acknowledged: boolean) {
    setBusy(true);
    try {
      const base = `configuration-policies/${policy.id}/versions/${v.number}`;
      if (action === "approve" || action === "reject") {
        await api.post(`${base}/${action}`, { note: note || null, acknowledgeDestructive: acknowledged });
      } else {
        const result = await api.post<PolicyVersionDto>(`${base}/${action}`);
        if (action === "restore") setSelected(result.number);
      }

      toast({ tone: "success", title: `v${v.number}: ${actionLabel[action]}` });
      setPending(null);
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Action failed", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  async function removeAssignment(a: AssignmentDto) {
    try {
      await api.delete(`configuration/assignments/${a.id}`);
      toast({ tone: "success", title: "Assignment removed", description: a.removalBehavior === "Revert" ? "Devices restore their previous values." : "Devices keep the applied values." });
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not remove assignment", description: messageFrom(e) });
    }
  }

  return (
    <div className="space-y-6">
      {policy.isArchived ? <Alert tone="info">This policy is archived and no longer delivered to devices.</Alert> : null}
      <div className="grid gap-4 lg:grid-cols-[16rem_1fr]">
        <Card>
          <CardHeader>
            <CardTitle className="text-sm">Versions</CardTitle>
          </CardHeader>
          <ul className="divide-y divide-border">
            {policy.versions.map((v) => (
              <li key={v.id}>
                <button
                  type="button"
                  onClick={() => setSelected(v.number)}
                  className={`flex w-full items-center justify-between px-4 py-2 text-left text-sm hover:bg-muted ${v.number === version?.number ? "bg-muted" : ""}`}
                >
                  <span>
                    v{v.number}
                    {policy.activeVersionId === v.id ? <span className="ml-1 text-xs text-success">active</span> : null}
                  </span>
                  <Badge tone={versionTone[v.status]}>{v.status}</Badge>
                </button>
              </li>
            ))}
          </ul>
        </Card>

        {version ? (
          <Card>
            <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-2">
              <div>
                <CardTitle className="flex items-center gap-2">
                  Version {version.number} <Badge tone={riskTone[version.maxRisk]}>{version.maxRisk} risk</Badge>
                </CardTitle>
                <CardDescription>
                  {version.changeNote ?? "No change note."} Approval: {version.approvalRequirement}
                  {version.reviewNote ? ` Review note: ${version.reviewNote}` : ""}
                </CardDescription>
              </div>
              {isAdmin ? (
                <div className="flex flex-wrap gap-1">
                  {versionActions(version.status).map((action) => (
                    <Button key={action} size="sm" variant={action === "reject" ? "outline" : "secondary"} onClick={() => setPending({ action, version })}>
                      {actionLabel[action]}
                    </Button>
                  ))}
                </div>
              ) : null}
            </CardHeader>
            <Table>
              <THead>
                <TR>
                  <TH>Setting</TH>
                  <TH>Kind</TH>
                  <TH className="w-80">Risk</TH>
                </TR>
              </THead>
              <TBody>
                {version.settings.length === 0 ? (
                  <EmptyRow colSpan={3}>No settings.</EmptyRow>
                ) : (
                  version.settings.map((s) => (
                    <TR key={s.key}>
                      <TD>
                        {s.description}
                        <span className="block font-mono text-xs text-muted-foreground">{s.key}</span>
                      </TD>
                      <TD className="text-muted-foreground">{s.kind}</TD>
                      <TD>
                        <Badge tone={riskTone[s.risk]} title={s.riskReason}>
                          {s.risk}
                        </Badge>
                        <span className="block text-xs text-muted-foreground">{s.riskReason}</span>
                      </TD>
                    </TR>
                  ))
                )}
              </TBody>
            </Table>
          </Card>
        ) : null}
      </div>

      <Card>
        <CardHeader className="flex flex-row items-center justify-between">
          <div>
            <CardTitle>Assignments</CardTitle>
            <CardDescription>Lower priority wins conflicts; on a tie the more specific target wins.</CardDescription>
          </div>
          {isAdmin && !policy.isArchived ? (
            <Button onClick={() => setAssigning(true)}>
              <Plus className="h-4 w-4" /> Assign
            </Button>
          ) : null}
        </CardHeader>
        <Table>
          <THead>
            <TR>
              <TH>Target</TH>
              <TH>Mode</TH>
              <TH>Priority</TH>
              <TH>Window</TH>
              <TH>When removed</TH>
              {isAdmin ? <TH /> : null}
            </TR>
          </THead>
          <TBody>
            {policy.assignments.length === 0 ? (
              <EmptyRow colSpan={6}>Not assigned to any devices.</EmptyRow>
            ) : (
              policy.assignments.map((a) => (
                <TR key={a.id}>
                  <TD>{describeTarget(a, targets)}</TD>
                  <TD>{a.isExclusion ? "—" : <Badge tone={a.mode === "Enforce" ? "brand" : "neutral"}>{a.mode}</Badge>}</TD>
                  <TD>{a.isExclusion ? "—" : a.priority}</TD>
                  <TD className="text-muted-foreground">{a.maintenanceWindowId ? (windows.find((w) => w.id === a.maintenanceWindowId)?.name ?? "?") : "Any time"}</TD>
                  <TD className="text-muted-foreground">{a.removalBehavior === "Revert" ? "Restore previous values" : "Keep values"}</TD>
                  {isAdmin ? (
                    <TD className="text-right">
                      <Button variant="ghost" size="icon" className="text-destructive" aria-label="Remove assignment" onClick={() => void removeAssignment(a)}>
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </TD>
                  ) : null}
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>

      <ConfirmDialog
        key={pending ? `${pending.action}-${pending.version.number}` : "none"}
        open={pending !== null}
        title={pending ? `${actionLabel[pending.action]} – v${pending.version.number}` : ""}
        description={pending?.action === "approve" ? pending.version.approvalRequirement : undefined}
        confirmLabel={pending ? actionLabel[pending.action] : "Confirm"}
        acknowledgement={
          pending?.action === "approve" && pending.version.maxRisk === "Destructive"
            ? "I authorise these destructive changes on every device this policy is assigned to."
            : undefined
        }
        destructive={pending?.version.maxRisk === "Destructive"}
        busy={busy}
        onClose={() => setPending(null)}
        onConfirm={(note, acknowledged) => pending && void run(pending.action, pending.version, note, acknowledged)}
      />
      <AssignDialog open={assigning} onClose={() => setAssigning(false)} policyId={policy.id} targets={targets} windows={windows} onAssigned={reload} />
    </div>
  );
}

function AssignDialog({
  open,
  onClose,
  policyId,
  targets,
  windows,
  onAssigned,
}: {
  open: boolean;
  onClose: () => void;
  policyId: string;
  targets: TargetOptions;
  windows: MaintenanceWindowDto[];
  onAssigned: () => Promise<void>;
}) {
  const { toast } = useToast();
  const [target, setTarget] = React.useState<TargetDto>({ targetType: "DeviceGroup", targetId: null, isExclusion: false });
  const [mode, setMode] = React.useState<EnforcementMode>("Monitor");
  const [priority, setPriority] = React.useState(100);
  const [windowId, setWindowId] = React.useState("");
  const [removal, setRemoval] = React.useState<RemovalBehavior>("Revert");
  const [busy, setBusy] = React.useState(false);

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Assign policy"
      description="Start in Monitor mode to see drift before enforcing."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button
            loading={busy}
            disabled={target.targetType !== "AllDevices" && !target.targetId}
            onClick={async () => {
              setBusy(true);
              try {
                await api.post(`configuration-policies/${policyId}/assignments`, {
                  ...target,
                  mode,
                  priority,
                  maintenanceWindowId: windowId || null,
                  removalBehavior: removal,
                });
                onClose();
                await onAssigned();
              } catch (e) {
                toast({ tone: "error", title: "Could not assign", description: messageFrom(e) });
              } finally {
                setBusy(false);
              }
            }}
          >
            Assign
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        <TargetPicker value={target} onChange={setTarget} options={targets} idPrefix="assign" />
        {!target.isExclusion ? (
          <div className="grid grid-cols-2 gap-3">
            <Field label="Mode" htmlFor="assign-mode">
              <Select id="assign-mode" value={mode} onChange={(e) => setMode(e.target.value as EnforcementMode)}>
                <option value="Monitor">Monitor (report drift only)</option>
                <option value="Enforce">Enforce (apply and remediate)</option>
              </Select>
            </Field>
            <Field label="Priority (1 = highest)" htmlFor="assign-priority">
              <NumberInput id="assign-priority" min={1} max={1000} value={priority} onValue={setPriority} />
            </Field>
            <Field label="Apply only in window" htmlFor="assign-window">
              <Select id="assign-window" value={windowId} onChange={(e) => setWindowId(e.target.value)}>
                <option value="">Any time</option>
                {windows.map((w) => (
                  <option key={w.id} value={w.id}>
                    {w.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="When removed" htmlFor="assign-removal">
              <Select id="assign-removal" value={removal} onChange={(e) => setRemoval(e.target.value as RemovalBehavior)}>
                <option value="Revert">Restore previous values</option>
                <option value="Leave">Keep values</option>
              </Select>
            </Field>
          </div>
        ) : null}
      </div>
    </Dialog>
  );
}
