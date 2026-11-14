"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { ChevronDown, ChevronRight, Pencil, Plus, Power, Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import type { BranchDto, DepartmentDto, DepartmentTreeNode, PagedResult } from "@/lib/api/types";
import { emptyToNull, fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select, Textarea } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useNotifications } from "@/components/providers/notifications-provider";
import { useCan } from "@/components/providers/session-provider";
import { cn } from "@/lib/utils";

interface Props {
  branches: BranchDto[];
  initialBranchId: string | null;
  initialDepartments: PagedResult<DepartmentDto>;
  initialTree: DepartmentTreeNode[];
}

export function DepartmentsView({ branches, initialBranchId, initialDepartments, initialTree }: Props) {
  const router = useRouter();
  const { toast } = useToast();
  const { notify } = useNotifications();
  const { isAdmin } = useCan();
  const [branchId, setBranchId] = React.useState(initialBranchId);
  const [departments, setDepartments] = React.useState(initialDepartments);
  const [tree, setTree] = React.useState(initialTree);
  const [editing, setEditing] = React.useState<DepartmentDto | "new" | null>(null);
  const [confirmDelete, setConfirmDelete] = React.useState<DepartmentDto | null>(null);
  const [busyId, setBusyId] = React.useState<string | null>(null);

  const load = React.useCallback(async (id: string | null) => {
    if (!id) {
      setDepartments({ items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 });
      setTree([]);
      return;
    }
    const [list, nodes] = await Promise.all([
      api.get<PagedResult<DepartmentDto>>("departments", { branchId: id, includeInactive: true, pageSize: 100 }),
      api.get<DepartmentTreeNode[]>(`departments/tree/${id}`),
    ]);
    setDepartments(list);
    setTree(nodes);
  }, []);

  async function changeBranch(id: string) {
    setBranchId(id);
    try {
      await load(id);
    } catch (e) {
      toast({ tone: "error", title: "Could not load departments", description: messageFrom(e) });
    }
  }

  async function toggleActive(d: DepartmentDto) {
    setBusyId(d.id);
    try {
      await api.post(`departments/${d.id}/${d.isActive ? "deactivate" : "activate"}`);
      toast({ tone: "success", title: `Department ${d.isActive ? "deactivated" : "activated"}` });
      await load(branchId);
      router.refresh();
    } catch (e) {
      toast({ tone: "error", title: "Could not update department", description: messageFrom(e) });
    } finally {
      setBusyId(null);
    }
  }

  async function remove(d: DepartmentDto) {
    setBusyId(d.id);
    try {
      await api.delete(`departments/${d.id}`);
      toast({ tone: "success", title: "Department deleted" });
      notify({ tone: "warning", title: "Department deleted", description: `${d.name} (${d.code})` });
      setConfirmDelete(null);
      await load(branchId);
      router.refresh();
    } catch (e) {
      toast({ tone: "error", title: "Could not delete department", description: messageFrom(e) });
    } finally {
      setBusyId(null);
    }
  }

  const selectedBranch = branches.find((b) => b.id === branchId);

  return (
    <>
      <div className="mb-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-2">
          <label htmlFor="branch-select" className="text-sm text-muted-foreground">
            Branch
          </label>
          <Select id="branch-select" value={branchId ?? ""} onChange={(e) => changeBranch(e.target.value)} className="w-64">
            {branches.length === 0 ? <option value="">No branches</option> : null}
            {branches.map((b) => (
              <option key={b.id} value={b.id}>
                {b.name} ({b.code})
              </option>
            ))}
          </Select>
        </div>
        {isAdmin && branchId ? (
          <Button onClick={() => setEditing("new")}>
            <Plus className="h-4 w-4" /> New department
          </Button>
        ) : null}
      </div>

      {branches.length === 0 ? (
        <Alert tone="info" title="No branches yet">
          Create a branch first; departments always belong to a branch.
        </Alert>
      ) : null}

      <div className="grid gap-4 lg:grid-cols-[1fr_1.6fr]">
        <Card>
          <CardHeader>
            <CardTitle>Hierarchy</CardTitle>
            <CardDescription>{selectedBranch ? `Departments of ${selectedBranch.name}` : "Select a branch"}</CardDescription>
          </CardHeader>
          <CardContent>
            {tree.length === 0 ? <p className="text-sm text-muted-foreground">No departments in this branch.</p> : <TreeList nodes={tree} depth={0} />}
          </CardContent>
        </Card>

        <Card>
          <Table>
            <THead>
              <TR>
                <TH>Name</TH>
                <TH>Code</TH>
                <TH>Parent</TH>
                <TH>Status</TH>
                {isAdmin ? <TH className="text-right">Actions</TH> : null}
              </TR>
            </THead>
            <TBody>
              {departments.items.length === 0 ? (
                <EmptyRow colSpan={5}>No departments found.</EmptyRow>
              ) : (
                departments.items.map((d) => (
                  <TR key={d.id}>
                    <TD className="font-medium">{d.name}</TD>
                    <TD className="font-mono text-xs">{d.code}</TD>
                    <TD className="text-muted-foreground">{departments.items.find((p) => p.id === d.parentDepartmentId)?.name ?? "—"}</TD>
                    <TD>
                      <Badge tone={d.isActive ? "success" : "neutral"}>{d.isActive ? "Active" : "Inactive"}</Badge>
                    </TD>
                    {isAdmin ? (
                      <TD>
                        <div className="flex justify-end gap-1">
                          <Button variant="ghost" size="icon" onClick={() => setEditing(d)} aria-label={`Edit ${d.name}`}>
                            <Pencil className="h-4 w-4" />
                          </Button>
                          <Button variant="ghost" size="icon" onClick={() => toggleActive(d)} loading={busyId === d.id} aria-label={`${d.isActive ? "Deactivate" : "Activate"} ${d.name}`}>
                            <Power className="h-4 w-4" />
                          </Button>
                          <Button variant="ghost" size="icon" className="text-destructive" onClick={() => setConfirmDelete(d)} aria-label={`Delete ${d.name}`}>
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
      </div>

      {editing && branchId ? (
        <DepartmentDialog
          branchId={branchId}
          department={editing === "new" ? null : editing}
          siblings={departments.items}
          onClose={() => setEditing(null)}
          onSaved={async (saved, created) => {
            setEditing(null);
            toast({ tone: "success", title: created ? "Department created" : "Department updated" });
            notify({ tone: "success", title: created ? "Department created" : "Department updated", description: `${saved.name} (${saved.code})` });
            await load(branchId);
            router.refresh();
          }}
        />
      ) : null}

      <Dialog
        open={confirmDelete !== null}
        onClose={() => setConfirmDelete(null)}
        title="Delete department?"
        description={confirmDelete ? `${confirmDelete.name} (${confirmDelete.code}) will be removed. Departments with children cannot be deleted.` : undefined}
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

function TreeList({ nodes, depth }: { nodes: DepartmentTreeNode[]; depth: number }) {
  return (
    <ul className={cn("space-y-0.5", depth > 0 && "ml-4 border-l border-border pl-3")}>
      {nodes.map((node) => (
        <TreeItem key={node.id} node={node} depth={depth} />
      ))}
    </ul>
  );
}

function TreeItem({ node, depth }: { node: DepartmentTreeNode; depth: number }) {
  const [open, setOpen] = React.useState(true);
  const hasChildren = node.children.length > 0;
  return (
    <li>
      <div className="flex items-center gap-1.5 rounded-md px-1.5 py-1 text-sm hover:bg-muted">
        {hasChildren ? (
          <button type="button" onClick={() => setOpen(!open)} aria-label={open ? "Collapse" : "Expand"} className="rounded p-0.5 text-muted-foreground hover:text-foreground">
            {open ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
          </button>
        ) : (
          <span className="inline-block h-5 w-5" aria-hidden />
        )}
        <span className={cn("font-medium", !node.isActive && "text-muted-foreground line-through")}>{node.name}</span>
        <span className="font-mono text-xs text-muted-foreground">{node.code}</span>
      </div>
      {hasChildren && open ? <TreeList nodes={node.children} depth={depth + 1} /> : null}
    </li>
  );
}

function DepartmentDialog({
  branchId,
  department,
  siblings,
  onClose,
  onSaved,
}: {
  branchId: string;
  department: DepartmentDto | null;
  siblings: DepartmentDto[];
  onClose: () => void;
  onSaved: (saved: DepartmentDto, created: boolean) => Promise<void>;
}) {
  const [draft, setDraft] = React.useState({
    name: department?.name ?? "",
    code: department?.code ?? "",
    description: department?.description ?? "",
    parentDepartmentId: department?.parentDepartmentId ?? "",
  });
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    const payload = {
      name: draft.name,
      code: draft.code,
      description: emptyToNull(draft.description),
      parentDepartmentId: draft.parentDepartmentId || null,
    };
    try {
      const saved = department
        ? await api.put<DepartmentDto>(`departments/${department.id}`, payload)
        : await api.post<DepartmentDto>("departments", { branchId, ...payload });
      await onSaved(saved, !department);
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  const parentOptions = siblings.filter((s) => s.id !== department?.id);

  return (
    <Dialog
      open
      onClose={onClose}
      title={department ? `Edit ${department.name}` : "New department"}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="department-form" loading={pending}>
            {department ? "Save changes" : "Create department"}
          </Button>
        </>
      }
    >
      <form id="department-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Name" htmlFor="dept-name" error={errors.name} required>
            <Input id="dept-name" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} required aria-invalid={!!errors.name} autoFocus />
          </Field>
          <Field label="Code" htmlFor="dept-code" error={errors.code} hint="Unique within the branch" required>
            <Input id="dept-code" value={draft.code} onChange={(e) => setDraft({ ...draft, code: e.target.value })} required className="uppercase" aria-invalid={!!errors.code} />
          </Field>
        </div>
        <Field label="Parent department" htmlFor="dept-parent" error={errors.parentDepartmentId}>
          <Select id="dept-parent" value={draft.parentDepartmentId} onChange={(e) => setDraft({ ...draft, parentDepartmentId: e.target.value })}>
            <option value="">— None (top level) —</option>
            {parentOptions.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name} ({p.code})
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Description" htmlFor="dept-description" error={errors.description}>
          <Textarea id="dept-description" value={draft.description} onChange={(e) => setDraft({ ...draft, description: e.target.value })} />
        </Field>
      </form>
    </Dialog>
  );
}
