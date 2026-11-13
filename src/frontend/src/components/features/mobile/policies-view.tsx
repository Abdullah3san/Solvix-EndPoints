"use client";

import * as React from "react";
import { AppWindow, Pencil, Plus, Send, Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import type { AppDto, AppInstallType, MobilePolicySettings, PolicyDto, PublishPolicyResultDto, Scenario } from "@/lib/api/mdm-types";
import type { PagedResult } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { emptySettings, installTypeLabel, scenarioKey, scenarioLabel, scenarios } from "@/lib/mdm";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Select, Textarea } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { PolicySettingsForm } from "./policy-form";

export function PoliciesView({ initial }: { initial: PagedResult<PolicyDto> }) {
  const { toast } = useToast();
  const { isAdmin } = useCan();
  const [data, setData] = React.useState(initial);
  const [editing, setEditing] = React.useState<PolicyDto | "new" | null>(null);
  const [appsFor, setAppsFor] = React.useState<PolicyDto | null>(null);
  const [report, setReport] = React.useState<PublishPolicyResultDto | null>(null);
  const [confirmDelete, setConfirmDelete] = React.useState<PolicyDto | null>(null);
  const [busyId, setBusyId] = React.useState<string | null>(null);

  const reload = React.useCallback(async () => {
    setData(await api.get<PagedResult<PolicyDto>>("mobile-policies", { pageSize: 100 }));
  }, []);

  async function publish(p: PolicyDto) {
    setBusyId(p.id);
    try {
      setReport(await api.post<PublishPolicyResultDto>(`mobile-policies/${p.id}/publish`));
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not publish", description: messageFrom(e) });
    } finally {
      setBusyId(null);
    }
  }

  async function remove(p: PolicyDto) {
    setBusyId(p.id);
    try {
      await api.delete(`mobile-policies/${p.id}`);
      toast({ tone: "success", title: "Policy deleted" });
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not delete", description: messageFrom(e) });
    } finally {
      setConfirmDelete(null);
      setBusyId(null);
    }
  }

  return (
    <>
      {isAdmin ? (
        <div className="mb-4 flex justify-end">
          <Button onClick={() => setEditing("new")}><Plus className="h-4 w-4" /> New policy</Button>
        </div>
      ) : null}
      <Card>
        <Table>
          <THead><TR><TH>Policy</TH><TH>Applies to</TH><TH>Version</TH><TH>Devices</TH>{isAdmin ? <TH className="text-right">Actions</TH> : null}</TR></THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={isAdmin ? 5 : 4}>No policies yet.</EmptyRow>
            ) : (
              data.items.map((p) => {
                const dirty = p.publishedVersion < p.version;
                return (
                  <TR key={p.id}>
                    <TD>
                      <p className="font-medium">{p.name}</p>
                      {p.description ? <p className="text-xs text-muted-foreground">{p.description}</p> : null}
                    </TD>
                    <TD className="text-sm text-muted-foreground">{scenarioLabel(p)}</TD>
                    <TD>
                      <div className="flex items-center gap-2">
                        v{p.version}
                        {p.publishedVersion === 0 ? <Badge tone="warning">Not published</Badge> : dirty ? <Badge tone="warning">Unpublished changes</Badge> : <Badge tone="success">Published</Badge>}
                      </div>
                    </TD>
                    <TD>{p.deviceCount}</TD>
                    {isAdmin ? (
                      <TD>
                        <div className="flex justify-end gap-1">
                          <Button variant="ghost" size="icon" aria-label={`Edit ${p.name}`} onClick={() => setEditing(p)}><Pencil className="h-4 w-4" /></Button>
                          <Button variant="ghost" size="icon" aria-label={`Apps for ${p.name}`} onClick={() => setAppsFor(p)}><AppWindow className="h-4 w-4" /></Button>
                          <Button variant="ghost" size="icon" aria-label={`Publish ${p.name}`} loading={busyId === p.id} onClick={() => publish(p)}><Send className="h-4 w-4" /></Button>
                          <Button variant="ghost" size="icon" aria-label={`Delete ${p.name}`} className="text-destructive" onClick={() => setConfirmDelete(p)}><Trash2 className="h-4 w-4" /></Button>
                        </div>
                      </TD>
                    ) : null}
                  </TR>
                );
              })
            )}
          </TBody>
        </Table>
      </Card>

      {editing ? (
        <PolicyDialog
          policy={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={async (created) => {
            setEditing(null);
            toast({ tone: "success", title: created ? "Policy created" : "Policy saved", description: "Publish it to send the changes to devices." });
            await reload();
          }}
        />
      ) : null}
      {appsFor ? (
        <PolicyAppsDialog
          policy={appsFor}
          onClose={() => setAppsFor(null)}
          onSaved={async () => {
            setAppsFor(null);
            toast({ tone: "success", title: "Applications saved", description: "Publish the policy to send the changes to devices." });
            await reload();
          }}
        />
      ) : null}
      {report ? <PublishReport result={report} onClose={() => setReport(null)} /> : null}
      <Dialog
        open={confirmDelete !== null}
        onClose={() => setConfirmDelete(null)}
        title="Delete policy?"
        description={confirmDelete ? `${confirmDelete.name} will be removed. Policies with assigned devices or issued enrollments cannot be deleted.` : undefined}
        footer={
          <>
            <Button variant="outline" onClick={() => setConfirmDelete(null)}>Cancel</Button>
            <Button variant="destructive" loading={busyId === confirmDelete?.id} onClick={() => confirmDelete && remove(confirmDelete)}>Delete</Button>
          </>
        }
      >
        <p className="text-sm text-muted-foreground">This action cannot be undone.</p>
      </Dialog>
    </>
  );
}

function PublishReport({ result, onClose }: { result: PublishPolicyResultDto; onClose: () => void }) {
  return (
    <Dialog open onClose={onClose} title={`Published ${result.policy.name} v${result.policy.publishedVersion}`} className="max-w-2xl" footer={<Button onClick={onClose}>Close</Button>}>
      <div className="space-y-4">
        <p className="text-sm text-muted-foreground">{result.devicesNotified} device{result.devicesNotified === 1 ? "" : "s"} will pick up this version.</p>
        {result.skipped.length > 0 ? (
          <Alert tone="error" title={`${result.skipped.length} setting${result.skipped.length === 1 ? " was" : "s were"} not applied`}>
            <ul className="mt-1 list-disc space-y-1 pl-5 text-sm">
              {result.skipped.map((s) => (
                <li key={s}>{s}</li>
              ))}
            </ul>
          </Alert>
        ) : (
          <Alert tone="success">Every configured setting is supported on this device type.</Alert>
        )}
        <div>
          <h3 className="mb-1 text-sm font-semibold">Applied</h3>
          <ul className="list-disc space-y-0.5 pl-5 text-sm text-muted-foreground">
            {result.applied.length === 0 ? <li>Nothing configured.</li> : result.applied.map((a) => <li key={a}>{a}</li>)}
          </ul>
        </div>
      </div>
    </Dialog>
  );
}

function PolicyDialog({ policy, onClose, onSaved }: { policy: PolicyDto | null; onClose: () => void; onSaved: (created: boolean) => Promise<void> }) {
  const [name, setName] = React.useState(policy?.name ?? "");
  const [description, setDescription] = React.useState(policy?.description ?? "");
  const [scenario, setScenario] = React.useState<Scenario>(policy ?? scenarios[0]);
  const [settings, setSettings] = React.useState<MobilePolicySettings>(policy?.settings ?? emptySettings());
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  function pickScenario(key: string) {
    // A different scenario has different valid settings, so start clean rather than carry over invalid ones.
    setScenario(scenarios.find((s) => scenarioKey(s) === key)!);
    setSettings(emptySettings());
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    setErrors({});
    try {
      const body = { name, description: description.trim() || null, settings };
      if (policy) await api.put(`mobile-policies/${policy.id}`, body);
      else await api.post("mobile-policies", { ...body, platform: scenario.platform, mode: scenario.mode, ownership: scenario.ownership });
      await onSaved(!policy);
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title={policy ? `Edit ${policy.name}` : "New policy"}
      className="max-w-3xl"
      footer={
        <>
          <Button variant="outline" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="policy-form" loading={pending}>{policy ? "Save changes" : "Create policy"}</Button>
        </>
      }
    >
      <form id="policy-form" onSubmit={submit} className="space-y-5">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Name" htmlFor="policy-name" error={errors.name} required>
            <Input id="policy-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} autoFocus />
          </Field>
          <Field label="Applies to" htmlFor="policy-scenario" error={errors.scenario} hint={policy ? "Cannot be changed after creation" : undefined}>
            <Select id="policy-scenario" value={scenarioKey(scenario)} disabled={policy !== null} onChange={(e) => pickScenario(e.target.value)}>
              {scenarios.map((s) => (
                <option key={scenarioKey(s)} value={scenarioKey(s)}>{scenarioLabel(s)}</option>
              ))}
            </Select>
          </Field>
          <Field className="sm:col-span-2" label="Description" htmlFor="policy-desc" error={errors.description}>
            <Input id="policy-desc" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={1000} />
          </Field>
        </div>
        <PolicySettingsForm scenario={scenario} value={settings} onChange={setSettings} errors={errors} />
      </form>
    </Dialog>
  );
}

function PolicyAppsDialog({ policy, onClose, onSaved }: { policy: PolicyDto; onClose: () => void; onSaved: () => Promise<void> }) {
  const [catalogue, setCatalogue] = React.useState<AppDto[] | null>(null);
  const [rows, setRows] = React.useState<Record<string, { installType: AppInstallType; config: string }>>(
    Object.fromEntries(policy.apps.map((a) => [a.appId, { installType: a.installType, config: a.managedConfigurationJson ?? "" }])),
  );
  const [error, setError] = React.useState<string | null>(null);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [pending, setPending] = React.useState(false);
  const android = policy.platform === "Android";
  const types: AppInstallType[] = ["ForceInstalled", "Available", "Blocked", ...(policy.mode === "Dedicated" ? (["Kiosk"] as AppInstallType[]) : [])];

  React.useEffect(() => {
    api
      .get<PagedResult<AppDto>>("mobile-apps", { platform: policy.platform, pageSize: 200 })
      .then((r) => setCatalogue(r.items))
      .catch((e) => setError(messageFrom(e)));
  }, [policy.platform]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    setErrors({});
    try {
      const apps = Object.entries(rows).map(([appId, r]) => ({ appId, installType: r.installType, managedConfigurationJson: r.config.trim() || null }));
      await api.put(`mobile-policies/${policy.id}/apps`, { apps });
      await onSaved();
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title={`Apps for ${policy.name}`}
      description="Tick the apps this policy manages. Managed configuration is a flat JSON object of app settings."
      className="max-w-2xl"
      footer={
        <>
          <Button variant="outline" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="policy-apps-form" loading={pending}>Save apps</Button>
        </>
      }
    >
      <form id="policy-apps-form" onSubmit={submit} className="space-y-3">
        {error ? <Alert tone="error">{error}</Alert> : null}
        {catalogue === null ? <p className="text-sm text-muted-foreground">Loading…</p> : null}
        {catalogue?.length === 0 ? <Alert tone="info">Add apps to the catalogue first.</Alert> : null}
        {(catalogue ?? []).map((app) => {
          const row = rows[app.id];
          const idx = Object.keys(rows).indexOf(app.id);
          return (
            <div key={app.id} className="rounded-md border border-border p-3">
              <label className="flex items-center gap-2 text-sm font-medium">
                <Checkbox
                  checked={row !== undefined}
                  onChange={(e) =>
                    setRows((r) => {
                      const next = { ...r };
                      if (e.target.checked) next[app.id] = { installType: "ForceInstalled", config: "" };
                      else delete next[app.id];
                      return next;
                    })
                  }
                />
                {app.name}
                <span className="font-mono text-xs font-normal text-muted-foreground">{app.identifier}</span>
              </label>
              {row ? (
                <div className="mt-3 grid gap-3 sm:grid-cols-2">
                  <Field label="Install type" htmlFor={`it-${app.id}`} error={errors[`apps[${idx}].installType`]} hint={!android && row.installType === "ForceInstalled" && app.storeId === null ? "Needs an App Store id in the catalogue" : undefined}>
                    <Select id={`it-${app.id}`} value={row.installType} onChange={(e) => setRows((r) => ({ ...r, [app.id]: { ...row, installType: e.target.value as AppInstallType } }))}>
                      {types.map((t) => (
                        <option key={t} value={t}>{installTypeLabel[t]}</option>
                      ))}
                    </Select>
                  </Field>
                  {row.installType !== "Blocked" ? (
                    <Field label="Managed configuration (JSON)" htmlFor={`cfg-${app.id}`} error={errors[`apps[${idx}].managedConfigurationJson`]}>
                      <Textarea id={`cfg-${app.id}`} className="min-h-9 font-mono text-xs" placeholder='{"server":"mail.example.com"}' value={row.config} onChange={(e) => setRows((r) => ({ ...r, [app.id]: { ...row, config: e.target.value } }))} />
                    </Field>
                  ) : null}
                </div>
              ) : null}
            </div>
          );
        })}
      </form>
    </Dialog>
  );
}
