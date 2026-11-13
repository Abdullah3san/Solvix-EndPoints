"use client";

import * as React from "react";
import { Copy, Plus } from "lucide-react";
import { api } from "@/lib/api/client";
import type { EnrollmentCreatedDto, EnrollmentDto, EnterpriseDto, ManagementMode, MobilePlatform, PolicyDto, Scenario } from "@/lib/api/mdm-types";
import type { PagedResult } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { formatDate, intOrNull, modeLabel, ownershipLabel, platformLabel, scenarioKey, scenarioLabel, scenarios } from "@/lib/mdm";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { QrCode } from "./qr-code";

export function EnrollmentsView({ initial, android, policies }: { initial: PagedResult<EnrollmentDto>; android: EnterpriseDto; policies: PolicyDto[] }) {
  const { toast } = useToast();
  const { isAdmin } = useCan();
  const [data, setData] = React.useState(initial);
  const [includeInactive, setIncludeInactive] = React.useState(false);
  const [creating, setCreating] = React.useState(false);
  const [created, setCreated] = React.useState<EnrollmentCreatedDto | null>(null);
  const [busyId, setBusyId] = React.useState<string | null>(null);
  const policyNames = React.useMemo(() => new Map(policies.map((p) => [p.id, p.name])), [policies]);

  const reload = React.useCallback(async (inactive: boolean) => {
    setData(await api.get<PagedResult<EnrollmentDto>>("mobile-enrollments", { includeInactive: inactive, pageSize: 50 }));
  }, []);

  async function revoke(e: EnrollmentDto) {
    setBusyId(e.id);
    try {
      await api.post(`mobile-enrollments/${e.id}/revoke`);
      toast({ tone: "success", title: "Enrollment revoked" });
      await reload(includeInactive);
    } catch (err) {
      toast({ tone: "error", title: "Could not revoke", description: messageFrom(err) });
    } finally {
      setBusyId(null);
    }
  }

  return (
    <div className="space-y-6">
      <AndroidEnterpriseCard initial={android} />

      <div className="flex items-center justify-between gap-3">
        <label className="flex items-center gap-2 text-sm text-muted-foreground">
          <input
            type="checkbox"
            className="h-4 w-4 accent-primary"
            checked={includeInactive}
            onChange={(e) => {
              setIncludeInactive(e.target.checked);
              void reload(e.target.checked);
            }}
          />
          Show expired and used
        </label>
        {isAdmin ? (
          <Button onClick={() => setCreating(true)} disabled={policies.length === 0}>
            <Plus className="h-4 w-4" /> New enrollment
          </Button>
        ) : null}
      </div>
      {policies.length === 0 ? <Alert tone="info">Create a policy first; every enrollment applies one.</Alert> : null}

      <Card>
        <Table>
          <THead>
            <TR><TH>Name</TH><TH>Scenario</TH><TH>Policy</TH><TH>Uses</TH><TH>Expires</TH><TH>Status</TH>{isAdmin ? <TH className="text-right">Actions</TH> : null}</TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={isAdmin ? 7 : 6}>No active enrollments.</EmptyRow>
            ) : (
              data.items.map((e) => (
                <TR key={e.id}>
                  <TD className="font-medium">{e.name}</TD>
                  <TD className="text-sm text-muted-foreground">{scenarioLabel(e)}</TD>
                  <TD className="text-sm">{policyNames.get(e.policyId) ?? "—"}</TD>
                  <TD>{e.useCount} / {e.maxUses}</TD>
                  <TD className="text-xs text-muted-foreground">{formatDate(e.expiresAtUtc)}</TD>
                  <TD><Badge tone={e.isUsable ? "success" : "neutral"}>{e.revokedAtUtc ? "Revoked" : e.isUsable ? "Active" : "Closed"}</Badge></TD>
                  {isAdmin ? (
                    <TD className="text-right">
                      {e.isUsable ? <Button size="sm" variant="outline" loading={busyId === e.id} onClick={() => revoke(e)}>Revoke</Button> : null}
                    </TD>
                  ) : null}
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>

      {creating ? (
        <CreateEnrollmentDialog
          policies={policies}
          androidBound={android.isBound}
          onClose={() => setCreating(false)}
          onCreated={async (result) => {
            setCreating(false);
            setCreated(result);
            await reload(includeInactive);
          }}
        />
      ) : null}
      {created ? <CreatedDialog result={created} onClose={() => setCreated(null)} /> : null}
    </div>
  );
}

function AndroidEnterpriseCard({ initial }: { initial: EnterpriseDto }) {
  const { toast } = useToast();
  const { isOwner } = useCan();
  const [state, setState] = React.useState(initial);
  const [token, setToken] = React.useState("");
  const [signupUrl, setSignupUrl] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  async function start() {
    setPending(true);
    setError(null);
    try {
      const callback = `${window.location.origin}/mobile/enrollments`;
      setSignupUrl((await api.post<{ url: string }>("mobile-enterprise/android/signup", { callbackUrl: callback })).url);
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  async function complete(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      setState(await api.post<EnterpriseDto>("mobile-enterprise/android/complete", { enterpriseToken: token.trim(), displayName: null }));
      setSignupUrl(null);
      toast({ tone: "success", title: "Android Enterprise connected" });
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Android Enterprise</CardTitle>
        <CardDescription>Android devices are managed through a managed Google Play enterprise bound to this workspace.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {state.isBound ? (
          <p className="text-sm">
            <Badge tone="success">Connected</Badge> <span className="ml-2 font-mono text-xs text-muted-foreground">{state.enterpriseName}</span>
            {state.lastSyncAtUtc ? <span className="ml-2 text-xs text-muted-foreground">Last sync {formatDate(state.lastSyncAtUtc)}</span> : null}
          </p>
        ) : !state.providerConfigured ? (
          <Alert tone="info">Android management is not configured on this server. An operator must set the Android project and service account.</Alert>
        ) : (
          <>
            <p className="text-sm text-muted-foreground">Not connected yet.</p>
            {error ? <Alert tone="error">{error}</Alert> : null}
            {isOwner ? (
              signupUrl ? (
                <form onSubmit={complete} className="space-y-3">
                  <p className="text-sm">
                    <a href={signupUrl} target="_blank" rel="noopener noreferrer" className="font-medium underline">Open Google to bind your account</a>, then paste the enterprise token Google returns.
                  </p>
                  <Field label="Enterprise token" htmlFor="enterprise-token" required>
                    <Input id="enterprise-token" value={token} onChange={(e) => setToken(e.target.value)} required />
                  </Field>
                  <Button type="submit" loading={pending}>Complete connection</Button>
                </form>
              ) : (
                <Button onClick={start} loading={pending}>Connect Android Enterprise</Button>
              )
            ) : (
              <p className="text-xs text-muted-foreground">Only the workspace owner can connect Android Enterprise.</p>
            )}
          </>
        )}
      </CardContent>
    </Card>
  );
}

function CreateEnrollmentDialog({
  policies,
  androidBound,
  onClose,
  onCreated,
}: {
  policies: PolicyDto[];
  androidBound: boolean;
  onClose: () => void;
  onCreated: (r: EnrollmentCreatedDto) => Promise<void>;
}) {
  const available = scenarios.filter((s) => policies.some((p) => scenarioKey(p) === scenarioKey(s)));
  const [scenario, setScenario] = React.useState<Scenario>(available[0] ?? scenarios[0]);
  const matching = policies.filter((p) => scenarioKey(p) === scenarioKey(scenario));
  const [policyId, setPolicyId] = React.useState(matching[0]?.id ?? "");
  const [name, setName] = React.useState("");
  const [hours, setHours] = React.useState("24");
  const [maxUses, setMaxUses] = React.useState("1");
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);
  const android = scenario.platform === "Android";

  function pickScenario(key: string) {
    const next = scenarios.find((s) => scenarioKey(s) === key)!;
    setScenario(next);
    setPolicyId(policies.find((p) => scenarioKey(p) === key)?.id ?? "");
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    setErrors({});
    try {
      const result = await api.post<EnrollmentCreatedDto>("mobile-enrollments", {
        name,
        platform: scenario.platform as MobilePlatform,
        mode: scenario.mode as ManagementMode,
        ownership: scenario.ownership,
        policyId,
        validForHours: intOrNull(hours) ?? 24,
        maxUses: android ? 1 : (intOrNull(maxUses) ?? 1),
      });
      await onCreated(result);
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
      title="New enrollment"
      footer={
        <>
          <Button variant="outline" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="enrollment-form" loading={pending} disabled={!policyId || (android && !androidBound)}>Create</Button>
        </>
      }
    >
      <form id="enrollment-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        {android && !androidBound ? <Alert tone="info">Connect Android Enterprise first.</Alert> : null}
        <Field label="Name" htmlFor="enrollment-name" error={errors.name} required>
          <Input id="enrollment-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} autoFocus />
        </Field>
        <Field label="Device type" htmlFor="enrollment-scenario" hint={`${modeLabel[scenario.mode]} · ${ownershipLabel[scenario.ownership]}`} error={errors.scenario}>
          <Select id="enrollment-scenario" value={scenarioKey(scenario)} onChange={(e) => pickScenario(e.target.value)}>
            {available.map((s) => (
              <option key={scenarioKey(s)} value={scenarioKey(s)}>{scenarioLabel(s)}</option>
            ))}
          </Select>
        </Field>
        <Field label="Policy" htmlFor="enrollment-policy" error={errors.policyId} required>
          <Select id="enrollment-policy" value={policyId} onChange={(e) => setPolicyId(e.target.value)} required>
            {matching.map((p) => (
              <option key={p.id} value={p.id}>{p.name}</option>
            ))}
          </Select>
        </Field>
        <div className="grid grid-cols-2 gap-4">
          <Field label="Valid for (hours)" htmlFor="enrollment-hours" error={errors.validForHours}>
            <Input id="enrollment-hours" inputMode="numeric" value={hours} onChange={(e) => setHours(e.target.value)} />
          </Field>
          <Field label="Devices" htmlFor="enrollment-uses" error={errors.maxUses} hint={android ? "Android tokens are single-use" : undefined}>
            <Input id="enrollment-uses" inputMode="numeric" value={android ? "1" : maxUses} disabled={android} onChange={(e) => setMaxUses(e.target.value)} />
          </Field>
        </div>
      </form>
    </Dialog>
  );
}

function CopyField({ label, value }: { label: string; value: string }) {
  const { toast } = useToast();
  return (
    <div className="space-y-1.5">
      <p className="text-sm font-medium">{label}</p>
      <div className="flex gap-2">
        <Input readOnly value={value} className="font-mono text-xs" aria-label={label} onFocus={(e) => e.currentTarget.select()} />
        <Button
          variant="outline"
          size="icon"
          aria-label={`Copy ${label}`}
          onClick={() => navigator.clipboard.writeText(value).then(() => toast({ tone: "success", title: "Copied" }), () => toast({ tone: "error", title: "Copy failed" }))}
        >
          <Copy className="h-4 w-4" />
        </Button>
      </div>
    </div>
  );
}

function CreatedDialog({ result, onClose }: { result: EnrollmentCreatedDto; onClose: () => void }) {
  return (
    <Dialog open onClose={onClose} title={`Enroll devices: ${result.enrollment.name}`} className="max-w-xl" footer={<Button onClick={onClose}>I have saved this</Button>}>
      <div className="space-y-4">
        <Alert tone="info">This is the only time the enrollment code is shown. It grants enrollment into your workspace, so share it only with the intended user.</Alert>
        <ol className="list-decimal space-y-1 pl-5 text-sm">
          {result.instructions.map((i) => (
            <li key={i}>{i}</li>
          ))}
        </ol>
        {result.qrPayload || result.enrollmentUrl ? (
          <div className="flex justify-center">
            <QrCode value={(result.qrPayload ?? result.enrollmentUrl)!} />
          </div>
        ) : null}
        {result.tokenValue ? <CopyField label="Enrollment token" value={result.tokenValue} /> : null}
        {result.enrollmentUrl ? <CopyField label="Enrollment link" value={result.enrollmentUrl} /> : null}
        <p className="text-xs text-muted-foreground">
          {platformLabel[result.enrollment.platform]} · valid until {formatDate(result.enrollment.expiresAtUtc)}
        </p>
      </div>
    </Dialog>
  );
}
