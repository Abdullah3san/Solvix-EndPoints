"use client";

import * as React from "react";
import { Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import type { BranchDto, DeploymentGroupDto, DeploymentPolicyDto, DeviceGroupDto, RestrictionRuleDto, RestrictionRuleType } from "@/lib/api/types";
import { emptyToNull, messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Select } from "@/components/ui/input";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";

export function PoliciesView({ policy, rules, groups, deviceGroups, branches }: {
  policy: DeploymentPolicyDto;
  rules: RestrictionRuleDto[];
  groups: DeploymentGroupDto[];
  deviceGroups: DeviceGroupDto[];
  branches: BranchDto[];
}) {
  const { toast } = useToast();
  const canManage = useCan("software.policies.manage");
  const [p, setP] = React.useState(policy);
  const [thumbprints, setThumbprints] = React.useState(policy.trustedPublisherThumbprints.join("\n"));
  const [ruleList, setRuleList] = React.useState(rules);
  const [groupList, setGroupList] = React.useState(groups);
  const [error, setError] = React.useState<string | null>(null);
  const [rule, setRule] = React.useState({ name: "", pattern: "", type: "Blocked" as RestrictionRuleType, publisher: "", minimum: "" });
  const [group, setGroup] = React.useState({ name: "", deviceGroupIds: [] as string[], branchIds: [] as string[] });

  async function savePolicy(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    try {
      const saved = await api.put<DeploymentPolicyDto>("software/policy", { ...p, trustedPublisherThumbprints: thumbprints.split("\n").map((t) => t.trim()).filter(Boolean) });
      setP(saved);
      toast({ tone: "success", title: "Policy saved" });
    } catch (e) {
      setError(messageFrom(e));
    }
  }

  async function addRule(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    try {
      await api.post("software/restriction-rules", { name: rule.name, description: null, ruleType: rule.type, namePattern: rule.pattern, publisher: emptyToNull(rule.publisher), minimumVersion: emptyToNull(rule.minimum), deviceGroupIds: null, enabled: true });
      setRuleList(await api.get<RestrictionRuleDto[]>("software/restriction-rules"));
      setRule({ name: "", pattern: "", type: "Blocked", publisher: "", minimum: "" });
    } catch (e) {
      setError(messageFrom(e));
    }
  }

  async function addGroup(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    try {
      await api.post("software/deployment-groups", { name: group.name, description: null, deviceIds: [], deviceGroupIds: group.deviceGroupIds, branchIds: group.branchIds, excludedDeviceIds: [] });
      setGroupList(await api.get<DeploymentGroupDto[]>("software/deployment-groups"));
      setGroup({ name: "", deviceGroupIds: [], branchIds: [] });
    } catch (e) {
      setError(messageFrom(e));
    }
  }

  const bool = (key: keyof DeploymentPolicyDto, label: string) => (
    <label className="flex items-center gap-2 text-sm">
      <Checkbox disabled={!canManage} checked={p[key] as boolean} onChange={(e) => setP({ ...p, [key]: e.target.checked })} /> {label}
    </label>
  );

  return (
    <div className="space-y-6">
      {error ? <Alert tone="error">{error}</Alert> : null}
      <Card>
        <CardHeader><CardTitle>Deployment policy</CardTitle></CardHeader>
        <CardContent>
          <form onSubmit={savePolicy} className="space-y-3">
            {bool("allowExeInstallers", "Allow EXE installers (otherwise MSI only)")}
            {bool("requireSignedPackages", "Refuse unsigned packages at upload")}
            {bool("requireApproval", "Every deployment needs one approval by someone else")}
            {bool("uninstallIsSensitive", "Uninstallations need the additional approval")}
            {bool("serverTargetsAreSensitive", "Deployments to servers need the additional approval")}
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Large deployment threshold (devices)" htmlFor="p-threshold"><Input id="p-threshold" type="number" min={1} max={5000} disabled={!canManage} value={p.largeDeploymentThreshold} onChange={(e) => setP({ ...p, largeDeploymentThreshold: Number(e.target.value) })} /></Field>
              <Field label="Approval period (hours)" htmlFor="p-ttl"><Input id="p-ttl" type="number" min={1} max={720} disabled={!canManage} value={p.approvalTtlHours} onChange={(e) => setP({ ...p, approvalTtlHours: Number(e.target.value) })} /></Field>
            </div>
            <Field label="Trusted publishers (SHA-256 certificate thumbprints, one per line)" htmlFor="p-thumbs" hint="Signed packages from these publishers skip the additional approval.">
              <textarea id="p-thumbs" className="min-h-20 w-full rounded-md border bg-background p-2 font-mono text-xs" disabled={!canManage} value={thumbprints} onChange={(e) => setThumbprints(e.target.value)} />
            </Field>
            {canManage ? <Button type="submit">Save policy</Button> : null}
          </form>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>Software restriction rules</CardTitle></CardHeader>
        <CardContent className="space-y-3">
          {ruleList.length === 0 ? <p className="text-sm text-muted-foreground">No rules.</p> : ruleList.map((r) => (
            <div key={r.id} className="flex items-center justify-between gap-2 text-sm">
              <span><Badge tone={r.ruleType === "Blocked" ? "danger" : "warning"}>{r.ruleType}</Badge> <strong>{r.name}</strong> — {r.namePattern}{r.publisher ? ` · ${r.publisher}` : ""}{r.minimumVersion ? ` · ${r.ruleType === "Required" ? "min" : "below"} ${r.minimumVersion}` : ""}</span>
              {canManage ? <Button variant="ghost" size="icon" aria-label={`Delete ${r.name}`} className="text-destructive" onClick={async () => { await api.delete(`software/restriction-rules/${r.id}`); setRuleList(ruleList.filter((x) => x.id !== r.id)); }}><Trash2 className="h-4 w-4" /></Button> : null}
            </div>
          ))}
          {canManage ? (
            <form onSubmit={addRule} className="grid gap-3 border-t pt-3 sm:grid-cols-5">
              <Input aria-label="Rule name" placeholder="Rule name" required value={rule.name} onChange={(e) => setRule({ ...rule, name: e.target.value })} />
              <Select aria-label="Rule type" value={rule.type} onChange={(e) => setRule({ ...rule, type: e.target.value as RestrictionRuleType })}><option value="Blocked">Blocked</option><option value="Required">Required</option></Select>
              <Input aria-label="Name pattern" placeholder="Name, e.g. *torrent*" required value={rule.pattern} onChange={(e) => setRule({ ...rule, pattern: e.target.value })} />
              <Input aria-label="Minimum version" placeholder="Version (optional)" value={rule.minimum} onChange={(e) => setRule({ ...rule, minimum: e.target.value })} />
              <Button type="submit">Add rule</Button>
            </form>
          ) : null}
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>Deployment groups</CardTitle></CardHeader>
        <CardContent className="space-y-3">
          {groupList.length === 0 ? <p className="text-sm text-muted-foreground">No deployment groups.</p> : groupList.map((g) => (
            <div key={g.id} className="flex items-center justify-between text-sm">
              <span><strong>{g.name}</strong> — {g.resolvedDeviceCount} active device(s)</span>
              {canManage ? <Button variant="ghost" size="icon" aria-label={`Delete ${g.name}`} className="text-destructive" onClick={async () => { await api.delete(`software/deployment-groups/${g.id}`); setGroupList(groupList.filter((x) => x.id !== g.id)); }}><Trash2 className="h-4 w-4" /></Button> : null}
            </div>
          ))}
          {canManage ? (
            <form onSubmit={addGroup} className="space-y-2 border-t pt-3">
              <Input aria-label="Group name" placeholder="Group name" required value={group.name} onChange={(e) => setGroup({ ...group, name: e.target.value })} />
              <div className="grid gap-3 md:grid-cols-2">
                <Select aria-label="Device group" multiple value={group.deviceGroupIds} onChange={(e) => setGroup({ ...group, deviceGroupIds: Array.from(e.target.selectedOptions, (o) => o.value) })}>
                  {deviceGroups.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
                </Select>
                <Select aria-label="Branch" multiple value={group.branchIds} onChange={(e) => setGroup({ ...group, branchIds: Array.from(e.target.selectedOptions, (o) => o.value) })}>
                  {branches.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
                </Select>
              </div>
              <Button type="submit">Create group</Button>
            </form>
          ) : null}
        </CardContent>
      </Card>
    </div>
  );
}
