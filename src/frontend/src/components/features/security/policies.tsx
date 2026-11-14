"use client";

import * as React from "react";
import { Plus } from "lucide-react";
import { api } from "@/lib/api/client";
import type { EffectivePolicy, Policy, PolicySettings, RulesCatalog } from "@/lib/api/security-types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { humanize, timeAgo } from "@/lib/security";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Checkbox, Input, Select, Textarea } from "@/components/ui/input";
import { Field } from "@/components/ui/field";
import { useCan, useSession } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";
import { LoadState, useResource } from "./security-ui";

const defaults: PolicySettings = {
  enforcementMode: "DetectOnly",
  allowedRemediationActions: [],
  requireDefenderRealTimeProtection: true,
  requireDefenderTamperProtection: true,
  maxSignatureAgeDays: 3,
  maxDaysSinceQuickScan: 14,
  requireFirewallAllProfiles: true,
  requireBitLockerOsVolume: true,
  requireBitLockerFixedVolumes: false,
  maxLocalAdministrators: 3,
  approvedLocalAdministrators: [],
  requireBuiltInAdministratorDisabled: true,
  requireGuestAccountDisabled: true,
  usbStorage: "Allow",
  requireSmb1Disabled: true,
  requireUac: true,
  requireRdpNla: true,
  requireSecureBoot: false,
  requireWindowsUpdateNotDisabled: true,
  failedLogonThreshold: 10,
  failedLogonWindowMinutes: 10,
  staleAfterDays: 7,
};

const toggles: { key: keyof PolicySettings; label: string }[] = [
  { key: "requireDefenderRealTimeProtection", label: "Defender real-time protection on" },
  { key: "requireDefenderTamperProtection", label: "Defender Tamper Protection on" },
  { key: "requireFirewallAllProfiles", label: "Windows Firewall on for all profiles" },
  { key: "requireBitLockerOsVolume", label: "BitLocker on the system drive" },
  { key: "requireBitLockerFixedVolumes", label: "BitLocker on fixed data drives" },
  { key: "requireBuiltInAdministratorDisabled", label: "Built-in Administrator disabled" },
  { key: "requireGuestAccountDisabled", label: "Guest account disabled" },
  { key: "requireSmb1Disabled", label: "SMBv1 disabled" },
  { key: "requireUac", label: "User Account Control on" },
  { key: "requireRdpNla", label: "Remote Desktop requires NLA" },
  { key: "requireSecureBoot", label: "Secure Boot on" },
  { key: "requireWindowsUpdateNotDisabled", label: "Windows Update not disabled by policy" },
];

const numbers: { key: keyof PolicySettings; label: string; min: number; max: number }[] = [
  { key: "maxSignatureAgeDays", label: "Max Defender signature age (days)", min: 1, max: 30 },
  { key: "maxDaysSinceQuickScan", label: "Max days since a quick scan", min: 1, max: 90 },
  { key: "maxLocalAdministrators", label: "Max local administrators", min: 1, max: 50 },
  { key: "failedLogonThreshold", label: "Failed sign-ins that raise an alert", min: 3, max: 1000 },
  { key: "failedLogonWindowMinutes", label: "…within (minutes)", min: 1, max: 1440 },
  { key: "staleAfterDays", label: "Device is stale after (days without report)", min: 1, max: 90 },
];

export function PolicyEditor(props: { open: boolean; initial: Policy | null; onClose: () => void; onSaved: () => void }) {
  // Mounted only while open (and re-keyed per policy) so the form starts from the right values without an effect.
  return props.open ? <PolicyEditorBody key={props.initial?.id ?? "new"} {...props} /> : null;
}

function PolicyEditorBody({
  open,
  initial,
  onClose,
  onSaved,
}: {
  open: boolean;
  initial: Policy | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const { toast } = useToast();
  const rules = useResource<RulesCatalog>("security/rules");
  const [name, setName] = React.useState(initial?.name ?? "");
  const [description, setDescription] = React.useState(initial?.description ?? "");
  const [s, setS] = React.useState<PolicySettings>(initial?.settings ?? defaults);
  const [admins, setAdmins] = React.useState((initial?.settings.approvedLocalAdministrators ?? []).join("\n"));
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [busy, setBusy] = React.useState(false);

  const actions = rules.data?.remediationActions ?? [];
  const detectOnly = s.enforcementMode === "DetectOnly";

  async function save() {
    setBusy(true);
    setErrors({});
    const settings: PolicySettings = {
      ...s,
      allowedRemediationActions: detectOnly ? [] : s.allowedRemediationActions,
      approvedLocalAdministrators: admins.split("\n").map((x) => x.trim()).filter(Boolean),
    };
    try {
      const body = { name, description: description.trim() || null, settings };
      if (initial) await api.put(`security/policies/${initial.id}`, body);
      else await api.post("security/policies", body);
      toast({ tone: "success", title: "Draft saved" });
      onSaved();
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      toast({ tone: "error", title: "Could not save policy", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      className="max-w-3xl"
      title={initial ? `Edit draft v${initial.version}` : "New policy"}
      description="A policy takes effect only after another owner approves it."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button loading={busy} disabled={name.trim().length === 0} onClick={save}>
            Save draft
          </Button>
        </>
      }
    >
      <div className="space-y-5">
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Name" htmlFor="pol-name" error={errors.name} required>
            <Input id="pol-name" value={name} onChange={(e) => setName(e.target.value)} maxLength={120} />
          </Field>
          <Field label="Description" htmlFor="pol-desc" error={errors.description}>
            <Input id="pol-desc" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500} />
          </Field>
        </div>

        <fieldset className="space-y-3 rounded-md border border-border p-3">
          <legend className="px-1 text-sm font-medium">Enforcement — does Solvix ever change devices?</legend>
          <Field label="Mode" htmlFor="pol-mode" error={errors.enforcementMode}>
            <Select id="pol-mode" value={s.enforcementMode} onChange={(e) => setS({ ...s, enforcementMode: e.target.value as PolicySettings["enforcementMode"] })}>
              <option value="DetectOnly">Detect only — report problems, never change a device</option>
              <option value="ApprovalRequired">Approval required — every change needs a second person</option>
              <option value="AutoApproveLowRisk">Auto-approve Low-risk — Low-risk actions below skip approval</option>
            </Select>
          </Field>
          {detectOnly ? (
            <Alert tone="info">In detect-only mode no remediation can be requested or delivered.</Alert>
          ) : (
            <div className="space-y-2">
              <p className="text-sm font-medium">Actions this policy permits</p>
              {errors.allowedRemediationActions ? <p className="text-xs text-destructive">{errors.allowedRemediationActions}</p> : null}
              {actions.map((a) => (
                <label key={a.key} className="flex items-start gap-2 text-sm">
                  <Checkbox
                    className="mt-0.5"
                    checked={s.allowedRemediationActions.includes(a.key)}
                    onChange={(e) =>
                      setS({
                        ...s,
                        allowedRemediationActions: e.target.checked ? [...s.allowedRemediationActions, a.key] : s.allowedRemediationActions.filter((k) => k !== a.key),
                      })
                    }
                  />
                  <span>
                    {a.title} <Badge tone={a.risk === "High" ? "danger" : a.risk === "Medium" ? "warning" : "neutral"}>{a.risk}</Badge>
                    <span className="block text-xs text-muted-foreground">{a.impact}</span>
                  </span>
                </label>
              ))}
            </div>
          )}
        </fieldset>

        <fieldset className="space-y-2 rounded-md border border-border p-3">
          <legend className="px-1 text-sm font-medium">Required settings</legend>
          <div className="grid gap-2 sm:grid-cols-2">
            {toggles.map((t) => (
              <label key={t.key} className="flex items-center gap-2 text-sm">
                <Checkbox checked={s[t.key] as boolean} onChange={(e) => setS({ ...s, [t.key]: e.target.checked })} />
                {t.label}
              </label>
            ))}
          </div>
          <Field label="USB storage" htmlFor="pol-usb" error={errors.usbStorage}>
            <Select id="pol-usb" value={s.usbStorage} onChange={(e) => setS({ ...s, usbStorage: e.target.value as PolicySettings["usbStorage"] })}>
              <option value="Allow">Allowed</option>
              <option value="BlockWrite">Block writing to USB storage</option>
              <option value="BlockAll">Block all USB storage</option>
            </Select>
          </Field>
        </fieldset>

        <fieldset className="space-y-3 rounded-md border border-border p-3">
          <legend className="px-1 text-sm font-medium">Thresholds</legend>
          <div className="grid gap-3 sm:grid-cols-2">
            {numbers.map((n) => (
              <Field key={n.key} label={n.label} htmlFor={`pol-${n.key}`} error={errors[n.key]}>
                <Input
                  id={`pol-${n.key}`}
                  type="number"
                  min={n.min}
                  max={n.max}
                  value={s[n.key] as number}
                  onChange={(e) => setS({ ...s, [n.key]: Number(e.target.value) })}
                />
              </Field>
            ))}
          </div>
          <Field label="Approved local administrators (one per line)" htmlFor="pol-admins" error={errors.approvedLocalAdministrators} hint="Full names such as CONTOSO\alice or SIDs. Leave empty to skip the “unapproved administrator” check.">
            <Textarea id="pol-admins" value={admins} onChange={(e) => setAdmins(e.target.value)} rows={4} />
          </Field>
        </fieldset>
      </div>
    </Dialog>
  );
}

export function PoliciesView() {
  const { isAdmin, isOwner } = useCan();
  const { user } = useSession();
  const { toast } = useToast();
  const effective = useResource<EffectivePolicy>("security/policies/effective");
  const policies = useResource<Policy[]>("security/policies");
  const [editor, setEditor] = React.useState<{ open: boolean; policy: Policy | null }>({ open: false, policy: null });
  const [busy, setBusy] = React.useState<string | null>(null);

  async function act(id: string, path: string, success: string, method: "post" | "delete" = "post") {
    setBusy(id + path);
    try {
      if (method === "delete") await api.delete(`security/policies/${id}`);
      else await api.post(`security/policies/${id}/${path}`);
      toast({ tone: "success", title: success });
      await Promise.all([policies.reload(), effective.reload()]);
    } catch (e) {
      toast({ tone: "error", title: "Action failed", description: messageFrom(e) });
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="space-y-6">
      <LoadState resource={effective}>
        {(e) => (
          <Card>
            <CardHeader>
              <CardTitle>Policy in force</CardTitle>
              <CardDescription>
                {e.name}
                {e.version ? ` (v${e.version})` : ""} · {e.advisoryCount} vulnerability advisories
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-2 text-sm">
              <p>
                Enforcement: <Badge tone={e.settings.enforcementMode === "DetectOnly" ? "neutral" : "warning"}>{humanize(e.settings.enforcementMode)}</Badge>
                {e.settings.enforcementMode === "DetectOnly" ? " — Solvix reports problems and never changes a device." : ` — permitted actions: ${e.settings.allowedRemediationActions.join(", ") || "none"}.`}
              </p>
              {e.isBuiltIn ? <p className="text-muted-foreground">No policy has been approved yet, so the built-in baseline applies.</p> : null}
            </CardContent>
          </Card>
        )}
      </LoadState>

      {isAdmin ? (
        <div className="flex justify-end">
          <Button onClick={() => setEditor({ open: true, policy: null })}>
            <Plus className="h-4 w-4" /> New policy
          </Button>
        </div>
      ) : null}

      <LoadState resource={policies}>
        {(items) =>
          items.length === 0 ? (
            <p className="text-sm text-muted-foreground">No policies yet.</p>
          ) : (
            <ul className="space-y-3">
              {items.map((p) => {
                const mine = p.authorUserId === user.id;
                return (
                  <li key={p.id}>
                    <Card>
                      <CardContent className="space-y-2 p-4">
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="font-medium">
                            {p.name} <span className="text-muted-foreground">v{p.version}</span>
                          </span>
                          <Badge tone={p.status === "Active" ? "success" : p.status === "PendingApproval" ? "warning" : p.status === "Rejected" ? "danger" : "neutral"}>{humanize(p.status)}</Badge>
                          <Badge>{humanize(p.settings.enforcementMode)}</Badge>
                          <span className="ml-auto text-xs text-muted-foreground">created {timeAgo(p.createdAtUtc)}{mine ? " by you" : ""}</span>
                        </div>
                        {p.description ? <p className="text-sm text-muted-foreground">{p.description}</p> : null}
                        {p.decisionNote ? <p className="text-sm">Decision note: {p.decisionNote}</p> : null}
                        <div className="flex flex-wrap gap-2">
                          {p.status === "Draft" && isAdmin ? (
                            <>
                              {mine ? (
                                <Button size="sm" variant="outline" onClick={() => setEditor({ open: true, policy: p })}>
                                  Edit
                                </Button>
                              ) : null}
                              <Button size="sm" loading={busy === p.id + "submit"} onClick={() => act(p.id, "submit", "Submitted for approval")}>
                                Submit for approval
                              </Button>
                              {mine || isOwner ? (
                                <Button size="sm" variant="ghost" className="text-destructive" onClick={() => act(p.id, "", "Draft deleted", "delete")}>
                                  Delete
                                </Button>
                              ) : null}
                            </>
                          ) : null}
                          {p.status === "PendingApproval" && isAdmin && (mine || p.submittedByUserId === user.id) ? (
                            <Button size="sm" variant="outline" onClick={() => act(p.id, "withdraw", "Submission withdrawn")}>
                              Withdraw
                            </Button>
                          ) : null}
                          {p.status === "PendingApproval" ? <span className="text-xs text-muted-foreground">Decide it on the Approvals page.</span> : null}
                          {(p.status === "Active" || p.status === "Retired" || p.status === "Rejected") && isAdmin ? (
                            <Button size="sm" variant="outline" onClick={() => act(p.id, "revise", "New draft created")}>
                              Create new version from this
                            </Button>
                          ) : null}
                        </div>
                      </CardContent>
                    </Card>
                  </li>
                );
              })}
            </ul>
          )
        }
      </LoadState>

      <PolicyEditor
        open={editor.open}
        initial={editor.policy}
        onClose={() => setEditor({ open: false, policy: null })}
        onSaved={() => {
          setEditor({ open: false, policy: null });
          void policies.reload();
        }}
      />
    </div>
  );
}
