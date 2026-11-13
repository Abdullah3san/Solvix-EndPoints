"use client";

import * as React from "react";
import { ChevronDown, ChevronRight, Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import type { PagedResult } from "@/lib/api/types";
import type { DeploymentDto, DeploymentJobDto, MaintenanceWindowDto, PatchDto, RestartBehavior, TargetDto } from "@/lib/api/endpoint-types";
import { deploymentActions, deploymentTone } from "@/lib/api/endpoint-types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Select } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { ConfirmDialog, NumberInput, ProgressBar, TargetPicker, describeTarget, type TargetOptions } from "./common";

const actionLabels: Record<string, string> = {
  schedule: "Authorise & schedule",
  unschedule: "Back to draft",
  promote: "Promote to all targets",
  "force-promote": "Override failed pilot",
  pause: "Pause",
  resume: "Resume",
  cancel: "Cancel",
};

export function DeploymentsView({
  initial,
  approvedPatches,
  windows,
  targets,
}: {
  initial: PagedResult<DeploymentDto>;
  approvedPatches: PatchDto[];
  windows: MaintenanceWindowDto[];
  targets: TargetOptions;
}) {
  const { toast } = useToast();
  const { isAdmin, isOwner } = useCan();
  const [data, setData] = React.useState(initial);
  const [expanded, setExpanded] = React.useState<string | null>(null);
  const [jobs, setJobs] = React.useState<DeploymentJobDto[]>([]);
  const [creating, setCreating] = React.useState(false);
  const [pending, setPending] = React.useState<{ deployment: DeploymentDto; action: string } | null>(null);
  const [busy, setBusy] = React.useState(false);

  const reload = React.useCallback(async () => {
    setData(await api.get<PagedResult<DeploymentDto>>("patch-deployments", { pageSize: 50 }));
  }, []);

  async function toggleJobs(id: string) {
    if (expanded === id) {
      setExpanded(null);
      return;
    }

    setExpanded(id);
    setJobs((await api.get<PagedResult<DeploymentJobDto>>(`patch-deployments/${id}/devices`, { pageSize: 200 })).items);
  }

  async function run(deployment: DeploymentDto, action: string, reason: string) {
    setBusy(true);
    try {
      if (action === "schedule" || action === "unschedule") {
        await api.post(`patch-deployments/${deployment.id}/${action}`);
      } else {
        const endpoint = action === "force-promote" ? "promote" : action;
        await api.post(`patch-deployments/${deployment.id}/${endpoint}`, { reason: reason || null, force: action === "force-promote" });
      }

      toast({ tone: "success", title: `${deployment.name}: ${actionLabels[action]}` });
      setPending(null);
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Action failed", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-4">
      {isAdmin ? (
        <div className="flex justify-end">
          <Button onClick={() => setCreating(true)} disabled={approvedPatches.length === 0}>
            <Plus className="h-4 w-4" /> New deployment
          </Button>
        </div>
      ) : null}
      {approvedPatches.length === 0 ? <Alert tone="info">Approve patches on the Patches page before creating a deployment.</Alert> : null}

      <Card>
        <Table>
          <THead>
            <TR>
              <TH className="w-8" />
              <TH>Deployment</TH>
              <TH>Status</TH>
              <TH>Targets</TH>
              <TH className="w-56">Progress</TH>
              {isAdmin ? <TH className="text-right">Actions</TH> : null}
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={6}>No deployments yet.</EmptyRow>
            ) : (
              data.items.map((d) => (
                <React.Fragment key={d.id}>
                  <TR>
                    <TD>
                      <Button variant="ghost" size="icon" aria-label="Show devices" onClick={() => void toggleJobs(d.id)}>
                        {expanded === d.id ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
                      </Button>
                    </TD>
                    <TD>
                      <span className="font-medium">{d.name}</span>
                      <span className="block text-xs text-muted-foreground">
                        {d.patchIds.length} patch(es) · starts {new Date(d.scheduledStartUtc).toLocaleString()}
                        {d.deadlineUtc ? ` · deadline ${new Date(d.deadlineUtc).toLocaleString()}` : ""}
                      </span>
                      {d.haltReason ? <span className="block text-xs text-destructive">{d.haltReason}</span> : null}
                    </TD>
                    <TD>
                      <Badge tone={deploymentTone[d.status]}>{d.status}</Badge>
                      {d.phase !== "NotStarted" ? <span className="ml-1 text-xs text-muted-foreground">{d.phase}</span> : null}
                    </TD>
                    <TD className="text-sm text-muted-foreground">{d.targets.map((t) => describeTarget(t, targets)).join(", ")}</TD>
                    <TD>
                      <ProgressBar
                        label={`${d.progress.succeeded} succeeded, ${d.progress.failed} failed of ${d.progress.devices}`}
                        segments={[
                          { value: d.progress.succeeded, className: "bg-success", title: "Succeeded" },
                          { value: d.progress.pendingRestart, className: "bg-warning", title: "Pending restart" },
                          { value: d.progress.inFlight, className: "bg-primary", title: "Installing" },
                          { value: d.progress.failed + d.progress.timedOut, className: "bg-destructive", title: "Failed" },
                          { value: d.progress.pending + d.progress.notApplicable + d.progress.cancelled, className: "bg-muted-foreground/30", title: "Other" },
                        ]}
                      />
                      <span className="mt-1 block text-xs text-muted-foreground">
                        {d.progress.succeeded}/{d.progress.devices} done
                        {d.progress.pilotSuccessPercent !== null ? ` · pilot ${d.progress.pilotSuccessPercent}%` : ""}
                      </span>
                    </TD>
                    {isAdmin ? (
                      <TD>
                        <div className="flex flex-wrap justify-end gap-1">
                          {deploymentActions(d.status, d.phase)
                            .filter((a) => a !== "force-promote" || isOwner)
                            .map((action) => (
                              <Button key={action} size="sm" variant={action === "cancel" ? "outline" : action === "force-promote" ? "destructive" : "secondary"} onClick={() => setPending({ deployment: d, action })}>
                                {actionLabels[action]}
                              </Button>
                            ))}
                        </div>
                      </TD>
                    ) : null}
                  </TR>
                  {expanded === d.id ? (
                    <TR>
                      <TD colSpan={6} className="bg-muted/30">
                        <Table>
                          <THead>
                            <TR>
                              <TH>Device</TH>
                              <TH>Ring</TH>
                              <TH>Status</TH>
                              <TH>Attempt</TH>
                              <TH>Details</TH>
                            </TR>
                          </THead>
                          <TBody>
                            {jobs.length === 0 ? (
                              <EmptyRow colSpan={5}>Devices are assigned when the deployment starts.</EmptyRow>
                            ) : (
                              jobs.map((j) => (
                                <TR key={j.id}>
                                  <TD>{j.hostname}</TD>
                                  <TD>{j.ring}</TD>
                                  <TD>
                                    {j.status}
                                    {j.stage ? ` · ${j.stage} ${j.percentComplete}%` : ""}
                                  </TD>
                                  <TD>
                                    {j.attempt} ({j.failureCount} failed)
                                  </TD>
                                  <TD className="text-xs text-muted-foreground">
                                    {j.lastError ?? j.message ?? "—"} {j.lastHResultHex ? <span className="font-mono">{j.lastHResultHex}</span> : null}
                                  </TD>
                                </TR>
                              ))
                            )}
                          </TBody>
                        </Table>
                      </TD>
                    </TR>
                  ) : null}
                </React.Fragment>
              ))
            )}
          </TBody>
        </Table>
      </Card>

      <CreateDeploymentDialog open={creating} onClose={() => setCreating(false)} patches={approvedPatches} windows={windows} targets={targets} onCreated={reload} />
      <ConfirmDialog
        key={pending ? `${pending.deployment.id}-${pending.action}` : "none"}
        open={pending !== null}
        title={pending ? `${actionLabels[pending.action]}: ${pending.deployment.name}` : ""}
        description={
          pending?.action === "schedule"
            ? "This authorises Solvix to install the selected patches on the targeted devices (pilot ring first, inside the maintenance window)."
            : pending?.action === "force-promote"
              ? "The pilot did not meet its success threshold. Rolling out anyway is recorded with your reason."
              : undefined
        }
        confirmLabel={pending ? actionLabels[pending.action] : "Confirm"}
        requireReason={pending?.action === "force-promote"}
        destructive={pending?.action === "force-promote" || pending?.action === "cancel"}
        busy={busy}
        onClose={() => setPending(null)}
        onConfirm={(reason) => pending && void run(pending.deployment, pending.action, reason)}
      />
    </div>
  );
}

function localInput(date: Date): string {
  const offset = date.getTimezoneOffset() * 60000;
  return new Date(date.getTime() - offset).toISOString().slice(0, 16);
}

function CreateDeploymentDialog({
  open,
  onClose,
  patches,
  windows,
  targets,
  onCreated,
}: {
  open: boolean;
  onClose: () => void;
  patches: PatchDto[];
  windows: MaintenanceWindowDto[];
  targets: TargetOptions;
  onCreated: () => Promise<void>;
}) {
  const { toast } = useToast();
  const [name, setName] = React.useState("Monthly security updates");
  const [patchIds, setPatchIds] = React.useState<Set<string>>(new Set());
  const [targetList, setTargetList] = React.useState<TargetDto[]>([{ targetType: "AllDevices", targetId: null, isExclusion: false }]);
  const [start, setStart] = React.useState(() => localInput(new Date()));
  const [deadline, setDeadline] = React.useState(() => localInput(new Date(Date.now() + 7 * 86400000)));
  const [windowId, setWindowId] = React.useState("");
  const [restart, setRestart] = React.useState<RestartBehavior>("WithinMaintenanceWindow");
  const [pilotGroup, setPilotGroup] = React.useState("");
  const [pilotPercent, setPilotPercent] = React.useState(10);
  const [threshold, setThreshold] = React.useState(90);
  const [failureThreshold, setFailureThreshold] = React.useState(20);
  const [autoPromote, setAutoPromote] = React.useState(false);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [busy, setBusy] = React.useState(false);

  async function submit() {
    setBusy(true);
    setErrors({});
    try {
      await api.post("patch-deployments", {
        name,
        description: null,
        patchIds: [...patchIds],
        targets: targetList,
        scheduledStartUtc: new Date(start).toISOString(),
        deadlineUtc: deadline ? new Date(deadline).toISOString() : null,
        maintenanceWindowId: windowId || null,
        restartBehavior: restart,
        restartGracePeriodSeconds: 600,
        pilotDeviceGroupId: pilotGroup && pilotGroup !== "percent" ? pilotGroup : null,
        pilotPercentage: pilotGroup === "percent" ? pilotPercent : null,
        pilotSuccessThresholdPercent: threshold,
        pilotSoakMinutes: autoPromote ? 60 : 0,
        autoPromote,
        failureThresholdPercent: failureThreshold,
        maxAttempts: 3,
      });
      toast({ tone: "success", title: "Deployment drafted", description: "Review it and authorise it to start." });
      onClose();
      await onCreated();
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      toast({ tone: "error", title: "Could not create deployment", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="New patch deployment"
      className="max-w-3xl"
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={submit} loading={busy} disabled={patchIds.size === 0}>
            Save draft
          </Button>
        </>
      }
    >
      <div className="max-h-[70vh] space-y-4 overflow-y-auto pr-1">
        <Field label="Name" htmlFor="dep-name" error={errors.name} required>
          <Input id="dep-name" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <fieldset>
          <legend className="mb-1 text-sm font-medium">Approved patches</legend>
          <div className="max-h-40 space-y-1 overflow-y-auto rounded-md border border-border p-2">
            {patches.map((p) => (
              <label key={p.id} className="flex items-center gap-2 text-sm">
                <Checkbox
                  checked={patchIds.has(p.id)}
                  onChange={(e) =>
                    setPatchIds((s) => {
                      const next = new Set(s);
                      if (e.target.checked) next.add(p.id);
                      else next.delete(p.id);
                      return next;
                    })
                  }
                />
                <span className="truncate">{p.title}</span>
                <span className="ml-auto text-xs text-muted-foreground">{p.devicesMissing} missing</span>
              </label>
            ))}
          </div>
        </fieldset>
        <fieldset className="space-y-2">
          <legend className="text-sm font-medium">Targets</legend>
          {targetList.map((t, i) => (
            <div key={i} className="flex items-end gap-2">
              <div className="flex-1">
                <TargetPicker value={t} options={targets} idPrefix={`dep-target-${i}`} onChange={(v) => setTargetList((list) => list.map((x, j) => (j === i ? v : x)))} />
              </div>
              {targetList.length > 1 ? (
                <Button variant="ghost" size="icon" aria-label="Remove target" onClick={() => setTargetList((list) => list.filter((_, j) => j !== i))}>
                  <Trash2 className="h-4 w-4" />
                </Button>
              ) : null}
            </div>
          ))}
          <Button variant="outline" size="sm" onClick={() => setTargetList((list) => [...list, { targetType: "DeviceGroup", targetId: null, isExclusion: false }])}>
            <Plus className="h-4 w-4" /> Add target
          </Button>
        </fieldset>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Start" htmlFor="dep-start">
            <Input id="dep-start" type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} />
          </Field>
          <Field label="Deadline" htmlFor="dep-deadline" error={errors.deadlineUtc}>
            <Input id="dep-deadline" type="datetime-local" value={deadline} onChange={(e) => setDeadline(e.target.value)} />
          </Field>
          <Field label="Maintenance window" htmlFor="dep-window">
            <Select id="dep-window" value={windowId} onChange={(e) => setWindowId(e.target.value)}>
              <option value="">Any time</option>
              {windows.map((w) => (
                <option key={w.id} value={w.id}>
                  {w.name}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Restarts" htmlFor="dep-restart">
            <Select id="dep-restart" value={restart} onChange={(e) => setRestart(e.target.value as RestartBehavior)}>
              <option value="WithinMaintenanceWindow">Inside the maintenance window</option>
              <option value="ForceAtDeadline">Inside the window, forced at deadline (Owner)</option>
              <option value="Never">Never restart automatically</option>
            </Select>
          </Field>
          <Field label="Pilot ring" htmlFor="dep-pilot">
            <Select id="dep-pilot" value={pilotGroup} onChange={(e) => setPilotGroup(e.target.value)}>
              <option value="">No pilot (Owner approval needed for all devices)</option>
              <option value="percent">Random sample of targets</option>
              {targets.groups.map((g) => (
                <option key={g.id} value={g.id}>
                  Group: {g.name}
                </option>
              ))}
            </Select>
          </Field>
          {pilotGroup === "percent" ? (
            <Field label="Pilot size (%)" htmlFor="dep-pilot-pct">
              <NumberInput id="dep-pilot-pct" min={1} max={100} value={pilotPercent} onValue={setPilotPercent} />
            </Field>
          ) : (
            <div />
          )}
          <Field label="Pilot success needed (%)" htmlFor="dep-threshold">
            <NumberInput id="dep-threshold" min={1} max={100} value={threshold} onValue={setThreshold} />
          </Field>
          <Field label="Halt at failure rate (%)" htmlFor="dep-failure">
            <NumberInput id="dep-failure" min={1} max={100} value={failureThreshold} onValue={setFailureThreshold} />
          </Field>
        </div>
        <label className="flex items-center gap-2 text-sm">
          <Checkbox checked={autoPromote} onChange={(e) => setAutoPromote(e.target.checked)} />
          Promote automatically 60 minutes after a successful pilot
        </label>
      </div>
    </Dialog>
  );
}
