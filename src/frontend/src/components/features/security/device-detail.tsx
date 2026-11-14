"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { KeyRound, ShieldOff, Trash2, Wrench } from "lucide-react";
import { api } from "@/lib/api/client";
import type { CheckResult, DeviceFacts, EndpointDetail, EndpointHistoryPoint, Enrollment, Remediation } from "@/lib/api/security-types";
import { messageFrom } from "@/lib/forms";
import { humanize, outcomeLabel, outcomeTone, timeAgo } from "@/lib/security";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Textarea } from "@/components/ui/input";
import { useCan } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";
import { LoadState, ScoreBadge, SeverityBadge, useResource } from "./security-ui";

function yesNo(value: boolean | null | undefined, good = true): React.ReactNode {
  if (value === null || value === undefined) return <Badge>Unknown</Badge>;
  return <Badge tone={value === good ? "success" : "danger"}>{value ? "Yes" : "No"}</Badge>;
}

function FactRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-3 py-1.5 text-sm">
      <dt className="text-muted-foreground">{label}</dt>
      <dd>{children}</dd>
    </div>
  );
}

function Facts({ facts }: { facts: DeviceFacts }) {
  const d = facts.defender;
  return (
    <div className="grid gap-4 md:grid-cols-2">
      <Card>
        <CardHeader>
          <CardTitle>Microsoft Defender</CardTitle>
          <CardDescription>As reported by Defender itself.</CardDescription>
        </CardHeader>
        <CardContent>
          {d ? (
            <dl className="divide-y divide-border">
              <FactRow label="Mode">{d.amRunningMode ?? "Unknown"}</FactRow>
              <FactRow label="Antivirus enabled">{yesNo(d.antivirusEnabled)}</FactRow>
              <FactRow label="Real-time protection">{yesNo(d.realTimeProtectionEnabled)}</FactRow>
              <FactRow label="Tamper protection">{yesNo(d.tamperProtected)}</FactRow>
              <FactRow label="Signature age">{d.signatureAgeDays === null || d.signatureAgeDays === undefined ? "Unknown" : `${d.signatureAgeDays} day(s)`}</FactRow>
              <FactRow label="Last quick scan">{d.quickScanAgeDays === null || d.quickScanAgeDays === undefined ? "Unknown" : d.quickScanAgeDays >= 9999 ? "Never" : `${d.quickScanAgeDays} day(s) ago`}</FactRow>
              <FactRow label="Active threats">{d.activeThreatCount ?? "Unknown"}</FactRow>
              <FactRow label="Other antivirus registered">{d.thirdPartyAntivirusRegistered === null || d.thirdPartyAntivirusRegistered === undefined ? "Unknown" : d.thirdPartyAntivirusRegistered ? "Yes" : "No"}</FactRow>
            </dl>
          ) : (
            <p className="text-sm text-muted-foreground">Not collected.</p>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Windows Firewall</CardTitle>
        </CardHeader>
        <CardContent>
          {facts.firewall?.profiles?.length ? (
            <dl className="divide-y divide-border">
              {facts.firewall.profiles.map((p) => (
                <FactRow key={p.name} label={`${p.name} profile`}>
                  <span className="flex items-center gap-2">
                    {yesNo(p.enabled)}
                    <span className="text-xs text-muted-foreground">inbound: {p.defaultInboundAction ?? "?"}</span>
                  </span>
                </FactRow>
              ))}
            </dl>
          ) : (
            <p className="text-sm text-muted-foreground">Not collected.</p>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>BitLocker</CardTitle>
        </CardHeader>
        <CardContent>
          {facts.bitLocker ? (
            facts.bitLocker.available === false ? (
              <p className="text-sm text-muted-foreground">BitLocker is not available on this Windows edition.</p>
            ) : (
              <dl className="divide-y divide-border">
                <FactRow label="TPM present">{yesNo(facts.bitLocker.tpmPresent)}</FactRow>
                {(facts.bitLocker.volumes ?? []).map((v) => (
                  <FactRow key={v.mountPoint} label={`${v.mountPoint} (${v.volumeType ?? "?"})`}>
                    <span className="flex items-center gap-2">
                      {yesNo(v.protectionOn)}
                      <span className="text-xs text-muted-foreground">
                        {v.volumeStatus ?? ""} {v.encryptionPercentage !== null ? `${v.encryptionPercentage}%` : ""}
                      </span>
                    </span>
                  </FactRow>
                ))}
              </dl>
            )
          ) : (
            <p className="text-sm text-muted-foreground">Not collected.</p>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Local administrators</CardTitle>
        </CardHeader>
        <CardContent>
          {facts.localAdmins?.members ? (
            <>
              <ul className="mb-2 divide-y divide-border text-sm">
                {facts.localAdmins.members.map((m) => (
                  <li key={m.sid ?? m.name} className="flex items-center justify-between py-1.5">
                    <span className="break-all">{m.name}</span>
                    <span className="text-xs text-muted-foreground">{m.objectClass ?? ""}</span>
                  </li>
                ))}
              </ul>
              <dl className="divide-y divide-border border-t border-border">
                <FactRow label="Built-in Administrator enabled">{yesNo(facts.localAdmins.builtInAdministratorEnabled, false)}</FactRow>
                <FactRow label="Guest account enabled">{yesNo(facts.localAdmins.guestAccountEnabled, false)}</FactRow>
              </dl>
            </>
          ) : (
            <p className="text-sm text-muted-foreground">Not collected.</p>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>USB storage</CardTitle>
        </CardHeader>
        <CardContent>
          <dl className="divide-y divide-border">
            <FactRow label="Effective restriction">{facts.effectiveUsbPolicy ?? "Could not be determined"}</FactRow>
            <FactRow label="USB storage driver disabled">{yesNo(facts.usb?.storageDriverDisabled, true)}</FactRow>
            <FactRow label="Write access denied (policy)">{yesNo(facts.usb?.denyWrite, true)}</FactRow>
            <FactRow label="Read access denied (policy)">{yesNo(facts.usb?.denyRead, true)}</FactRow>
          </dl>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Security configuration</CardTitle>
        </CardHeader>
        <CardContent>
          <dl className="divide-y divide-border">
            {Object.entries(facts.configuration ?? {}).map(([k, v]) => (
              <FactRow key={k} label={humanize(k)}>
                {v === null ? <Badge>Unknown</Badge> : <Badge tone="neutral">{v ? "Yes" : "No"}</Badge>}
              </FactRow>
            ))}
            <FactRow label="Installed software items">{facts.softwareCount ?? "Unknown"}</FactRow>
          </dl>
        </CardContent>
      </Card>

      {facts.collectionErrors.length > 0 ? (
        <Card className="md:col-span-2">
          <CardHeader>
            <CardTitle>Collection problems</CardTitle>
            <CardDescription>Sections that could not be read are treated as unknown, never as compliant.</CardDescription>
          </CardHeader>
          <CardContent>
            <ul className="space-y-1 text-sm">
              {facts.collectionErrors.map((e, i) => (
                <li key={i}>
                  <span className="font-mono text-xs">{e.section}</span>: {e.message}
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
      ) : null}
    </div>
  );
}

function CheckRow({ check, canRemediate, onRemediate }: { check: CheckResult; canRemediate: boolean; onRemediate: (c: CheckResult) => void }) {
  return (
    <details className="group border-b border-border last:border-0">
      <summary className="flex cursor-pointer list-none flex-wrap items-center gap-2 px-4 py-2.5 text-sm hover:bg-muted/50">
        <Badge tone={outcomeTone(check.outcome)}>{outcomeLabel(check.outcome)}</Badge>
        {check.outcome === "Fail" ? <SeverityBadge severity={check.severity} /> : null}
        <span className="font-medium">{check.title}</span>
        {check.subjectKey && check.ruleKey !== "SW-001" ? <span className="text-xs text-muted-foreground">({check.subjectKey})</span> : null}
        <span className="ml-auto font-mono text-xs text-muted-foreground">{check.ruleKey}</span>
      </summary>
      <div className="space-y-2 bg-muted/30 px-4 py-3 text-sm">
        <p>
          <span className="font-medium">Evidence: </span>
          {check.evidence}
        </p>
        {check.outcome === "Fail" ? (
          <>
            <p>
              <span className="font-medium">Why it matters: </span>
              {check.explanation.whyItMatters}
            </p>
            <p>
              <span className="font-medium">Why {check.severity}: </span>
              {check.explanation.severityRationale}
            </p>
            <p>
              <span className="font-medium">How to fix: </span>
              {check.explanation.howToFix}
            </p>
          </>
        ) : null}
        <p className="text-xs text-muted-foreground">Source: {check.explanation.windowsInterface}</p>
        {check.outcome === "Fail" && check.remediationActionKey && canRemediate ? (
          <Button size="sm" variant="outline" onClick={() => onRemediate(check)}>
            <Wrench className="h-3.5 w-3.5" /> Request remediation
          </Button>
        ) : null}
      </div>
    </details>
  );
}

export function DeviceDetailView({ id }: { id: string }) {
  const router = useRouter();
  const { isAdmin } = useCan();
  const { toast } = useToast();
  const resource = useResource<EndpointDetail>(`security/endpoints/${id}`);
  const history = useResource<EndpointHistoryPoint[]>(`security/endpoints/${id}/history`);
  const [filter, setFilter] = React.useState<"issues" | "all">("issues");
  const [remediating, setRemediating] = React.useState<CheckResult | null>(null);
  const [reason, setReason] = React.useState("");
  const [busy, setBusy] = React.useState(false);
  const [newKey, setNewKey] = React.useState<Enrollment | null>(null);
  const [confirmDelete, setConfirmDelete] = React.useState(false);

  async function run(label: string, action: () => Promise<void>) {
    setBusy(true);
    try {
      await action();
    } catch (e) {
      toast({ tone: "error", title: label, description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <LoadState resource={resource}>
      {(d) => {
        const s = d.summary;
        const visible = d.checks.filter((c) => (filter === "all" ? true : c.outcome === "Fail" || c.outcome === "Unknown"));
        return (
          <div className="space-y-6">
            <Card>
              <CardContent className="flex flex-col gap-4 p-5 sm:flex-row sm:items-start sm:justify-between">
                <div className="space-y-1">
                  <h2 className="text-xl font-semibold">{s.hostname}</h2>
                  <p className="text-sm text-muted-foreground">
                    {s.osCaption ?? "Unknown OS"} · agent {s.agentVersion ?? "?"} · last report {timeAgo(s.lastReportAtUtc)}
                    {s.status === "Revoked" ? " · revoked" : ""}
                  </p>
                  <p className="text-sm text-muted-foreground">Evaluated against: {d.policyName ?? "no report yet"}{d.policyVersion ? ` (v${d.policyVersion})` : ""}</p>
                  <div className="pt-1">
                    <ScoreBadge score={s.score} band={s.band} />
                    {s.coverage !== null ? <span className="ml-3 text-xs text-muted-foreground">{Math.round(s.coverage * 100)}% of checks could be assessed</span> : null}
                  </div>
                </div>
                {isAdmin && s.status === "Active" ? (
                  <div className="flex flex-wrap gap-2">
                    <Button
                      variant="outline"
                      loading={busy}
                      onClick={() =>
                        run("Could not rotate key", async () => {
                          setNewKey(await api.post<Enrollment>(`security/endpoints/${id}/rotate-key`));
                        })
                      }
                    >
                      <KeyRound className="h-4 w-4" /> Rotate key
                    </Button>
                    <Button
                      variant="outline"
                      loading={busy}
                      onClick={() =>
                        run("Could not revoke", async () => {
                          await api.post(`security/endpoints/${id}/revoke`);
                          toast({ tone: "success", title: "Device revoked" });
                          await resource.reload();
                        })
                      }
                    >
                      <ShieldOff className="h-4 w-4" /> Revoke
                    </Button>
                    <Button variant="outline" className="text-destructive" onClick={() => setConfirmDelete(true)}>
                      <Trash2 className="h-4 w-4" /> Delete
                    </Button>
                  </div>
                ) : null}
              </CardContent>
            </Card>

            <Card>
              <CardHeader>
                <CardTitle>Why this score</CardTitle>
                <CardDescription>Score = 100 minus the points below. Points per failed check: Critical 40, High 20, Medium 10, Low 4.</CardDescription>
              </CardHeader>
              <CardContent className="space-y-3 text-sm">
                <ul className="list-disc space-y-1 pl-5">
                  {d.scoreReasons.map((r) => (
                    <li key={r}>{r}</li>
                  ))}
                </ul>
                {d.scorePenalties.length > 0 ? (
                  <ul className="divide-y divide-border rounded-md border border-border">
                    {d.scorePenalties.map((p, i) => (
                      <li key={`${p.ruleKey}-${i}`} className="flex items-center gap-2 px-3 py-2">
                        <SeverityBadge severity={p.severity} />
                        <span className="flex-1">{p.title}</span>
                        <span className="font-mono tabular-nums">{p.points === 0 ? "accepted" : `−${p.points}`}</span>
                      </li>
                    ))}
                  </ul>
                ) : null}
                {history.data && history.data.length > 1 ? (
                  <p className="text-xs text-muted-foreground">
                    History ({history.data.length} reports): {history.data.map((h) => h.score).join(" → ")}
                  </p>
                ) : null}
              </CardContent>
            </Card>

            <Card>
              <CardHeader className="flex-row items-center justify-between">
                <div>
                  <CardTitle>Checks</CardTitle>
                  <CardDescription>Select a check to see the evidence, why it matters, why it has its severity and how to fix it.</CardDescription>
                </div>
                <div className="flex gap-1">
                  <Button size="sm" variant={filter === "issues" ? "primary" : "outline"} onClick={() => setFilter("issues")}>
                    Issues
                  </Button>
                  <Button size="sm" variant={filter === "all" ? "primary" : "outline"} onClick={() => setFilter("all")}>
                    All
                  </Button>
                </div>
              </CardHeader>
              <CardContent className="p-0">
                {visible.length === 0 ? (
                  <p className="px-5 pb-5 text-sm text-muted-foreground">No failed or unknown checks.</p>
                ) : (
                  visible.map((c) => <CheckRow key={`${c.ruleKey}-${c.subjectKey}`} check={c} canRemediate={isAdmin} onRemediate={setRemediating} />)
                )}
              </CardContent>
            </Card>

            {d.facts ? <Facts facts={d.facts} /> : null}

            <Dialog
              open={remediating !== null}
              onClose={() => setRemediating(null)}
              title="Request remediation"
              description="Nothing changes until a different owner or administrator approves, and only if the active policy allows this action."
              footer={
                <>
                  <Button variant="outline" onClick={() => setRemediating(null)}>
                    Cancel
                  </Button>
                  <Button
                    loading={busy}
                    disabled={reason.trim().length === 0}
                    onClick={() =>
                      run("Could not request remediation", async () => {
                        const c = remediating!;
                        await api.post<Remediation>("security/remediation", { endpointId: id, findingId: null, actionKey: c.remediationActionKey, parameter: c.remediationParameter, reason });
                        toast({ tone: "success", title: "Requested", description: "Waiting for approval." });
                        setRemediating(null);
                        setReason("");
                        router.refresh();
                      })
                    }
                  >
                    Submit for approval
                  </Button>
                </>
              }
            >
              <div className="space-y-3">
                <p className="text-sm">
                  Action: <span className="font-mono">{remediating?.remediationActionKey}</span> — for “{remediating?.title}”.
                </p>
                <Field label="Reason" htmlFor="rem-reason" required>
                  <Textarea id="rem-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={3} />
                </Field>
              </div>
            </Dialog>

            <Dialog open={newKey !== null} onClose={() => setNewKey(null)} title="New device key" description={newKey?.notice} footer={<Button onClick={() => setNewKey(null)}>I have stored the key</Button>}>
              <code className="block break-all rounded-md border border-border bg-muted p-3 text-xs">{newKey?.deviceKey}</code>
            </Dialog>

            <Dialog
              open={confirmDelete}
              onClose={() => setConfirmDelete(false)}
              title={`Delete ${s.hostname}?`}
              description="Removes the device and its snapshots, findings, alerts, events and remediation history. The audit trail is kept."
              footer={
                <>
                  <Button variant="outline" onClick={() => setConfirmDelete(false)}>
                    Cancel
                  </Button>
                  <Button
                    variant="destructive"
                    loading={busy}
                    onClick={() =>
                      run("Could not delete", async () => {
                        await api.delete(`security/endpoints/${id}`);
                        router.push("/security/devices");
                      })
                    }
                  >
                    Delete device
                  </Button>
                </>
              }
            >
              <p className="text-sm text-muted-foreground">This cannot be undone. To keep the history, revoke the device instead.</p>
            </Dialog>
          </div>
        );
      }}
    </LoadState>
  );
}
