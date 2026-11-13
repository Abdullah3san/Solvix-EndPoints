"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { api } from "@/lib/api/client";
import type { BranchDto, CreateDeploymentRequest, DeploymentDetailDto, DeploymentGroupDto, DeploymentPreviewDto, DeviceGroupDto, PackageDto, PagedResult, DeviceSummaryDto } from "@/lib/api/types";
import { emptyToNull, messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Select, Textarea } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD } from "@/components/ui/table";
import { sensitivityLabels } from "./labels";

const toggle = (list: string[], id: string) => (list.includes(id) ? list.filter((x) => x !== id) : [...list, id]);

function CheckList({ title, items, selected, onChange }: { title: string; items: { id: string; label: string }[]; selected: string[]; onChange: (v: string[]) => void }) {
  return (
    <div>
      <div className="mb-1 text-sm font-medium">{title}</div>
      <div className="max-h-44 space-y-1 overflow-auto rounded-md border p-2">
        {items.length === 0 ? <p className="text-sm text-muted-foreground">None available.</p> : null}
        {items.map((i) => (
          <label key={i.id} className="flex items-center gap-2 text-sm">
            <Checkbox checked={selected.includes(i.id)} onChange={() => onChange(toggle(selected, i.id))} /> {i.label}
          </label>
        ))}
      </div>
    </div>
  );
}

export function NewDeploymentView({
  packages,
  groups,
  deviceGroups,
  branches,
  initialPackageId,
}: {
  packages: PackageDto[];
  groups: DeploymentGroupDto[];
  deviceGroups: DeviceGroupDto[];
  branches: BranchDto[];
  initialPackageId?: string;
}) {
  const router = useRouter();
  const [name, setName] = React.useState("");
  const [reason, setReason] = React.useState("");
  const [packageId, setPackageId] = React.useState(packages.find((p) => p.id === initialPackageId)?.id ?? packages[0]?.id ?? "");
  const [action, setAction] = React.useState<"Install" | "Uninstall">("Install");
  const [groupId, setGroupId] = React.useState("");
  const [deviceGroupIds, setDeviceGroupIds] = React.useState<string[]>([]);
  const [branchIds, setBranchIds] = React.useState<string[]>([]);
  const [deviceIds, setDeviceIds] = React.useState<string[]>([]);
  const [devices, setDevices] = React.useState<DeviceSummaryDto[]>([]);
  const [deviceSearch, setDeviceSearch] = React.useState("");
  const [startAt, setStartAt] = React.useState("");
  const [deadlineAt, setDeadlineAt] = React.useState("");
  const [maxAttempts, setMaxAttempts] = React.useState(3);
  const [skipConflicts, setSkipConflicts] = React.useState(false);
  const [preview, setPreview] = React.useState<DeploymentPreviewDto | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  React.useEffect(() => {
    let cancelled = false;
    api.get<PagedResult<DeviceSummaryDto>>("devices", { search: deviceSearch, pageSize: 25, status: "Active" }).then((r) => !cancelled && setDevices(r.items), () => undefined);
    return () => {
      cancelled = true;
    };
  }, [deviceSearch]);

  const request = (): CreateDeploymentRequest => ({
    name,
    reason: emptyToNull(reason),
    packageId,
    action,
    deploymentGroupId: groupId || null,
    deviceIds,
    deviceGroupIds,
    branchIds,
    scheduledStartUtc: startAt ? new Date(startAt).toISOString() : null,
    deadlineUtc: deadlineAt ? new Date(deadlineAt).toISOString() : null,
    maxAttempts,
    retryDelayMinutes: 15,
    skipConflicts,
  });

  async function review(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      setPreview(await api.post<DeploymentPreviewDto>("software/deployments/preview", request()));
    } catch (e) {
      setPreview(null);
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  async function submit() {
    setPending(true);
    setError(null);
    try {
      const created = await api.post<DeploymentDetailDto>("software/deployments", request());
      router.push(`/software/deployments/${created.deployment.id}`);
    } catch (e) {
      setError(messageFrom(e));
      setPending(false);
    }
  }

  if (packages.length === 0) return <Alert tone="info">There is no configured package to deploy yet. Upload and configure one first.</Alert>;

  return (
    <div className="space-y-6">
      <form onSubmit={review} className="space-y-4" onChange={() => setPreview(null)}>
        <Card>
          <CardHeader><CardTitle>1 · Package and action</CardTitle></CardHeader>
          <CardContent className="grid gap-4 sm:grid-cols-2">
            <Field label="Deployment name" htmlFor="d-name" required><Input id="d-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} /></Field>
            <Field label="Package" htmlFor="d-pkg">
              <Select id="d-pkg" value={packageId} onChange={(e) => setPackageId(e.target.value)}>
                {packages.map((p) => <option key={p.id} value={p.id}>{p.name} {p.version ?? ""} ({p.installerType})</option>)}
              </Select>
            </Field>
            <Field label="Action" htmlFor="d-action">
              <Select id="d-action" value={action} onChange={(e) => setAction(e.target.value as "Install" | "Uninstall")}>
                <option value="Install">Install / update</option>
                <option value="Uninstall">Uninstall</option>
              </Select>
            </Field>
            <Field label="Reason (shown to approvers)" htmlFor="d-reason"><Textarea id="d-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} /></Field>
          </CardContent>
        </Card>

        <Card>
          <CardHeader><CardTitle>2 · Authorized devices</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <Field label="Deployment group" htmlFor="d-group">
              <Select id="d-group" value={groupId} onChange={(e) => setGroupId(e.target.value)}>
                <option value="">None</option>
                {groups.map((g) => <option key={g.id} value={g.id}>{g.name} ({g.resolvedDeviceCount} devices)</option>)}
              </Select>
            </Field>
            <div className="grid gap-4 md:grid-cols-3">
              <CheckList title="Device groups" items={deviceGroups.map((g) => ({ id: g.id, label: `${g.name} (${g.deviceCount})` }))} selected={deviceGroupIds} onChange={setDeviceGroupIds} />
              <CheckList title="Branches" items={branches.map((b) => ({ id: b.id, label: b.name }))} selected={branchIds} onChange={setBranchIds} />
              <div>
                <Input aria-label="Search devices" placeholder="Search devices…" value={deviceSearch} onChange={(e) => setDeviceSearch(e.target.value)} className="mb-1" />
                <CheckList title="Individual devices" items={devices.map((d) => ({ id: d.id, label: d.hostname }))} selected={deviceIds} onChange={setDeviceIds} />
              </div>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader><CardTitle>3 · Schedule and retries</CardTitle></CardHeader>
          <CardContent className="grid gap-4 sm:grid-cols-3">
            <Field label="Start (empty: when approved)" htmlFor="d-start"><Input id="d-start" type="datetime-local" value={startAt} onChange={(e) => setStartAt(e.target.value)} /></Field>
            <Field label="Deadline (empty: 7 days)" htmlFor="d-deadline"><Input id="d-deadline" type="datetime-local" value={deadlineAt} onChange={(e) => setDeadlineAt(e.target.value)} /></Field>
            <Field label="Attempts per device" htmlFor="d-attempts"><Input id="d-attempts" type="number" min={1} max={5} value={maxAttempts} onChange={(e) => setMaxAttempts(Number(e.target.value))} /></Field>
            <label className="flex items-center gap-2 text-sm sm:col-span-3"><Checkbox checked={skipConflicts} onChange={(e) => setSkipConflicts(e.target.checked)} /> Skip devices that already have an unfinished deployment of this package</label>
          </CardContent>
        </Card>

        {error ? <Alert tone="error">{error}</Alert> : null}
        <Button type="submit" loading={pending && !preview}>Review deployment</Button>
      </form>

      {preview ? (
        <Card>
          <CardHeader><CardTitle>4 · Review</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <dl className="grid gap-2 text-sm sm:grid-cols-2">
              <div><dt className="text-muted-foreground">Package</dt><dd>{preview.packageName} {preview.packageVersion ?? ""} — <span className="font-mono text-xs">{preview.packageSha256.slice(0, 16)}…</span></dd></div>
              <div><dt className="text-muted-foreground">Command</dt><dd>{preview.action} ({preview.installerType}){preview.requireSignature ? " · signature required on the device" : ""}</dd></div>
              <div><dt className="text-muted-foreground">Arguments</dt><dd className="font-mono text-xs">{preview.arguments.length ? preview.arguments.join(" ") : "none"}</dd></div>
              <div><dt className="text-muted-foreground">Window</dt><dd>{formatDate(preview.scheduledStartUtc)} → {formatDate(preview.deadlineUtc)}, up to {preview.maxAttempts} attempts</dd></div>
            </dl>
            <Alert tone="info" title={preview.isSensitive ? "Sensitive deployment" : undefined}>
              {preview.requiredApprovals === 0 ? "No approval is required by your policy." : `${preview.requiredApprovals} approval${preview.requiredApprovals > 1 ? "s" : ""} by ${preview.requiredApprovals > 1 ? "different people other than you" : "someone other than you"} required.`}
              {preview.isSensitive ? <span className="mt-1 block">Sensitive because: {preview.sensitivityReasons.map((r) => sensitivityLabels[r] ?? r).join("; ")}.</span> : null}
            </Alert>
            <Table>
              <THead><TR><TH>Device</TH><TH>OS</TH><TH>Connectivity</TH><TH>Outcome</TH></TR></THead>
              <TBody>
                {preview.targets.map((t) => (
                  <TR key={t.deviceId}>
                    <TD>{t.deviceName}</TD>
                    <TD>{t.osName ?? "—"}</TD>
                    <TD>{t.connectivity}</TD>
                    <TD>{t.willRun ? <Badge tone="success">Will run</Badge> : <Badge tone="warning">Skipped: {t.skipReason?.replace(/_/g, " ")}</Badge>}</TD>
                  </TR>
                ))}
              </TBody>
            </Table>
            {error ? <Alert tone="error">{error}</Alert> : null}
            <Button onClick={submit} loading={pending} disabled={preview.deployableCount === 0}>
              Submit {preview.deployableCount} device{preview.deployableCount === 1 ? "" : "s"} for approval
            </Button>
          </CardContent>
        </Card>
      ) : null}
    </div>
  );
}
