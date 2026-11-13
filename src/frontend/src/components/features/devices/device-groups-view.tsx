"use client";

import * as React from "react";
import Link from "next/link";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import type { DeviceGroupDto, PagedResult } from "@/lib/api/types";
import { emptyToNull, fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Textarea } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";

export function DeviceGroupsView({ initial }: { initial: DeviceGroupDto[] }) {
  const { toast } = useToast();
  const canManage = useCan("devices.manage");
  const [groups, setGroups] = React.useState(initial);
  const [editing, setEditing] = React.useState<DeviceGroupDto | "new" | null>(null);
  const [deleting, setDeleting] = React.useState<DeviceGroupDto | null>(null);
  const [busy, setBusy] = React.useState(false);

  async function reload() {
    setGroups((await api.get<PagedResult<DeviceGroupDto>>("device-groups", { pageSize: 200 })).items);
  }

  async function remove(group: DeviceGroupDto) {
    setBusy(true);
    try {
      await api.delete(`device-groups/${group.id}`);
      toast({ tone: "success", title: "Group deleted" });
      setDeleting(null);
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not delete group", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      {canManage ? (
        <div className="mb-4 flex justify-end">
          <Button onClick={() => setEditing("new")}>
            <Plus className="h-4 w-4" /> New group
          </Button>
        </div>
      ) : null}
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Name</TH>
              <TH>Description</TH>
              <TH>Devices</TH>
              {canManage ? <TH className="text-right">Actions</TH> : null}
            </TR>
          </THead>
          <TBody>
            {groups.length === 0 ? (
              <EmptyRow colSpan={4}>No device groups yet.</EmptyRow>
            ) : (
              groups.map((g) => (
                <TR key={g.id}>
                  <TD className="font-medium">{g.name}</TD>
                  <TD className="text-muted-foreground">{g.description ?? "—"}</TD>
                  <TD>
                    <Link href={`/endpoints/devices?groupId=${g.id}`} className="text-primary hover:underline">
                      {g.deviceCount}
                    </Link>
                  </TD>
                  {canManage ? (
                    <TD>
                      <div className="flex justify-end gap-1">
                        <Button variant="ghost" size="icon" onClick={() => setEditing(g)} aria-label={`Edit ${g.name}`}>
                          <Pencil className="h-4 w-4" />
                        </Button>
                        <Button variant="ghost" size="icon" onClick={() => setDeleting(g)} aria-label={`Delete ${g.name}`} className="text-destructive">
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

      {editing ? (
        <GroupDialog
          group={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={async (created) => {
            setEditing(null);
            toast({ tone: "success", title: created ? "Group created" : "Group updated" });
            await reload();
          }}
        />
      ) : null}
      <Dialog
        open={deleting !== null}
        onClose={() => setDeleting(null)}
        title="Delete group?"
        description={deleting ? `"${deleting.name}" is removed; its devices stay enrolled. Enrollment tokens that targeted it no longer assign a group.` : undefined}
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
        <p className="text-sm text-muted-foreground">This action cannot be undone.</p>
      </Dialog>
    </>
  );
}

function GroupDialog({ group, onClose, onSaved }: { group: DeviceGroupDto | null; onClose: () => void; onSaved: (created: boolean) => Promise<void> }) {
  const [name, setName] = React.useState(group?.name ?? "");
  const [description, setDescription] = React.useState(group?.description ?? "");
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      const body = { name, description: emptyToNull(description) };
      if (group) await api.put(`device-groups/${group.id}`, body);
      else await api.post("device-groups", body);
      await onSaved(!group);
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
      title={group ? `Edit ${group.name}` : "New device group"}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="group-form" loading={pending}>
            {group ? "Save" : "Create"}
          </Button>
        </>
      }
    >
      <form id="group-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <Field label="Name" htmlFor="group-name" error={errors.name} required>
          <Input id="group-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} autoFocus />
        </Field>
        <Field label="Description" htmlFor="group-description" error={errors.description}>
          <Textarea id="group-description" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500} />
        </Field>
      </form>
    </Dialog>
  );
}
