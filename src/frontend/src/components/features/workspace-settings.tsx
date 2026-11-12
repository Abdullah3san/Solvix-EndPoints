"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { Plus, Trash2, UserCog } from "lucide-react";
import { api } from "@/lib/api/client";
import type { MemberDto, PagedResult, TenantDto, TenantRole } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useNotifications } from "@/components/providers/notifications-provider";
import { useCan, useSession } from "@/components/providers/session-provider";
import { formatDate } from "@/lib/utils";

const roleTone: Record<TenantRole, "brand" | "neutral" | "success"> = { Owner: "brand", Admin: "success", Member: "neutral" };

export function WorkspaceSettings({ tenant: initialTenant, members: initialMembers }: { tenant: TenantDto; members: PagedResult<MemberDto> }) {
  const router = useRouter();
  const { toast } = useToast();
  const { notify } = useNotifications();
  const { isAdmin, isOwner } = useCan();
  const { user } = useSession();
  const [tenant, setTenant] = React.useState(initialTenant);
  const [name, setName] = React.useState(initialTenant.name);
  const [tenantError, setTenantError] = React.useState<string | null>(null);
  const [savingTenant, setSavingTenant] = React.useState(false);
  const [members, setMembers] = React.useState(initialMembers);
  const [adding, setAdding] = React.useState(false);
  const [roleTarget, setRoleTarget] = React.useState<MemberDto | null>(null);
  const [removeTarget, setRemoveTarget] = React.useState<MemberDto | null>(null);
  const [busyId, setBusyId] = React.useState<string | null>(null);

  async function reloadMembers() {
    setMembers(await api.get<PagedResult<MemberDto>>("tenant/members", { pageSize: 100 }));
  }

  async function saveTenant(event: React.FormEvent) {
    event.preventDefault();
    setSavingTenant(true);
    setTenantError(null);
    try {
      const updated = await api.put<TenantDto>("tenant", { name });
      setTenant(updated);
      toast({ tone: "success", title: "Workspace renamed" });
      notify({ tone: "info", title: "Workspace renamed", description: updated.name });
      router.refresh();
    } catch (e) {
      setTenantError(messageFrom(e));
    } finally {
      setSavingTenant(false);
    }
  }

  async function removeMember(member: MemberDto) {
    setBusyId(member.membershipId);
    try {
      await api.delete(`tenant/members/${member.membershipId}`);
      toast({ tone: "success", title: "Member removed" });
      notify({ tone: "warning", title: "Member removed", description: member.email });
      setRemoveTarget(null);
      await reloadMembers();
    } catch (e) {
      toast({ tone: "error", title: "Could not remove member", description: messageFrom(e) });
    } finally {
      setBusyId(null);
    }
  }

  return (
    <div className="space-y-6">
      <form onSubmit={saveTenant}>
        <Card>
          <CardHeader>
            <CardTitle>Workspace</CardTitle>
            <CardDescription>Account-level settings. The workspace id is permanent.</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-4 sm:grid-cols-2">
            {tenantError ? (
              <div className="sm:col-span-2">
                <Alert tone="error">{tenantError}</Alert>
              </div>
            ) : null}
            <Field label="Name" htmlFor="tenant-name" required>
              <Input id="tenant-name" value={name} onChange={(e) => setName(e.target.value)} disabled={!isOwner} required />
            </Field>
            <Field label="Workspace id" htmlFor="tenant-slug" hint="Used in sign-in and URLs">
              <Input id="tenant-slug" value={tenant.slug} readOnly disabled className="font-mono" />
            </Field>
            <div className="flex items-center justify-between gap-4 text-sm sm:col-span-2">
              <span className="text-muted-foreground">Status</span>
              <Badge tone={tenant.status === "Active" ? "success" : "warning"}>{tenant.status}</Badge>
            </div>
          </CardContent>
          <CardFooter className="justify-between">
            <p className="text-xs text-muted-foreground">Created {formatDate(tenant.createdAtUtc)}</p>
            {isOwner ? (
              <Button type="submit" loading={savingTenant} disabled={name.trim() === tenant.name}>
                Save
              </Button>
            ) : (
              <p className="text-xs text-muted-foreground">Only the owner can rename the workspace.</p>
            )}
          </CardFooter>
        </Card>
      </form>

      <Card>
        <CardHeader className="flex-row items-start justify-between">
          <div>
            <CardTitle>Members</CardTitle>
            <CardDescription>People who can access this workspace and their roles.</CardDescription>
          </div>
          {isAdmin ? (
            <Button onClick={() => setAdding(true)}>
              <Plus className="h-4 w-4" /> Add member
            </Button>
          ) : null}
        </CardHeader>
        <Table>
          <THead>
            <TR>
              <TH>Name</TH>
              <TH>E-mail</TH>
              <TH>Role</TH>
              <TH>Joined</TH>
              {isAdmin ? <TH className="text-right">Actions</TH> : null}
            </TR>
          </THead>
          <TBody>
            {members.items.length === 0 ? (
              <EmptyRow colSpan={5}>No members.</EmptyRow>
            ) : (
              members.items.map((m) => (
                <TR key={m.membershipId}>
                  <TD className="font-medium">
                    {m.displayName}
                    {m.userId === user.id ? <span className="ml-2 text-xs text-muted-foreground">(you)</span> : null}
                  </TD>
                  <TD className="text-muted-foreground">{m.email}</TD>
                  <TD>
                    <Badge tone={roleTone[m.role]}>{m.role}</Badge>
                  </TD>
                  <TD className="text-muted-foreground">{formatDate(m.joinedAtUtc)}</TD>
                  {isAdmin ? (
                    <TD>
                      <div className="flex justify-end gap-1">
                        <Button variant="ghost" size="icon" onClick={() => setRoleTarget(m)} aria-label={`Change role of ${m.displayName}`} disabled={m.role === "Owner" && !isOwner}>
                          <UserCog className="h-4 w-4" />
                        </Button>
                        <Button variant="ghost" size="icon" className="text-destructive" onClick={() => setRemoveTarget(m)} aria-label={`Remove ${m.displayName}`} disabled={m.userId === user.id}>
                          <Trash2 className="h-4 w-4" />
                        </Button>
                      </div>
                    </TD>
                  ) : null}
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>

      {adding ? (
        <AddMemberDialog
          onClose={() => setAdding(false)}
          onSaved={async (member) => {
            setAdding(false);
            toast({ tone: "success", title: "Member added" });
            notify({ tone: "success", title: "Member added", description: member.email });
            await reloadMembers();
          }}
        />
      ) : null}

      {roleTarget ? (
        <ChangeRoleDialog
          member={roleTarget}
          canAssignOwner={isOwner}
          onClose={() => setRoleTarget(null)}
          onSaved={async (member) => {
            setRoleTarget(null);
            toast({ tone: "success", title: `${member.displayName} is now ${member.role}` });
            await reloadMembers();
          }}
        />
      ) : null}

      <Dialog
        open={removeTarget !== null}
        onClose={() => setRemoveTarget(null)}
        title="Remove member?"
        description={removeTarget ? `${removeTarget.displayName} (${removeTarget.email}) will lose access to this workspace immediately.` : undefined}
        footer={
          <>
            <Button variant="outline" onClick={() => setRemoveTarget(null)}>
              Cancel
            </Button>
            <Button variant="destructive" loading={busyId === removeTarget?.membershipId} onClick={() => removeTarget && removeMember(removeTarget)}>
              Remove
            </Button>
          </>
        }
      >
        <p className="text-sm text-muted-foreground">Their account is kept; only the membership in this workspace is revoked.</p>
      </Dialog>
    </div>
  );
}

function AddMemberDialog({ onClose, onSaved }: { onClose: () => void; onSaved: (member: MemberDto) => Promise<void> }) {
  const [draft, setDraft] = React.useState({ email: "", displayName: "", password: "", role: "Member" as TenantRole });
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      const member = await api.post<MemberDto>("tenant/members", { ...draft, password: draft.password || null });
      await onSaved(member);
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
      title="Add member"
      description="If the e-mail is new to Solvix an account is created with the password you provide; existing users are simply linked."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="add-member-form" loading={pending}>
            Add member
          </Button>
        </>
      }
    >
      <form id="add-member-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <Field label="E-mail" htmlFor="member-email" error={errors.email} required>
          <Input id="member-email" type="email" value={draft.email} onChange={(e) => setDraft({ ...draft, email: e.target.value })} required autoFocus aria-invalid={!!errors.email} />
        </Field>
        <Field label="Name" htmlFor="member-name" error={errors.displayName} required>
          <Input id="member-name" value={draft.displayName} onChange={(e) => setDraft({ ...draft, displayName: e.target.value })} required aria-invalid={!!errors.displayName} />
        </Field>
        <Field label="Initial password" htmlFor="member-password" error={errors.password} hint="Required for new accounts: 10+ characters with a letter and a digit.">
          <Input id="member-password" type="password" value={draft.password} onChange={(e) => setDraft({ ...draft, password: e.target.value })} autoComplete="new-password" aria-invalid={!!errors.password} />
        </Field>
        <Field label="Role" htmlFor="member-role" error={errors.role}>
          <Select id="member-role" value={draft.role} onChange={(e) => setDraft({ ...draft, role: e.target.value as TenantRole })}>
            <option value="Member">Member</option>
            <option value="Admin">Admin</option>
          </Select>
        </Field>
      </form>
    </Dialog>
  );
}

function ChangeRoleDialog({ member, canAssignOwner, onClose, onSaved }: { member: MemberDto; canAssignOwner: boolean; onClose: () => void; onSaved: (m: MemberDto) => Promise<void> }) {
  const [role, setRole] = React.useState<TenantRole>(member.role);
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      await onSaved(await api.patch<MemberDto>(`tenant/members/${member.membershipId}/role`, { role }));
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
      title={`Change role of ${member.displayName}`}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="role-form" loading={pending} disabled={role === member.role}>
            Save
          </Button>
        </>
      }
    >
      <form id="role-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <Field label="Role" htmlFor="role-select">
          <Select id="role-select" value={role} onChange={(e) => setRole(e.target.value as TenantRole)}>
            <option value="Member">Member</option>
            <option value="Admin">Admin</option>
            {canAssignOwner ? <option value="Owner">Owner</option> : null}
          </Select>
        </Field>
        <p className="text-xs text-muted-foreground">A workspace must always keep at least one owner.</p>
      </form>
    </Dialog>
  );
}
