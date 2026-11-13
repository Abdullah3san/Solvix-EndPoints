"use client";

import * as React from "react";
import { Lock, Pencil, Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import type { Permission, PermissionDto, RoleDto } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { expandPermissions, groupPermissions, missingPermissions } from "@/lib/permissions";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Textarea } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { useToast } from "@/components/providers/toast-provider";
import { useCan, useSession } from "@/components/providers/session-provider";

export function RolesView({ initialRoles, catalog }: { initialRoles: RoleDto[]; catalog: PermissionDto[] }) {
  const { toast } = useToast();
  const { permissions: actor } = useSession();
  const canManage = useCan("roles.manage");
  const [roles, setRoles] = React.useState(initialRoles);
  const [editing, setEditing] = React.useState<RoleDto | "new" | null>(null);
  const [deleting, setDeleting] = React.useState<RoleDto | null>(null);
  const [busy, setBusy] = React.useState(false);
  const groups = React.useMemo(() => groupPermissions(catalog), [catalog]);

  async function reload() {
    setRoles(await api.get<RoleDto[]>("roles"));
  }

  async function remove(role: RoleDto) {
    setBusy(true);
    try {
      await api.delete(`roles/${role.id}`);
      toast({ tone: "success", title: "Role deleted", description: role.name });
      setDeleting(null);
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not delete role", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-4">
      {canManage ? (
        <div className="flex justify-end">
          <Button onClick={() => setEditing("new")}>
            <Plus className="h-4 w-4" /> New custom role
          </Button>
        </div>
      ) : null}
      <div className="grid gap-4 lg:grid-cols-2">
        {roles.map((role) => {
          const aboveActor = missingPermissions(role.permissions, actor).length > 0;
          return (
            <Card key={role.id}>
              <CardHeader className="flex-row items-start justify-between gap-3">
                <div className="min-w-0">
                  <CardTitle className="flex items-center gap-2">
                    {role.name}
                    {role.isSystem ? (
                      <Badge>
                        <Lock className="mr-1 h-3 w-3" /> Built-in
                      </Badge>
                    ) : (
                      <Badge tone="brand">Custom</Badge>
                    )}
                  </CardTitle>
                  <CardDescription>{role.description ?? "No description."}</CardDescription>
                </div>
                {canManage && !role.isSystem && !aboveActor ? (
                  <div className="flex shrink-0 gap-1">
                    <Button variant="ghost" size="icon" aria-label={`Edit ${role.name}`} onClick={() => setEditing(role)}>
                      <Pencil className="h-4 w-4" />
                    </Button>
                    <Button variant="ghost" size="icon" className="text-destructive" aria-label={`Delete ${role.name}`} onClick={() => setDeleting(role)} disabled={role.memberCount > 0}>
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </div>
                ) : null}
              </CardHeader>
              <CardContent className="space-y-3">
                <p className="text-xs text-muted-foreground">
                  {role.memberCount} member{role.memberCount === 1 ? "" : "s"} · {role.permissions.length} permission{role.permissions.length === 1 ? "" : "s"}
                </p>
                <div className="flex flex-wrap gap-1">
                  {role.permissions.map((p) => (
                    <code key={p} className="rounded bg-muted px-1.5 py-0.5 text-[11px]">
                      {p}
                    </code>
                  ))}
                </div>
              </CardContent>
            </Card>
          );
        })}
      </div>

      {editing ? (
        <RoleEditor
          role={editing === "new" ? null : editing}
          groups={groups}
          catalog={catalog}
          actor={actor}
          onClose={() => setEditing(null)}
          onSaved={async (role) => {
            setEditing(null);
            toast({ tone: "success", title: editing === "new" ? "Role created" : "Role updated", description: role.name });
            await reload();
          }}
        />
      ) : null}

      <Dialog
        open={deleting !== null}
        onClose={() => setDeleting(null)}
        title={`Delete ${deleting?.name ?? "role"}?`}
        footer={
          <>
            <Button variant="outline" onClick={() => setDeleting(null)}>
              Cancel
            </Button>
            <Button variant="destructive" loading={busy} onClick={() => deleting && remove(deleting)}>
              Delete
            </Button>
          </>
        }
      >
        <p className="text-sm text-muted-foreground">Only roles that nobody holds can be deleted.</p>
      </Dialog>
    </div>
  );
}

function RoleEditor({
  role,
  groups,
  catalog,
  actor,
  onClose,
  onSaved,
}: {
  role: RoleDto | null;
  groups: { group: string; items: PermissionDto[] }[];
  catalog: PermissionDto[];
  actor: Permission[];
  onClose: () => void;
  onSaved: (role: RoleDto) => Promise<void>;
}) {
  const [name, setName] = React.useState(role?.name ?? "");
  const [description, setDescription] = React.useState(role?.description ?? "");
  const [selected, setSelected] = React.useState<Set<Permission>>(new Set(role?.permissions ?? []));
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);
  const held = new Set(actor);
  const effective = expandPermissions(selected, catalog);

  function toggle(key: Permission, on: boolean) {
    const next = new Set(selected);
    if (on) next.add(key);
    else next.delete(key);
    setSelected(next);
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      const body = { name, description: description || null, permissions: [...selected] };
      await onSaved(role ? await api.put<RoleDto>(`roles/${role.id}`, body) : await api.post<RoleDto>("roles", body));
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
      title={role ? `Edit ${role.name}` : "New custom role"}
      description="You can only include permissions you hold yourself. Changes apply to every holder on their next request."
      className="max-w-2xl"
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="role-editor" loading={pending} disabled={selected.size === 0 || !name.trim()}>
            Save
          </Button>
        </>
      }
    >
      <form id="role-editor" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <Field label="Name" htmlFor="role-name" error={errors.name} required>
          <Input id="role-name" value={name} onChange={(e) => setName(e.target.value)} required autoFocus />
        </Field>
        <Field label="Description" htmlFor="role-description" error={errors.description}>
          <Textarea id="role-description" value={description} onChange={(e) => setDescription(e.target.value)} rows={2} />
        </Field>
        <fieldset className="space-y-3">
          <legend className="text-sm font-medium">Permissions</legend>
          {errors.permissions ? <p className="text-xs text-destructive">{errors.permissions}</p> : null}
          <div className="max-h-96 space-y-3 overflow-y-auto rounded-md border border-border p-3">
            {groups.map(({ group, items }) => (
              <div key={group}>
                <p className="mb-1 text-xs font-semibold uppercase tracking-wider text-muted-foreground">{group}</p>
                <ul className="space-y-1">
                  {items.map((p) => {
                    const implied = !selected.has(p.key) && effective.has(p.key);
                    const id = `perm-${p.key}`;
                    return (
                      <li key={p.key} className="flex items-start gap-3">
                        <Checkbox
                          id={id}
                          checked={selected.has(p.key) || implied}
                          disabled={implied || (!held.has(p.key) && !selected.has(p.key))}
                          onChange={(e) => toggle(p.key, e.target.checked)}
                          className="mt-0.5"
                        />
                        <label htmlFor={id} className="text-sm">
                          <code className="text-xs">{p.key}</code>
                          <span className="block text-xs text-muted-foreground">
                            {p.description}
                            {implied ? " (included by another permission)" : ""}
                            {!held.has(p.key) ? " You do not hold this permission." : ""}
                          </span>
                        </label>
                      </li>
                    );
                  })}
                </ul>
              </div>
            ))}
          </div>
        </fieldset>
      </form>
    </Dialog>
  );
}
