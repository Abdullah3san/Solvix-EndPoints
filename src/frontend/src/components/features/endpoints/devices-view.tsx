"use client";

import * as React from "react";
import { Copy, KeyRound, Plus, Power, RefreshCw } from "lucide-react";
import { api } from "@/lib/api/client";
import type { PagedResult } from "@/lib/api/types";
import type { CreatedEnrollmentTokenDto, DeviceDto, DeviceGroupDto, EnrollmentTokenDto } from "@/lib/api/endpoint-types";
import { messageFrom } from "@/lib/forms";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { ConfirmDialog, NumberInput, formatRelative, useNow } from "./common";

export function DevicesView({
  initialDevices,
  initialGroups,
  initialTokens,
  branches,
}: {
  initialDevices: PagedResult<DeviceDto>;
  initialGroups: DeviceGroupDto[];
  initialTokens: EnrollmentTokenDto[];
  branches: { id: string; name: string }[];
}) {
  const { toast } = useToast();
  const { isAdmin } = useCan();
  const now = useNow();
  const [devices, setDevices] = React.useState(initialDevices);
  const [groups, setGroups] = React.useState(initialGroups);
  const [tokens, setTokens] = React.useState(initialTokens);
  const [search, setSearch] = React.useState("");
  const [groupFilter, setGroupFilter] = React.useState("");
  const [creatingToken, setCreatingToken] = React.useState(false);
  const [creatingGroup, setCreatingGroup] = React.useState(false);
  const [retiring, setRetiring] = React.useState<DeviceDto | null>(null);
  const [busy, setBusy] = React.useState(false);

  const reload = React.useCallback(async () => {
    const [d, g, t] = await Promise.all([
      api.get<PagedResult<DeviceDto>>("devices", { search, groupId: groupFilter || undefined, pageSize: 100 }),
      api.get<DeviceGroupDto[]>("device-groups"),
      isAdmin ? api.get<EnrollmentTokenDto[]>("enrollment-tokens") : Promise.resolve(tokens),
    ]);
    setDevices(d);
    setGroups(g);
    setTokens(t);
  }, [search, groupFilter, isAdmin, tokens]);

  const groupName = (id: string) => groups.find((g) => g.id === id)?.name ?? "?";
  const branchName = (id: string | null) => (id ? (branches.find((b) => b.id === id)?.name ?? "?") : "—");

  async function retire(device: DeviceDto) {
    setBusy(true);
    try {
      await api.post(`devices/${device.id}/retire`);
      toast({ tone: "success", title: `${device.hostname} retired`, description: "Its agent credential is revoked." });
      setRetiring(null);
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not retire device", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  async function addToGroup(device: DeviceDto, groupId: string) {
    try {
      await api.post(`device-groups/${groupId}/members`, { deviceIds: [device.id] });
      toast({ tone: "success", title: `${device.hostname} added to ${groupName(groupId)}` });
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not add to group", description: messageFrom(e) });
    }
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <form
          className="flex flex-1 flex-wrap items-center gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            void reload();
          }}
        >
          <Input placeholder="Search hostname or OS…" value={search} onChange={(e) => setSearch(e.target.value)} className="max-w-xs" aria-label="Search devices" />
          <Select aria-label="Filter by group" value={groupFilter} onChange={(e) => setGroupFilter(e.target.value)} className="w-48">
            <option value="">All groups</option>
            {groups.map((g) => (
              <option key={g.id} value={g.id}>
                {g.name}
              </option>
            ))}
          </Select>
          <Button type="submit" variant="outline">
            <RefreshCw className="h-4 w-4" /> Apply
          </Button>
        </form>
        {isAdmin ? (
          <div className="flex gap-2">
            <Button variant="outline" onClick={() => setCreatingGroup(true)}>
              <Plus className="h-4 w-4" /> Device group
            </Button>
            <Button onClick={() => setCreatingToken(true)}>
              <KeyRound className="h-4 w-4" /> Enrolment token
            </Button>
          </div>
        ) : null}
      </div>

      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Device</TH>
              <TH>Operating system</TH>
              <TH>Branch</TH>
              <TH>Groups</TH>
              <TH>Last seen</TH>
              <TH>Agent</TH>
              {isAdmin ? <TH className="text-right">Actions</TH> : null}
            </TR>
          </THead>
          <TBody>
            {devices.items.length === 0 ? (
              <EmptyRow colSpan={7}>No devices yet. Create an enrolment token and install the agent with Install-SolvixAgent.ps1.</EmptyRow>
            ) : (
              devices.items.map((d) => {
                const stale = now - new Date(d.lastSeenAtUtc).getTime() > 24 * 3600 * 1000;
                return (
                  <TR key={d.id}>
                    <TD className="font-medium">
                      {d.hostname}
                      {d.dnsDomain ? <span className="block text-xs text-muted-foreground">{d.dnsDomain}</span> : null}
                    </TD>
                    <TD className="text-muted-foreground">
                      {d.osName} {d.osVersion}
                      <span className="block font-mono text-xs">{d.osBuild}</span>
                    </TD>
                    <TD className="text-muted-foreground">{branchName(d.branchId)}</TD>
                    <TD>
                      <div className="flex flex-wrap gap-1">
                        {d.groupIds.map((g) => (
                          <Badge key={g}>{groupName(g)}</Badge>
                        ))}
                      </div>
                    </TD>
                    <TD>
                      <Badge tone={stale ? "warning" : "success"}>{formatRelative(d.lastSeenAtUtc, now)}</Badge>
                    </TD>
                    <TD className="font-mono text-xs text-muted-foreground">{d.agentVersion}</TD>
                    {isAdmin ? (
                      <TD>
                        <div className="flex justify-end gap-1">
                          <Select
                            aria-label={`Add ${d.hostname} to group`}
                            className="h-8 w-36 text-xs"
                            value=""
                            onChange={(e) => e.target.value && void addToGroup(d, e.target.value)}
                          >
                            <option value="">Add to group…</option>
                            {groups
                              .filter((g) => !d.groupIds.includes(g.id))
                              .map((g) => (
                                <option key={g.id} value={g.id}>
                                  {g.name}
                                </option>
                              ))}
                          </Select>
                          <Button variant="ghost" size="icon" className="text-destructive" onClick={() => setRetiring(d)} aria-label={`Retire ${d.hostname}`}>
                            <Power className="h-4 w-4" />
                          </Button>
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

      {isAdmin ? (
        <Card>
          <CardHeader>
            <CardTitle>Enrolment tokens</CardTitle>
            <CardDescription>Used once by the agent installer; only a hash is stored.</CardDescription>
          </CardHeader>
          <Table>
            <THead>
              <TR>
                <TH>Name</TH>
                <TH>Uses</TH>
                <TH>Expires</TH>
                <TH>Status</TH>
                <TH className="text-right">Actions</TH>
              </TR>
            </THead>
            <TBody>
              {tokens.length === 0 ? (
                <EmptyRow colSpan={5}>No enrolment tokens.</EmptyRow>
              ) : (
                tokens.map((t) => {
                  const expired = new Date(t.expiresAtUtc).getTime() < now;
                  return (
                    <TR key={t.id}>
                      <TD className="font-medium">{t.name}</TD>
                      <TD>
                        {t.useCount}
                        {t.maxUses ? ` / ${t.maxUses}` : ""}
                      </TD>
                      <TD className="text-muted-foreground">{new Date(t.expiresAtUtc).toLocaleString()}</TD>
                      <TD>
                        <Badge tone={t.isRevoked || expired ? "neutral" : "success"}>{t.isRevoked ? "Revoked" : expired ? "Expired" : "Active"}</Badge>
                      </TD>
                      <TD className="text-right">
                        {!t.isRevoked && !expired ? (
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={async () => {
                              await api.post(`enrollment-tokens/${t.id}/revoke`);
                              await reload();
                            }}
                          >
                            Revoke
                          </Button>
                        ) : null}
                      </TD>
                    </TR>
                  );
                })
              )}
            </TBody>
          </Table>
        </Card>
      ) : null}

      <CreateTokenDialog open={creatingToken} onClose={() => setCreatingToken(false)} groups={groups} branches={branches} onCreated={reload} />
      <CreateGroupDialog open={creatingGroup} onClose={() => setCreatingGroup(false)} onCreated={reload} />
      <ConfirmDialog
        open={retiring !== null}
        title={`Retire ${retiring?.hostname ?? ""}?`}
        description="The device's credential is revoked immediately and it is removed from all targeting. Settings already applied stay on the device."
        confirmLabel="Retire device"
        destructive
        busy={busy}
        onClose={() => setRetiring(null)}
        onConfirm={() => retiring && void retire(retiring)}
      />
    </div>
  );
}

function CreateTokenDialog({
  open,
  onClose,
  groups,
  branches,
  onCreated,
}: {
  open: boolean;
  onClose: () => void;
  groups: DeviceGroupDto[];
  branches: { id: string; name: string }[];
  onCreated: () => Promise<void>;
}) {
  const { toast } = useToast();
  const [name, setName] = React.useState("Laptop rollout");
  const [hours, setHours] = React.useState(72);
  const [maxUses, setMaxUses] = React.useState(50);
  const [branchId, setBranchId] = React.useState("");
  const [groupId, setGroupId] = React.useState("");
  const [created, setCreated] = React.useState<CreatedEnrollmentTokenDto | null>(null);
  const [busy, setBusy] = React.useState(false);

  function close() {
    setCreated(null);
    onClose();
  }

  async function submit() {
    setBusy(true);
    try {
      const result = await api.post<CreatedEnrollmentTokenDto>("enrollment-tokens", {
        name,
        validForHours: hours,
        maxUses: maxUses > 0 ? maxUses : null,
        defaultBranchId: branchId || null,
        defaultDepartmentId: null,
        defaultDeviceGroupId: groupId || null,
      });
      setCreated(result);
      await onCreated();
    } catch (e) {
      toast({ tone: "error", title: "Could not create token", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog
      open={open}
      onClose={close}
      title="Create enrolment token"
      footer={
        created ? (
          <Button onClick={close}>Done</Button>
        ) : (
          <>
            <Button variant="outline" onClick={close}>
              Cancel
            </Button>
            <Button onClick={submit} loading={busy}>
              Create
            </Button>
          </>
        )
      }
    >
      {created ? (
        <div className="space-y-3">
          <Alert tone="success" title="Copy the token now">
            It is shown only once. Run on the device (elevated):
          </Alert>
          <pre className="overflow-x-auto rounded-md bg-muted p-3 font-mono text-xs">
            .\Install-SolvixAgent.ps1 -ServerUrl https://… -EnrollmentToken {created.token}
          </pre>
          <Button variant="outline" size="sm" onClick={() => void navigator.clipboard.writeText(created.token)}>
            <Copy className="h-4 w-4" /> Copy token
          </Button>
        </div>
      ) : (
        <div className="space-y-3">
          <Field label="Name" htmlFor="token-name" required>
            <Input id="token-name" value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Valid for (hours)" htmlFor="token-hours">
              <NumberInput id="token-hours" min={1} max={2160} value={hours} onValue={setHours} />
            </Field>
            <Field label="Max uses (0 = unlimited)" htmlFor="token-uses">
              <NumberInput id="token-uses" min={0} value={maxUses} onValue={setMaxUses} />
            </Field>
          </div>
          <Field label="Default branch" htmlFor="token-branch">
            <Select id="token-branch" value={branchId} onChange={(e) => setBranchId(e.target.value)}>
              <option value="">None</option>
              {branches.map((b) => (
                <option key={b.id} value={b.id}>
                  {b.name}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Default device group" htmlFor="token-group">
            <Select id="token-group" value={groupId} onChange={(e) => setGroupId(e.target.value)}>
              <option value="">None</option>
              {groups.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.name}
                </option>
              ))}
            </Select>
          </Field>
        </div>
      )}
    </Dialog>
  );
}

function CreateGroupDialog({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: () => Promise<void> }) {
  const { toast } = useToast();
  const [name, setName] = React.useState("");
  const [busy, setBusy] = React.useState(false);
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="New device group"
      description="Groups are used for policy assignment and pilot rings."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button
            loading={busy}
            disabled={!name.trim()}
            onClick={async () => {
              setBusy(true);
              try {
                await api.post("device-groups", { name, description: null });
                setName("");
                onClose();
                await onCreated();
              } catch (e) {
                toast({ tone: "error", title: "Could not create group", description: messageFrom(e) });
              } finally {
                setBusy(false);
              }
            }}
          >
            Create
          </Button>
        </>
      }
    >
      <Field label="Name" htmlFor="group-name" required>
        <Input id="group-name" value={name} onChange={(e) => setName(e.target.value)} placeholder="Pilot ring" />
      </Field>
    </Dialog>
  );
}
