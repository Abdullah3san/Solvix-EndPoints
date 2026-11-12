"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { Pencil, Plus, Power, Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import type { BranchDto, PagedResult } from "@/lib/api/types";
import { emptyAddress } from "@/lib/api/types";
import { emptyToNull, fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Checkbox } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { Card } from "@/components/ui/card";
import { AddressFields } from "./address-fields";
import { useToast } from "@/components/providers/toast-provider";
import { useNotifications } from "@/components/providers/notifications-provider";
import { useCan } from "@/components/providers/session-provider";

type BranchDraft = {
  name: string;
  code: string;
  isHeadquarters: boolean;
  phone: string;
  email: string;
  timeZone: string;
  address: BranchDto["address"];
};

const emptyDraft: BranchDraft = { name: "", code: "", isHeadquarters: false, phone: "", email: "", timeZone: "", address: emptyAddress };

function toDraft(b: BranchDto): BranchDraft {
  return { name: b.name, code: b.code, isHeadquarters: b.isHeadquarters, phone: b.phone ?? "", email: b.email ?? "", timeZone: b.timeZone ?? "", address: b.address };
}

function toPayload(d: BranchDraft) {
  return {
    name: d.name,
    code: d.code,
    isHeadquarters: d.isHeadquarters,
    phone: emptyToNull(d.phone),
    email: emptyToNull(d.email),
    timeZone: emptyToNull(d.timeZone),
    address: d.address,
  };
}

export function BranchesView({ initial }: { initial: PagedResult<BranchDto> }) {
  const router = useRouter();
  const { toast } = useToast();
  const { notify } = useNotifications();
  const { isAdmin } = useCan();
  const [data, setData] = React.useState(initial);
  const [search, setSearch] = React.useState("");
  const [includeInactive, setIncludeInactive] = React.useState(false);
  const [editing, setEditing] = React.useState<BranchDto | "new" | null>(null);
  const [confirmDelete, setConfirmDelete] = React.useState<BranchDto | null>(null);
  const [busyId, setBusyId] = React.useState<string | null>(null);

  const reload = React.useCallback(
    async (overrides?: { search?: string; includeInactive?: boolean; page?: number }) => {
      const result = await api.get<PagedResult<BranchDto>>("branches", {
        search: overrides?.search ?? search,
        includeInactive: overrides?.includeInactive ?? includeInactive,
        page: overrides?.page ?? 1,
        pageSize: data.pageSize,
      });
      setData(result);
    },
    [search, includeInactive, data.pageSize],
  );

  async function toggleActive(branch: BranchDto) {
    setBusyId(branch.id);
    try {
      await api.post(`branches/${branch.id}/${branch.isActive ? "deactivate" : "activate"}`);
      toast({ tone: "success", title: `Branch ${branch.isActive ? "deactivated" : "activated"}` });
      await reload();
      router.refresh();
    } catch (e) {
      toast({ tone: "error", title: "Could not update branch", description: messageFrom(e) });
    } finally {
      setBusyId(null);
    }
  }

  async function remove(branch: BranchDto) {
    setBusyId(branch.id);
    try {
      await api.delete(`branches/${branch.id}`);
      toast({ tone: "success", title: "Branch deleted" });
      notify({ tone: "warning", title: "Branch deleted", description: `${branch.name} (${branch.code})` });
      setConfirmDelete(null);
      await reload();
      router.refresh();
    } catch (e) {
      toast({ tone: "error", title: "Could not delete branch", description: messageFrom(e) });
    } finally {
      setBusyId(null);
    }
  }

  return (
    <>
      <div className="mb-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <form
          className="flex flex-1 items-center gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            void reload();
          }}
        >
          <Input placeholder="Search by name or code…" value={search} onChange={(e) => setSearch(e.target.value)} className="max-w-xs" aria-label="Search branches" />
          <Button type="submit" variant="outline">
            Search
          </Button>
          <label className="ml-2 flex items-center gap-2 text-sm text-muted-foreground">
            <Checkbox
              checked={includeInactive}
              onChange={(e) => {
                setIncludeInactive(e.target.checked);
                void reload({ includeInactive: e.target.checked });
              }}
            />
            Show inactive
          </label>
        </form>
        {isAdmin ? (
          <Button onClick={() => setEditing("new")}>
            <Plus className="h-4 w-4" /> New branch
          </Button>
        ) : null}
      </div>

      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Name</TH>
              <TH>Code</TH>
              <TH>Location</TH>
              <TH>Time zone</TH>
              <TH>Status</TH>
              {isAdmin ? <TH className="text-right">Actions</TH> : null}
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={6}>No branches found.</EmptyRow>
            ) : (
              data.items.map((branch) => (
                <TR key={branch.id}>
                  <TD className="font-medium">
                    <div className="flex items-center gap-2">
                      {branch.name}
                      {branch.isHeadquarters ? <Badge tone="brand">HQ</Badge> : null}
                    </div>
                  </TD>
                  <TD className="font-mono text-xs">{branch.code}</TD>
                  <TD className="text-muted-foreground">{[branch.address.city, branch.address.countryCode].filter(Boolean).join(", ") || "—"}</TD>
                  <TD className="text-muted-foreground">{branch.timeZone ?? "—"}</TD>
                  <TD>
                    <Badge tone={branch.isActive ? "success" : "neutral"}>{branch.isActive ? "Active" : "Inactive"}</Badge>
                  </TD>
                  {isAdmin ? (
                    <TD>
                      <div className="flex justify-end gap-1">
                        <Button variant="ghost" size="icon" onClick={() => setEditing(branch)} aria-label={`Edit ${branch.name}`}>
                          <Pencil className="h-4 w-4" />
                        </Button>
                        <Button variant="ghost" size="icon" onClick={() => toggleActive(branch)} loading={busyId === branch.id} aria-label={`${branch.isActive ? "Deactivate" : "Activate"} ${branch.name}`}>
                          <Power className="h-4 w-4" />
                        </Button>
                        <Button variant="ghost" size="icon" onClick={() => setConfirmDelete(branch)} aria-label={`Delete ${branch.name}`} className="text-destructive">
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
        {data.totalPages > 1 ? (
          <div className="flex items-center justify-between border-t border-border px-3 py-2 text-sm text-muted-foreground">
            <span>
              Page {data.page} of {data.totalPages} · {data.totalCount} branches
            </span>
            <div className="flex gap-1">
              <Button variant="outline" size="sm" disabled={data.page <= 1} onClick={() => reload({ page: data.page - 1 })}>
                Previous
              </Button>
              <Button variant="outline" size="sm" disabled={data.page >= data.totalPages} onClick={() => reload({ page: data.page + 1 })}>
                Next
              </Button>
            </div>
          </div>
        ) : null}
      </Card>

      {editing ? (
        <BranchDialog
          branch={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={async (saved, created) => {
            setEditing(null);
            toast({ tone: "success", title: created ? "Branch created" : "Branch updated" });
            notify({ tone: "success", title: created ? "Branch created" : "Branch updated", description: `${saved.name} (${saved.code})` });
            await reload();
            router.refresh();
          }}
        />
      ) : null}

      <Dialog
        open={confirmDelete !== null}
        onClose={() => setConfirmDelete(null)}
        title="Delete branch?"
        description={confirmDelete ? `${confirmDelete.name} (${confirmDelete.code}) will be permanently removed. Branches with departments cannot be deleted.` : undefined}
        footer={
          <>
            <Button variant="outline" onClick={() => setConfirmDelete(null)}>
              Cancel
            </Button>
            <Button variant="destructive" loading={busyId === confirmDelete?.id} onClick={() => confirmDelete && remove(confirmDelete)}>
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

function BranchDialog({ branch, onClose, onSaved }: { branch: BranchDto | null; onClose: () => void; onSaved: (saved: BranchDto, created: boolean) => Promise<void> }) {
  const [draft, setDraft] = React.useState<BranchDraft>(branch ? toDraft(branch) : emptyDraft);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);
  const set = (key: keyof BranchDraft) => (event: React.ChangeEvent<HTMLInputElement>) =>
    setDraft((d) => ({ ...d, [key]: event.target.type === "checkbox" ? event.target.checked : event.target.value }));

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      const saved = branch
        ? await api.put<BranchDto>(`branches/${branch.id}`, toPayload(draft))
        : await api.post<BranchDto>("branches", toPayload(draft));
      await onSaved(saved, !branch);
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
      title={branch ? `Edit ${branch.name}` : "New branch"}
      className="max-w-2xl"
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="branch-form" loading={pending}>
            {branch ? "Save changes" : "Create branch"}
          </Button>
        </>
      }
    >
      <form id="branch-form" onSubmit={submit} className="space-y-5">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Name" htmlFor="branch-name" error={errors.name} required>
            <Input id="branch-name" value={draft.name} onChange={set("name")} required aria-invalid={!!errors.name} autoFocus />
          </Field>
          <Field label="Code" htmlFor="branch-code" error={errors.code} hint="Short unique id, e.g. BER-01" required>
            <Input id="branch-code" value={draft.code} onChange={set("code")} required className="uppercase" aria-invalid={!!errors.code} />
          </Field>
          <Field label="Phone" htmlFor="branch-phone" error={errors.phone}>
            <Input id="branch-phone" value={draft.phone} onChange={set("phone")} />
          </Field>
          <Field label="E-mail" htmlFor="branch-email" error={errors.email}>
            <Input id="branch-email" type="email" value={draft.email} onChange={set("email")} aria-invalid={!!errors.email} />
          </Field>
          <Field label="Time zone" htmlFor="branch-tz" error={errors.timeZone} hint="IANA id, optional">
            <Input id="branch-tz" value={draft.timeZone} onChange={set("timeZone")} aria-invalid={!!errors.timeZone} />
          </Field>
          <label className="flex items-center gap-2 self-end pb-2 text-sm">
            <Checkbox checked={draft.isHeadquarters} onChange={set("isHeadquarters")} /> Headquarters
          </label>
        </div>
        <AddressFields value={draft.address} onChange={(address) => setDraft((d) => ({ ...d, address }))} errors={errors} idPrefix="branch-address" />
      </form>
    </Dialog>
  );
}
