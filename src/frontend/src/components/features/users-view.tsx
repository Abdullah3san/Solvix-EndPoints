"use client";

import * as React from "react";
import { LogOut, Mail, MoreHorizontal, RotateCw, ShieldCheck, Trash2, UserCheck, UserCog, UserPlus, UserX, X } from "lucide-react";
import { api } from "@/lib/api/client";
import type { InvitationDto, InvitationStatus, PagedResult, RoleDto, TenantUserDto } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Menu, MenuItem, MenuPanel, MenuSeparator, MenuTrigger } from "@/components/ui/menu";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useNotifications } from "@/components/providers/notifications-provider";
import { useCan, useSession } from "@/components/providers/session-provider";
import { RolePicker } from "./role-picker";

const invitationTone: Record<InvitationStatus, "brand" | "neutral" | "success" | "warning"> = {
  Pending: "brand",
  Accepted: "success",
  Revoked: "neutral",
  Expired: "warning",
};

type Confirm = { title: string; description: string; action: string; destructive?: boolean; run: () => Promise<unknown> };

export function UsersView({
  initialUsers,
  initialInvitations,
  roles,
}: {
  initialUsers: PagedResult<TenantUserDto>;
  initialInvitations: PagedResult<InvitationDto>;
  roles: RoleDto[] | null;
}) {
  const session = useSession();
  const { toast } = useToast();
  const { notify } = useNotifications();
  const canManage = useCan("users.manage");
  const canRevokeSessions = useCan("security.manage");
  const [users, setUsers] = React.useState(initialUsers);
  const [invitations, setInvitations] = React.useState(initialInvitations);
  const [search, setSearch] = React.useState("");
  const [inviting, setInviting] = React.useState(false);
  const [rolesTarget, setRolesTarget] = React.useState<TenantUserDto | null>(null);
  const [confirm, setConfirm] = React.useState<Confirm | null>(null);
  const [busy, setBusy] = React.useState(false);

  async function reload() {
    const [u, i] = await Promise.all([
      api.get<PagedResult<TenantUserDto>>("users", { pageSize: 200 }),
      api.get<PagedResult<InvitationDto>>("invitations", { pageSize: 100 }),
    ]);
    setUsers(u);
    setInvitations(i);
  }

  async function runConfirmed() {
    if (!confirm) return;
    setBusy(true);
    try {
      await confirm.run();
      toast({ tone: "success", title: confirm.action + " done" });
      setConfirm(null);
      await reload();
    } catch (e) {
      toast({ tone: "error", title: `Could not complete: ${confirm.action.toLowerCase()}`, description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  const term = search.trim().toLowerCase();
  const visible = users.items.filter((u) => !term || u.email.toLowerCase().includes(term) || u.displayName.toLowerCase().includes(term));
  const pending = invitations.items.filter((i) => i.status === "Pending" || i.status === "Expired");

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader className="flex-row flex-wrap items-start justify-between gap-3">
          <div>
            <CardTitle>Members</CardTitle>
            <CardDescription>
              {users.totalCount} member{users.totalCount === 1 ? "" : "s"}. Passwords and MFA belong to each person and cannot be changed here.
            </CardDescription>
          </div>
          <div className="flex items-center gap-2">
            <Input placeholder="Search name or e-mail" value={search} onChange={(e) => setSearch(e.target.value)} className="w-56" aria-label="Search users" />
            {canManage && roles ? (
              <Button onClick={() => setInviting(true)}>
                <UserPlus className="h-4 w-4" /> Invite
              </Button>
            ) : null}
          </div>
        </CardHeader>
        <Table>
          <THead>
            <TR>
              <TH>Name</TH>
              <TH>Roles</TH>
              <TH>Status</TH>
              <TH>Joined</TH>
              {canManage || canRevokeSessions ? <TH className="text-right">Actions</TH> : null}
            </TR>
          </THead>
          <TBody>
            {visible.length === 0 ? (
              <EmptyRow colSpan={5}>No members match.</EmptyRow>
            ) : (
              visible.map((u) => {
                const self = u.userId === session.user.id;
                return (
                  <TR key={u.userId}>
                    <TD>
                      <div className="font-medium">
                        {u.displayName}
                        {self ? <span className="ml-2 text-xs text-muted-foreground">(you)</span> : null}
                      </div>
                      <div className="text-xs text-muted-foreground">{u.email}</div>
                    </TD>
                    <TD>
                      <div className="flex flex-wrap gap-1">
                        {u.roles.map((r) => (
                          <Badge key={r.id} tone={r.isSystem ? "neutral" : "brand"}>
                            {r.name}
                          </Badge>
                        ))}
                      </div>
                    </TD>
                    <TD>
                      <div className="flex flex-wrap gap-1">
                        <Badge tone={u.isActive ? "success" : "warning"}>{u.isActive ? "Active" : "Deactivated"}</Badge>
                        {u.mfaEnabled ? (
                          <Badge tone="success">
                            <ShieldCheck className="mr-1 h-3 w-3" /> MFA
                          </Badge>
                        ) : null}
                      </div>
                    </TD>
                    <TD className="text-muted-foreground">{formatDate(u.joinedAtUtc)}</TD>
                    {canManage || canRevokeSessions ? (
                      <TD className="text-right">
                        {self ? (
                          <span className="text-xs text-muted-foreground">—</span>
                        ) : (
                          <Menu>
                            <MenuTrigger className="h-8 w-8 justify-center rounded-md hover:bg-muted" aria-label={`Actions for ${u.displayName}`}>
                              <MoreHorizontal className="h-4 w-4" />
                            </MenuTrigger>
                            <MenuPanel label={`Actions for ${u.displayName}`} className="w-56">
                              {canManage && roles ? (
                                <MenuItem onSelect={() => setRolesTarget(u)}>
                                  <UserCog className="h-4 w-4" /> Change roles
                                </MenuItem>
                              ) : null}
                              {canManage && u.isActive ? (
                                <MenuItem
                                  onSelect={() =>
                                    setConfirm({
                                      title: `Deactivate ${u.displayName}?`,
                                      description: "They are signed out of this workspace at once and cannot sign in to it until reactivated. Their roles are kept.",
                                      action: "Deactivate",
                                      destructive: true,
                                      run: () => api.post(`users/${u.userId}/deactivate`),
                                    })
                                  }
                                >
                                  <UserX className="h-4 w-4" /> Deactivate
                                </MenuItem>
                              ) : null}
                              {canManage && !u.isActive ? (
                                <MenuItem
                                  onSelect={() =>
                                    setConfirm({
                                      title: `Reactivate ${u.displayName}?`,
                                      description: "They regain the roles they had and can sign in again.",
                                      action: "Reactivate",
                                      run: () => api.post(`users/${u.userId}/activate`),
                                    })
                                  }
                                >
                                  <UserCheck className="h-4 w-4" /> Reactivate
                                </MenuItem>
                              ) : null}
                              {canRevokeSessions ? (
                                <MenuItem
                                  onSelect={() =>
                                    setConfirm({
                                      title: `Sign ${u.displayName} out?`,
                                      description: "Ends every session they have in this workspace. Their sessions in other workspaces are not affected.",
                                      action: "Sign out",
                                      run: () => api.post(`users/${u.userId}/sessions/revoke`),
                                    })
                                  }
                                >
                                  <LogOut className="h-4 w-4" /> Sign out everywhere
                                </MenuItem>
                              ) : null}
                              {canManage ? (
                                <>
                                  <MenuSeparator />
                                  <MenuItem
                                    tone="danger"
                                    onSelect={() =>
                                      setConfirm({
                                        title: `Remove ${u.displayName}?`,
                                        description: "They lose access to this workspace and their roles here. Their account and other workspaces are not affected.",
                                        action: "Remove",
                                        destructive: true,
                                        run: () => api.delete(`users/${u.userId}`),
                                      })
                                    }
                                  >
                                    <Trash2 className="h-4 w-4" /> Remove from workspace
                                  </MenuItem>
                                </>
                              ) : null}
                            </MenuPanel>
                          </Menu>
                        )}
                      </TD>
                    ) : null}
                  </TR>
                );
              })
            )}
          </TBody>
        </Table>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Invitations</CardTitle>
          <CardDescription>Links are sent by e-mail, work once and expire after 7 days.</CardDescription>
        </CardHeader>
        <Table>
          <THead>
            <TR>
              <TH>E-mail</TH>
              <TH>Roles</TH>
              <TH>Status</TH>
              <TH>Expires</TH>
              {canManage ? <TH className="text-right">Actions</TH> : null}
            </TR>
          </THead>
          <TBody>
            {pending.length === 0 ? (
              <EmptyRow colSpan={5}>No open invitations.</EmptyRow>
            ) : (
              pending.map((i) => (
                <TR key={i.id}>
                  <TD className="font-medium">{i.email}</TD>
                  <TD>
                    <div className="flex flex-wrap gap-1">
                      {i.roles.map((r) => (
                        <Badge key={r.id}>{r.name}</Badge>
                      ))}
                    </div>
                  </TD>
                  <TD>
                    <Badge tone={invitationTone[i.status]}>{i.status}</Badge>
                  </TD>
                  <TD className="text-muted-foreground">{formatDate(i.expiresAtUtc)}</TD>
                  {canManage ? (
                    <TD>
                      <div className="flex justify-end gap-1">
                        <Button
                          variant="ghost"
                          size="icon"
                          aria-label={`Send the invitation to ${i.email} again`}
                          onClick={async () => {
                            try {
                              await api.post(`invitations/${i.id}/resend`);
                              toast({ tone: "success", title: "Invitation sent again", description: i.email });
                              await reload();
                            } catch (e) {
                              toast({ tone: "error", title: "Could not resend", description: messageFrom(e) });
                            }
                          }}
                        >
                          <RotateCw className="h-4 w-4" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon"
                          className="text-destructive"
                          aria-label={`Revoke the invitation to ${i.email}`}
                          onClick={() =>
                            setConfirm({
                              title: "Revoke invitation?",
                              description: `The link sent to ${i.email} stops working immediately.`,
                              action: "Revoke",
                              destructive: true,
                              run: () => api.delete(`invitations/${i.id}`),
                            })
                          }
                        >
                          <X className="h-4 w-4" />
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

      {inviting && roles ? (
        <InviteDialog
          roles={roles}
          onClose={() => setInviting(false)}
          onSent={async (invitation) => {
            setInviting(false);
            toast({ tone: "success", title: "Invitation sent", description: invitation.email });
            notify({ tone: "success", title: "Invitation sent", description: invitation.email });
            await reload();
          }}
        />
      ) : null}

      {rolesTarget && roles ? (
        <RolesDialog
          user={rolesTarget}
          roles={roles}
          onClose={() => setRolesTarget(null)}
          onSaved={async (updated) => {
            setRolesTarget(null);
            toast({ tone: "success", title: "Roles updated", description: `${updated.displayName}: ${updated.roles.map((r) => r.name).join(", ")}` });
            await reload();
          }}
        />
      ) : null}

      <Dialog
        open={confirm !== null}
        onClose={() => setConfirm(null)}
        title={confirm?.title ?? ""}
        footer={
          <>
            <Button variant="outline" onClick={() => setConfirm(null)}>
              Cancel
            </Button>
            <Button variant={confirm?.destructive ? "destructive" : "primary"} loading={busy} onClick={runConfirmed}>
              {confirm?.action}
            </Button>
          </>
        }
      >
        <p className="text-sm text-muted-foreground">{confirm?.description}</p>
      </Dialog>
    </div>
  );
}

function InviteDialog({ roles, onClose, onSent }: { roles: RoleDto[]; onClose: () => void; onSent: (i: InvitationDto) => Promise<void> }) {
  const { permissions } = useSession();
  const [email, setEmail] = React.useState("");
  const [displayName, setDisplayName] = React.useState("");
  const [roleIds, setRoleIds] = React.useState<string[]>([]);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      await onSent(await api.post<InvitationDto>("invitations", { email, displayName: displayName || null, roleIds }));
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
      title="Invite someone"
      description="They receive an e-mail with a link to join. Existing Solvix users keep their own password."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="invite-form" loading={pending} disabled={roleIds.length === 0}>
            <Mail className="h-4 w-4" /> Send invitation
          </Button>
        </>
      }
    >
      <form id="invite-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <Field label="E-mail" htmlFor="invite-email" error={errors.email} required>
          <Input id="invite-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoFocus />
        </Field>
        <Field label="Name" htmlFor="invite-name" error={errors.displayName} hint="Optional. Pre-fills the sign-up form.">
          <Input id="invite-name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
        </Field>
        <Field label="Roles" htmlFor="invite-roles" error={errors.roleIds} required>
          <RolePicker roles={roles} selected={roleIds} onChange={setRoleIds} actorPermissions={permissions} idPrefix="invite-role" />
        </Field>
      </form>
    </Dialog>
  );
}

function RolesDialog({ user, roles, onClose, onSaved }: { user: TenantUserDto; roles: RoleDto[]; onClose: () => void; onSaved: (u: TenantUserDto) => Promise<void> }) {
  const { permissions } = useSession();
  const [roleIds, setRoleIds] = React.useState(user.roles.map((r) => r.id));
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      await onSaved(await api.put<TenantUserDto>(`users/${user.userId}/roles`, { roleIds }));
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
      title={`Roles of ${user.displayName}`}
      description="Takes effect on their very next request."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="roles-form" loading={pending} disabled={roleIds.length === 0}>
            Save
          </Button>
        </>
      }
    >
      <form id="roles-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <RolePicker roles={roles} selected={roleIds} onChange={setRoleIds} actorPermissions={permissions} idPrefix="user-role" />
        <p className="text-xs text-muted-foreground">A workspace always keeps at least one active Super Administrator.</p>
      </form>
    </Dialog>
  );
}
