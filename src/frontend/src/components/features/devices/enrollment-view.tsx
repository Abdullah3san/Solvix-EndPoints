"use client";

import * as React from "react";
import { Ban, Copy, Plus } from "lucide-react";
import { api } from "@/lib/api/client";
import type { CreatedEnrollmentTokenDto, DeviceGroupDto, EnrollmentTokenDto, PagedResult } from "@/lib/api/types";
import { installCommand, tokenStateTone } from "@/lib/devices";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";

const lifetimes = [
  { label: "1 hour", hours: 1 },
  { label: "8 hours", hours: 8 },
  { label: "24 hours", hours: 24 },
  { label: "3 days", hours: 72 },
  { label: "7 days", hours: 168 },
];

interface Props {
  initial: PagedResult<EnrollmentTokenDto>;
  groups: DeviceGroupDto[];
  serverUrl: string;
  serverPin: string | null;
}

export function EnrollmentView({ initial, groups, serverUrl, serverPin }: Props) {
  const { toast } = useToast();
  const [tokens, setTokens] = React.useState(initial.items);
  const [creating, setCreating] = React.useState(false);
  const [created, setCreated] = React.useState<CreatedEnrollmentTokenDto | null>(null);
  const [busyId, setBusyId] = React.useState<string | null>(null);

  async function reload() {
    setTokens((await api.get<PagedResult<EnrollmentTokenDto>>("enrollment-tokens", { includeInactive: true, pageSize: 50 })).items);
  }

  async function revoke(token: EnrollmentTokenDto) {
    setBusyId(token.id);
    try {
      await api.post(`enrollment-tokens/${token.id}/revoke`);
      toast({ tone: "success", title: "Token revoked" });
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not revoke token", description: messageFrom(e) });
    } finally {
      setBusyId(null);
    }
  }

  return (
    <>
      <Card className="mb-6">
        <CardHeader>
          <CardTitle>How enrollment works</CardTitle>
          <CardDescription>
            1. Create a token below. 2. Run the installer on the Windows device as administrator with that token. 3. The agent generates a
            non-exportable key (TPM when available), receives a device certificate and deletes the token. All further communication is outbound
            HTTPS with mutual TLS.
          </CardDescription>
        </CardHeader>
        <CardContent className="text-sm text-muted-foreground">
          Supported: Windows 10, Windows 11, Windows Server 2016, 2019, 2022 and 2025 (64-bit). Agent endpoint: <span className="font-mono">{serverUrl}</span>
        </CardContent>
      </Card>

      <div className="mb-4 flex justify-end">
        <Button onClick={() => setCreating(true)}>
          <Plus className="h-4 w-4" /> New enrollment token
        </Button>
      </div>

      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Name</TH>
              <TH>State</TH>
              <TH>Uses</TH>
              <TH>Group</TH>
              <TH>Expires</TH>
              <TH>Last used</TH>
              <TH className="text-right">Actions</TH>
            </TR>
          </THead>
          <TBody>
            {tokens.length === 0 ? (
              <EmptyRow colSpan={7}>No enrollment tokens yet.</EmptyRow>
            ) : (
              tokens.map((t) => (
                <TR key={t.id}>
                  <TD className="font-medium">{t.name}</TD>
                  <TD>
                    <Badge tone={tokenStateTone(t.state)}>{t.state}</Badge>
                  </TD>
                  <TD>
                    {t.useCount} / {t.maxUses}
                  </TD>
                  <TD className="text-muted-foreground">{t.deviceGroupName ?? "—"}</TD>
                  <TD className="text-muted-foreground">{formatDate(t.expiresAtUtc)}</TD>
                  <TD className="text-muted-foreground">{formatDate(t.lastUsedAtUtc)}</TD>
                  <TD className="text-right">
                    {t.state === "Active" ? (
                      <Button variant="ghost" size="sm" onClick={() => revoke(t)} loading={busyId === t.id}>
                        <Ban className="h-4 w-4" /> Revoke
                      </Button>
                    ) : null}
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>

      {creating ? (
        <CreateTokenDialog
          groups={groups}
          onClose={() => setCreating(false)}
          onCreated={async (result) => {
            setCreating(false);
            setCreated(result);
            await reload();
          }}
        />
      ) : null}

      {created ? (
        <Dialog open onClose={() => setCreated(null)} title="Enrollment token created" description="Copy it now: it is shown only once and cannot be retrieved later." className="max-w-2xl">
          <div className="space-y-4">
            <CopyField label="Token" value={created.token} />
            <CopyField label="Install command (PowerShell, as administrator)" value={installCommand(serverUrl, created.token, serverPin)} />
            <Alert>
              Valid until {formatDate(created.enrollmentToken.expiresAtUtc)} for {created.enrollmentToken.maxUses} device(s). Treat it like a password and revoke it
              when the rollout is complete.
            </Alert>
          </div>
        </Dialog>
      ) : null}
    </>
  );
}

function CopyField({ label, value }: { label: string; value: string }) {
  const { toast } = useToast();
  return (
    <div className="space-y-1.5">
      <p className="text-sm font-medium">{label}</p>
      <div className="flex gap-2">
        <code className="flex-1 overflow-x-auto whitespace-nowrap rounded-md border border-border bg-muted px-3 py-2 text-xs">{value}</code>
        <Button
          variant="outline"
          size="icon"
          aria-label={`Copy ${label.toLowerCase()}`}
          onClick={async () => {
            await navigator.clipboard.writeText(value);
            toast({ tone: "success", title: "Copied" });
          }}
        >
          <Copy className="h-4 w-4" />
        </Button>
      </div>
    </div>
  );
}

function CreateTokenDialog({ groups, onClose, onCreated }: { groups: DeviceGroupDto[]; onClose: () => void; onCreated: (r: CreatedEnrollmentTokenDto) => Promise<void> }) {
  const [name, setName] = React.useState("");
  const [lifetimeHours, setLifetimeHours] = React.useState(24);
  const [maxUses, setMaxUses] = React.useState(1);
  const [groupId, setGroupId] = React.useState("");
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      await onCreated(await api.post<CreatedEnrollmentTokenDto>("enrollment-tokens", { name, lifetimeHours, maxUses, deviceGroupId: groupId || null }));
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
      title="New enrollment token"
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="token-form" loading={pending}>
            Create token
          </Button>
        </>
      }
    >
      <form id="token-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <Field label="Name" htmlFor="token-name" error={errors.name} hint="e.g. Berlin office laptops, October rollout" required>
          <Input id="token-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} autoFocus />
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Valid for" htmlFor="token-lifetime" error={errors.lifetimeHours}>
            <Select id="token-lifetime" value={lifetimeHours} onChange={(e) => setLifetimeHours(Number(e.target.value))}>
              {lifetimes.map((l) => (
                <option key={l.hours} value={l.hours}>
                  {l.label}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Maximum devices" htmlFor="token-uses" error={errors.maxUses}>
            <Input id="token-uses" type="number" min={1} max={10000} value={maxUses} onChange={(e) => setMaxUses(Number(e.target.value))} />
          </Field>
        </div>
        <Field label="Add enrolled devices to group" htmlFor="token-group">
          <Select id="token-group" value={groupId} onChange={(e) => setGroupId(e.target.value)}>
            <option value="">No group</option>
            {groups.map((g) => (
              <option key={g.id} value={g.id}>
                {g.name}
              </option>
            ))}
          </Select>
        </Field>
      </form>
    </Dialog>
  );
}
