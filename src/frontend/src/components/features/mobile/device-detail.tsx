"use client";

import * as React from "react";
import Link from "next/link";
import { ShieldAlert, ShieldCheck } from "lucide-react";
import { api } from "@/lib/api/client";
import type { DeviceDetailDto, DeviceDto, PolicyDto } from "@/lib/api/mdm-types";
import type { PagedResult } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { deviceTitle, formatDate, modeLabel, platformLabel } from "@/lib/mdm";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Field } from "@/components/ui/field";
import { Select } from "@/components/ui/input";
import { Dialog } from "@/components/ui/dialog";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { CommandStateBadge, ComplianceBadge, OwnershipBadge, StateBadge } from "./badges";
import { CommandDialog } from "./command-dialog";

function yesNo(v: boolean | null): string {
  return v === null ? "Not reported" : v ? "Yes" : "No";
}

function Fact({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <dt className="text-xs uppercase tracking-wide text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 text-sm">{children}</dd>
    </div>
  );
}

export function DeviceDetail({ initial }: { initial: DeviceDetailDto }) {
  const { toast } = useToast();
  const { isAdmin } = useCan();
  const [detail, setDetail] = React.useState(initial);
  const [commandOpen, setCommandOpen] = React.useState(false);
  const [policyOpen, setPolicyOpen] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const { device, capabilities } = detail;
  const active = device.state === "Enrolled";
  const personal = device.ownership === "PersonallyOwned";

  const reload = React.useCallback(async () => {
    setDetail(await api.get<DeviceDetailDto>(`mobile-devices/${device.id}`));
  }, [device.id]);

  async function evaluate() {
    setBusy(true);
    try {
      setDetail(await api.post<DeviceDetailDto>(`mobile-devices/${device.id}/evaluate-compliance`));
      toast({ tone: "success", title: "Compliance re-evaluated" });
    } catch (e) {
      toast({ tone: "error", title: "Could not evaluate", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="text-2xl font-semibold tracking-tight">{deviceTitle(device)}</h1>
            <StateBadge value={device.state} />
            <ComplianceBadge value={device.compliance} />
            <OwnershipBadge value={device.ownership} />
          </div>
          <p className="mt-1 text-sm text-muted-foreground">
            {platformLabel[device.platform]} · {modeLabel[device.mode]}
          </p>
        </div>
        {isAdmin && active ? (
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" onClick={evaluate} loading={busy}>Re-evaluate</Button>
            <Button variant="outline" onClick={() => setPolicyOpen(true)}>Change policy</Button>
            <Button onClick={() => setCommandOpen(true)}>Send command…</Button>
          </div>
        ) : null}
      </div>

      {device.state === "RemovalPending" ? (
        <Alert tone="info" title="Removal in progress">A retire or wipe was approved and is waiting for the device or platform to complete it.</Alert>
      ) : null}
      {personal ? (
        <Alert tone="info" title="Personal device">
          Only work data is managed. Serial numbers and personal apps are not collected, and a full wipe is not available.
        </Alert>
      ) : null}

      <div className="grid gap-6 lg:grid-cols-3">
        <Card className="lg:col-span-2">
          <CardHeader><CardTitle>Inventory</CardTitle></CardHeader>
          <CardContent>
            <dl className="grid grid-cols-2 gap-4 sm:grid-cols-3">
              <Fact label="Manufacturer">{device.manufacturer ?? "—"}</Fact>
              <Fact label="Model">{device.model ?? "—"}</Fact>
              <Fact label="Serial number">{device.serialNumber ?? (personal ? "Not collected (BYOD)" : "—")}</Fact>
              <Fact label="OS version">{device.osVersion ?? "—"}</Fact>
              <Fact label="Security patch">{device.securityPatchLevel ?? "—"}</Fact>
              <Fact label="Supervised">{yesNo(device.isSupervised)}</Fact>
              <Fact label="Encrypted">{yesNo(device.isEncrypted)}</Fact>
              <Fact label="Passcode set">{yesNo(device.hasPasscode)}</Fact>
              <Fact label="Integrity">{device.integrity}</Fact>
              <Fact label="Enrolled">{formatDate(device.enrolledAtUtc)}</Fact>
              <Fact label="Last seen">{formatDate(device.lastSeenAtUtc)}</Fact>
              <Fact label="Policy version applied">{device.appliedPolicyVersion ?? "—"}</Fact>
            </dl>
          </CardContent>
        </Card>

        <Card>
          <CardHeader><CardTitle>Compliance</CardTitle></CardHeader>
          <CardContent className="space-y-3">
            <div className="flex items-center gap-2">
              {device.compliance === "NonCompliant" ? <ShieldAlert className="h-5 w-5 text-destructive" /> : <ShieldCheck className="h-5 w-5 text-success" />}
              <ComplianceBadge value={device.compliance} />
              <span className="text-xs text-muted-foreground">{formatDate(device.complianceEvaluatedAtUtc)}</span>
            </div>
            {detail.violations.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                {device.compliance === "Compliant" ? "All requirements are met." : "No violations recorded."}
              </p>
            ) : (
              <ul className="space-y-2">
                {detail.violations.map((v) => (
                  <li key={v.rule} className="rounded-md border border-destructive/30 bg-destructive/5 p-2 text-sm">
                    <span className="font-mono text-xs text-muted-foreground">{v.rule}</span>
                    <p>{v.message}</p>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader><CardTitle>What this device allows</CardTitle></CardHeader>
        <CardContent>
          <div className="flex flex-wrap gap-2">
            {capabilities.commands.map((c) => (
              <Badge key={c} tone={capabilities.commandsRequiringApproval.includes(c) ? "warning" : "neutral"}>
                {c}{capabilities.commandsRequiringApproval.includes(c) ? " · needs approval" : ""}
              </Badge>
            ))}
          </div>
          <ul className="mt-3 list-disc space-y-1 pl-5 text-sm text-muted-foreground">
            {capabilities.limitations.map((l) => (
              <li key={l}>{l}</li>
            ))}
          </ul>
        </CardContent>
      </Card>

      <div className="grid gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader><CardTitle>Commands</CardTitle></CardHeader>
          <Table>
            <THead><TR><TH>Command</TH><TH>State</TH><TH>Requested</TH></TR></THead>
            <TBody>
              {detail.commands.length === 0 ? (
                <EmptyRow colSpan={3}>No commands yet.</EmptyRow>
              ) : (
                detail.commands.map((c) => (
                  <TR key={c.id}>
                    <TD>
                      <p className="font-medium">{c.type}</p>
                      {c.reason ? <p className="text-xs text-muted-foreground">{c.reason}</p> : null}
                      {c.error ? <p className="text-xs text-destructive">{c.error}</p> : null}
                    </TD>
                    <TD><CommandStateBadge value={c.state} /></TD>
                    <TD className="text-xs text-muted-foreground">{formatDate(c.createdAtUtc)}</TD>
                  </TR>
                ))
              )}
            </TBody>
          </Table>
          {detail.commands.some((c) => c.state === "PendingApproval") ? (
            <p className="border-t border-border p-3 text-xs text-muted-foreground">
              Pending commands are decided on the <Link href="/mobile/approvals" className="underline">approvals</Link> page.
            </p>
          ) : null}
        </Card>

        <Card>
          <CardHeader><CardTitle>Applications ({detail.apps.length})</CardTitle></CardHeader>
          <div className="max-h-80 overflow-y-auto">
            <Table>
              <THead><TR><TH>App</TH><TH>Version</TH></TR></THead>
              <TBody>
                {detail.apps.length === 0 ? (
                  <EmptyRow colSpan={2}>{personal ? "Only managed apps are listed for personal devices." : "No application inventory reported yet."}</EmptyRow>
                ) : (
                  detail.apps.map((a) => (
                    <TR key={a.identifier}>
                      <TD>
                        <p className="font-medium">{a.name ?? a.identifier}</p>
                        <p className="font-mono text-xs text-muted-foreground">{a.identifier}</p>
                      </TD>
                      <TD className="text-muted-foreground">{a.version ?? "—"}</TD>
                    </TR>
                  ))
                )}
              </TBody>
            </Table>
          </div>
        </Card>
      </div>

      <Card>
        <CardHeader><CardTitle>Audit trail</CardTitle></CardHeader>
        <Table>
          <THead><TR><TH>When</TH><TH>Event</TH><TH>Details</TH></TR></THead>
          <TBody>
            {detail.events.length === 0 ? (
              <EmptyRow colSpan={3}>No events.</EmptyRow>
            ) : (
              detail.events.map((e) => (
                <TR key={e.id}>
                  <TD className="whitespace-nowrap text-xs text-muted-foreground">{formatDate(e.createdAtUtc)}</TD>
                  <TD><Badge>{e.type}</Badge></TD>
                  <TD className="text-sm">{e.summary}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>

      {commandOpen ? (
        <CommandDialog
          device={device}
          capabilities={capabilities}
          onClose={() => setCommandOpen(false)}
          onDone={async () => {
            setCommandOpen(false);
            await reload();
          }}
        />
      ) : null}
      {policyOpen ? (
        <AssignPolicyDialog
          device={device}
          onClose={() => setPolicyOpen(false)}
          onDone={async () => {
            setPolicyOpen(false);
            toast({ tone: "success", title: "Policy assigned" });
            await reload();
          }}
        />
      ) : null}
    </div>
  );
}

function AssignPolicyDialog({ device, onClose, onDone }: { device: DeviceDto; onClose: () => void; onDone: () => Promise<void> }) {
  const [policies, setPolicies] = React.useState<PolicyDto[] | null>(null);
  const [selected, setSelected] = React.useState(device.policyId ?? "");
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  React.useEffect(() => {
    api
      .get<PagedResult<PolicyDto>>("mobile-policies", { platform: device.platform, pageSize: 200 })
      .then((r) => setPolicies(r.items.filter((p) => p.mode === device.mode && p.ownership === device.ownership)))
      .catch((e) => setError(messageFrom(e)));
  }, [device.platform, device.mode, device.ownership]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      await api.put(`mobile-devices/${device.id}/policy`, { policyId: selected || null });
      await onDone();
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="Change policy"
      description="Only policies written for this device's platform, mode and ownership are offered."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="assign-policy-form" loading={pending}>Assign</Button>
        </>
      }
    >
      <form id="assign-policy-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <Field label="Policy" htmlFor="assign-policy">
          <Select id="assign-policy" value={selected} onChange={(e) => setSelected(e.target.value)} disabled={policies === null}>
            <option value="">No policy</option>
            {(policies ?? []).map((p) => (
              <option key={p.id} value={p.id}>{p.name} (v{p.version})</option>
            ))}
          </Select>
        </Field>
      </form>
    </Dialog>
  );
}
