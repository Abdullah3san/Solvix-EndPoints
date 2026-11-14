"use client";

import * as React from "react";
import { api } from "@/lib/api/client";
import type {
  AssetCondition,
  AssetDetailsDto,
  BranchDto,
  DepartmentDto,
  DisposalMethod,
  EndpointDto,
  MaintenanceType,
  TenantUserDto,
  PagedResult,
  VendorDto,
} from "@/lib/api/types";
import { CONDITIONS, DISPOSAL_METHODS, MAINTENANCE_TYPES } from "@/lib/assets";
import { connectivityTone, formatRelative } from "@/lib/devices";
import { emptyToNull, fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Select, Textarea } from "@/components/ui/input";
import { useToast } from "@/components/providers/toast-provider";
import { AssetForm, formFromAsset, toAssetInput, type AssetFormValues } from "./asset-form";

export type AssetDialog = "edit" | "assign" | "return" | "transfer" | "maintenance" | "retire" | "dispose" | "link" | "unlink" | null;

interface CommonProps {
  asset: AssetDetailsDto;
  onClose: () => void;
  onSaved: (asset: AssetDetailsDto) => void;
}

/** Runs a request, shows a toast, maps field errors; returns false on failure. */
function useSubmit(onClose: () => void) {
  const { toast } = useToast();
  const [busy, setBusy] = React.useState(false);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  async function run<T>(request: () => Promise<T>, success: string, then?: (result: T) => void) {
    setBusy(true);
    setErrors({});
    try {
      const result = await request();
      toast({ tone: "success", title: success });
      then?.(result);
      onClose();
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      toast({ tone: "error", title: "Action failed", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }
  return { busy, errors, setErrors, run };
}

function Footer({ onClose, busy, label, onSubmit, destructive, disabled }: { onClose: () => void; busy: boolean; label: string; onSubmit: () => void; destructive?: boolean; disabled?: boolean }) {
  return (
    <>
      <Button variant="outline" onClick={onClose}>
        Cancel
      </Button>
      <Button variant={destructive ? "destructive" : "primary"} loading={busy} disabled={disabled} onClick={onSubmit}>
        {label}
      </Button>
    </>
  );
}

export function EditAssetDialog({ asset, onClose, onSaved, branches, departments, vendors }: CommonProps & { branches: BranchDto[]; departments: DepartmentDto[]; vendors: VendorDto[] }) {
  const [values, setValues] = React.useState<AssetFormValues>(() => formFromAsset(asset));
  const { busy, errors, setErrors, run } = useSubmit(onClose);
  function save() {
    const { input, errors: local } = toAssetInput(values);
    if (Object.keys(local).length) return setErrors(local);
    void run(() => api.put<AssetDetailsDto>(`assets/${asset.summary.id}`, { asset: input, version: asset.version }), "Asset updated", onSaved);
  }
  return (
    <Dialog open onClose={onClose} title={`Edit ${asset.summary.assetTag}`} className="max-w-3xl" footer={<Footer onClose={onClose} busy={busy} label="Save changes" onSubmit={save} />}>
      <AssetForm values={values} onChange={(p) => setValues((v) => ({ ...v, ...p }))} errors={errors} mode="edit" branches={branches} departments={departments} vendors={vendors} />
    </Dialog>
  );
}

export function AssignDialog({ asset, onClose, onSaved, members }: CommonProps & { members: TenantUserDto[] }) {
  const [kind, setKind] = React.useState<"member" | "employee">("member");
  const [userId, setUserId] = React.useState("");
  const [name, setName] = React.useState("");
  const [number, setNumber] = React.useState("");
  const [expected, setExpected] = React.useState("");
  const [notes, setNotes] = React.useState("");
  const { busy, errors, run } = useSubmit(onClose);
  const valid = kind === "member" ? !!userId : !!name.trim();
  return (
    <Dialog
      open
      onClose={onClose}
      title={asset.summary.assignee ? "Re-assign asset" : "Assign asset"}
      description={asset.summary.assignee ? "The current assignment is closed and kept in the history." : undefined}
      footer={
        <Footer
          onClose={onClose}
          busy={busy}
          label="Assign"
          disabled={!valid}
          onSubmit={() =>
            run(
              () =>
                api.post<AssetDetailsDto>(`assets/${asset.summary.id}/assign`, {
                  assignedUserId: kind === "member" ? userId : null,
                  assigneeName: kind === "employee" ? name.trim() : null,
                  employeeNumber: emptyToNull(number),
                  expectedReturnDate: emptyToNull(expected),
                  notes: emptyToNull(notes),
                }),
              "Asset assigned",
              onSaved,
            )
          }
        />
      }
    >
      <div className="space-y-3">
        <div className="flex gap-4 text-sm" role="radiogroup" aria-label="Assignee type">
          <label className="flex items-center gap-2">
            <input type="radio" checked={kind === "member"} onChange={() => setKind("member")} /> Workspace member
          </label>
          <label className="flex items-center gap-2">
            <input type="radio" checked={kind === "employee"} onChange={() => setKind("employee")} /> Employee without a login
          </label>
        </div>
        {kind === "member" ? (
          <Field label="Member" htmlFor="assign-user" error={errors.assignedUserId} required>
            <Select id="assign-user" value={userId} onChange={(e) => setUserId(e.target.value)}>
              <option value="">Choose…</option>
              {members.filter((m) => m.isActive).map((m) => (
                <option key={m.userId} value={m.userId}>
                  {m.displayName} ({m.email})
                </option>
              ))}
            </Select>
          </Field>
        ) : (
          <Field label="Employee name" htmlFor="assign-name" error={errors.assigneeName} required>
            <Input id="assign-name" value={name} onChange={(e) => setName(e.target.value)} maxLength={200} />
          </Field>
        )}
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Employee number" htmlFor="assign-number">
            <Input id="assign-number" value={number} onChange={(e) => setNumber(e.target.value)} maxLength={50} />
          </Field>
          <Field label="Expected return" htmlFor="assign-return">
            <Input id="assign-return" type="date" value={expected} onChange={(e) => setExpected(e.target.value)} />
          </Field>
        </div>
        <Field label="Notes" htmlFor="assign-notes">
          <Textarea id="assign-notes" value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={1000} />
        </Field>
      </div>
    </Dialog>
  );
}

export function ReturnDialog({ asset, onClose, onSaved }: CommonProps) {
  const [condition, setCondition] = React.useState<AssetCondition | "">("");
  const [notes, setNotes] = React.useState("");
  const { busy, run } = useSubmit(onClose);
  return (
    <Dialog
      open
      onClose={onClose}
      title="Check in asset"
      description={`Returned by ${asset.summary.assignee?.displayName ?? "the current holder"}.`}
      footer={
        <Footer
          onClose={onClose}
          busy={busy}
          label="Check in"
          onSubmit={() => run(() => api.post<AssetDetailsDto>(`assets/${asset.summary.id}/return`, { condition: condition || null, notes: emptyToNull(notes) }), "Asset checked in", onSaved)}
        />
      }
    >
      <div className="space-y-3">
        <Field label="Condition on return" htmlFor="return-condition">
          <Select id="return-condition" value={condition} onChange={(e) => setCondition(e.target.value as AssetCondition | "")}>
            <option value="">Unchanged ({asset.summary.condition})</option>
            {CONDITIONS.map((c) => (
              <option key={c}>{c}</option>
            ))}
          </Select>
        </Field>
        <Field label="Notes" htmlFor="return-notes">
          <Textarea id="return-notes" value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={1000} />
        </Field>
      </div>
    </Dialog>
  );
}

export function TransferDialog({ asset, onClose, onSaved, branches, departments }: CommonProps & { branches: BranchDto[]; departments: DepartmentDto[] }) {
  const s = asset.summary;
  const [branchId, setBranchId] = React.useState(s.branch?.id ?? "");
  const [departmentId, setDepartmentId] = React.useState(s.department?.id ?? "");
  const [location, setLocation] = React.useState(s.location ?? "");
  const [reason, setReason] = React.useState("");
  const { busy, errors, run } = useSubmit(onClose);
  return (
    <Dialog
      open
      onClose={onClose}
      title="Transfer asset"
      description="Moves the asset to another branch, department or location. Every transfer is kept in the history."
      footer={
        <Footer
          onClose={onClose}
          busy={busy}
          label="Transfer"
          onSubmit={() =>
            run(
              () => api.post<AssetDetailsDto>(`assets/${s.id}/transfer`, { branchId: emptyToNull(branchId), departmentId: emptyToNull(departmentId), location: emptyToNull(location), reason: emptyToNull(reason) }),
              "Asset transferred",
              onSaved,
            )
          }
        />
      }
    >
      <div className="space-y-3">
        <Field label="Branch" htmlFor="transfer-branch" error={errors.branchId}>
          <Select id="transfer-branch" value={branchId} onChange={(e) => (setBranchId(e.target.value), setDepartmentId(""))}>
            <option value="">No branch</option>
            {branches.map((b) => (
              <option key={b.id} value={b.id}>
                {b.name}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Department" htmlFor="transfer-department" error={errors.departmentId}>
          <Select id="transfer-department" value={departmentId} onChange={(e) => setDepartmentId(e.target.value)}>
            <option value="">No department</option>
            {departments.filter((d) => !branchId || d.branchId === branchId).map((d) => (
              <option key={d.id} value={d.id}>
                {d.name}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Location" htmlFor="transfer-location">
          <Input id="transfer-location" value={location} onChange={(e) => setLocation(e.target.value)} maxLength={200} placeholder="Room, rack or shelf" />
        </Field>
        <Field label="Reason" htmlFor="transfer-reason">
          <Textarea id="transfer-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={1000} />
        </Field>
      </div>
    </Dialog>
  );
}

export function MaintenanceDialog({ asset, onClose, onSaved, vendors }: { asset: AssetDetailsDto; onClose: () => void; onSaved: () => void; vendors: VendorDto[] }) {
  const [type, setType] = React.useState<MaintenanceType>("Repair");
  const [title, setTitle] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [vendorId, setVendorId] = React.useState("");
  const [performedBy, setPerformedBy] = React.useState("");
  const [scheduledFor, setScheduledFor] = React.useState("");
  const [startNow, setStartNow] = React.useState(true);
  const { busy, errors, run } = useSubmit(onClose);
  return (
    <Dialog
      open
      onClose={onClose}
      title="Log maintenance"
      description="Starting work puts the asset in maintenance; completing it restores the previous status."
      footer={
        <Footer
          onClose={onClose}
          busy={busy}
          label={startNow ? "Start maintenance" : "Schedule"}
          disabled={!title.trim()}
          onSubmit={() =>
            run(
              () =>
                api.post(`assets/${asset.summary.id}/maintenance`, {
                  type,
                  title: title.trim(),
                  description: emptyToNull(description),
                  vendorId: emptyToNull(vendorId),
                  performedBy: emptyToNull(performedBy),
                  scheduledFor: emptyToNull(scheduledFor),
                  startNow,
                }),
              startNow ? "Maintenance started" : "Maintenance scheduled",
              onSaved,
            )
          }
        />
      }
    >
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Type" htmlFor="mnt-type">
          <Select id="mnt-type" value={type} onChange={(e) => setType(e.target.value as MaintenanceType)}>
            {MAINTENANCE_TYPES.map((t) => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Title" htmlFor="mnt-title" required error={errors.title}>
          <Input id="mnt-title" value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} />
        </Field>
        <Field label="Vendor" htmlFor="mnt-vendor">
          <Select id="mnt-vendor" value={vendorId} onChange={(e) => setVendorId(e.target.value)}>
            <option value="">In-house</option>
            {vendors.map((v) => (
              <option key={v.id} value={v.id}>
                {v.name}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Performed by" htmlFor="mnt-by">
          <Input id="mnt-by" value={performedBy} onChange={(e) => setPerformedBy(e.target.value)} maxLength={128} />
        </Field>
        <Field label="Scheduled for" htmlFor="mnt-date">
          <Input id="mnt-date" type="date" value={scheduledFor} onChange={(e) => setScheduledFor(e.target.value)} />
        </Field>
        <label className="flex items-center gap-2 self-end pb-2 text-sm">
          <Checkbox checked={startNow} onChange={(e) => setStartNow(e.target.checked)} /> Start now
        </label>
        <Field label="Description" htmlFor="mnt-desc" className="sm:col-span-2">
          <Textarea id="mnt-desc" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={4000} />
        </Field>
      </div>
    </Dialog>
  );
}

export function RetireDialog({ asset, onClose, onSaved }: CommonProps) {
  const [reason, setReason] = React.useState("");
  const { busy, run } = useSubmit(onClose);
  return (
    <Dialog
      open
      onClose={onClose}
      title={`Retire ${asset.summary.assetTag}?`}
      description="The asset is taken out of service; an open assignment is closed. It can be reinstated until it is disposed of."
      footer={
        <Footer
          onClose={onClose}
          busy={busy}
          label="Retire"
          destructive
          disabled={!reason.trim()}
          onSubmit={() => run(() => api.post<AssetDetailsDto>(`assets/${asset.summary.id}/retire`, { reason: reason.trim() }), "Asset retired", onSaved)}
        />
      }
    >
      <Field label="Reason" htmlFor="retire-reason" required>
        <Textarea id="retire-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={1000} placeholder="End of life, beyond economic repair, lost…" />
      </Field>
    </Dialog>
  );
}

export function DisposeDialog({ asset, onClose, onSaved }: CommonProps) {
  const [method, setMethod] = React.useState<DisposalMethod>("Recycled");
  const [date, setDate] = React.useState(() => new Date().toISOString().slice(0, 10));
  const [value, setValue] = React.useState("");
  const [reference, setReference] = React.useState("");
  const [notes, setNotes] = React.useState("");
  const { busy, errors, run } = useSubmit(onClose);
  return (
    <Dialog
      open
      onClose={onClose}
      title={`Dispose of ${asset.summary.assetTag}?`}
      description="Final step of the lifecycle. The record becomes read-only and stays in the register for audit purposes."
      footer={
        <Footer
          onClose={onClose}
          busy={busy}
          label="Record disposal"
          destructive
          onSubmit={() =>
            run(
              () =>
                api.post<AssetDetailsDto>(`assets/${asset.summary.id}/dispose`, {
                  method,
                  disposedAtUtc: date ? new Date(`${date}T12:00:00Z`).toISOString() : null,
                  value: value.trim() ? Number(value) : null,
                  reference: emptyToNull(reference),
                  notes: emptyToNull(notes),
                }),
              "Disposal recorded",
              onSaved,
            )
          }
        />
      }
    >
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Method" htmlFor="dispose-method">
          <Select id="dispose-method" value={method} onChange={(e) => setMethod(e.target.value as DisposalMethod)}>
            {DISPOSAL_METHODS.map((m) => (
              <option key={m.value} value={m.value}>
                {m.label}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Date" htmlFor="dispose-date" error={errors.disposedAtUtc}>
          <Input id="dispose-date" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
        </Field>
        <Field label="Proceeds / value" htmlFor="dispose-value" error={errors.value}>
          <Input id="dispose-value" inputMode="decimal" value={value} onChange={(e) => setValue(e.target.value)} />
        </Field>
        <Field label="Certificate / reference" htmlFor="dispose-ref" hint="e.g. data-destruction certificate number">
          <Input id="dispose-ref" value={reference} onChange={(e) => setReference(e.target.value)} maxLength={100} />
        </Field>
        <Field label="Notes" htmlFor="dispose-notes" className="sm:col-span-2">
          <Textarea id="dispose-notes" value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={1000} />
        </Field>
      </div>
    </Dialog>
  );
}

/** Searches endpoints that have no asset yet and links one manually. */
export function LinkEndpointDialog({ asset, onClose, onSaved }: { asset: AssetDetailsDto; onClose: () => void; onSaved: () => void }) {
  const { toast } = useToast();
  const [search, setSearch] = React.useState(asset.summary.serialNumber ?? "");
  const [results, setResults] = React.useState<EndpointDto[] | null>(null);
  const [note, setNote] = React.useState("");
  const { busy, run } = useSubmit(onClose);

  const find = React.useCallback(
    async (term: string) => {
      try {
        setResults((await api.get<PagedResult<EndpointDto>>("asset-matches/unregistered-endpoints", { search: term, pageSize: 10 })).items);
      } catch (e) {
        toast({ tone: "error", title: "Search failed", description: messageFrom(e) });
      }
    },
    [toast],
  );

  React.useEffect(() => {
    let cancelled = false;
    api
      .get<PagedResult<EndpointDto>>("asset-matches/unregistered-endpoints", { search: asset.summary.serialNumber ?? "", pageSize: 10 })
      .then((r) => !cancelled && setResults(r.items))
      .catch(() => !cancelled && setResults([]));
    return () => {
      cancelled = true;
    };
  }, [asset.summary.serialNumber]);

  return (
    <Dialog open onClose={onClose} title="Link managed endpoint" description="Associate this asset with an enrolled device that has no asset yet. Both records stay separate." className="max-w-2xl">
      <form
        className="mb-3 flex gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          void find(search);
        }}
      >
        <Input aria-label="Search endpoints" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Hostname, serial or model" />
        <Button type="submit" variant="outline">
          Search
        </Button>
      </form>
      <Field label="Note" htmlFor="link-note">
        <Input id="link-note" value={note} onChange={(e) => setNote(e.target.value)} maxLength={1000} placeholder="Why these records belong together" />
      </Field>
      <ul className="mt-3 divide-y divide-border rounded-md border border-border">
        {results === null ? (
          <li className="p-3 text-sm text-muted-foreground">Searching…</li>
        ) : results.length === 0 ? (
          <li className="p-3 text-sm text-muted-foreground">No unlinked endpoints match.</li>
        ) : (
          results.map((d) => (
            <li key={d.id} className="flex items-center justify-between gap-3 p-3 text-sm">
              <div className="min-w-0">
                <p className="font-medium">
                  {d.displayName ?? d.hostname} <Badge tone={connectivityTone(d.isActive ? "Active" : "Retired", d.connectivity)}>{d.isActive ? d.connectivity : "Retired"}</Badge>
                </p>
                <p className="truncate text-xs text-muted-foreground">
                  {[d.manufacturer, d.model].filter(Boolean).join(" ") || "—"} · SN {d.serialNumber ?? "—"} · seen {formatRelative(d.lastSeenAtUtc)}
                </p>
              </div>
              <Button size="sm" loading={busy} onClick={() => run(() => api.put(`assets/${asset.summary.id}/endpoint-link`, { deviceId: d.id, note: emptyToNull(note) }), "Endpoint linked", onSaved)}>
                Link
              </Button>
            </li>
          ))
        )}
      </ul>
    </Dialog>
  );
}

export function UnlinkDialog({ asset, onClose, onSaved }: { asset: AssetDetailsDto; onClose: () => void; onSaved: () => void }) {
  const [note, setNote] = React.useState("");
  const { busy, run } = useSubmit(onClose);
  return (
    <Dialog
      open
      onClose={onClose}
      title="Remove endpoint association?"
      description="The asset and the managed endpoint stay; only the association is removed. The pair will not be suggested again automatically."
      footer={
        <Footer onClose={onClose} busy={busy} label="Remove" destructive onSubmit={() => run(() => api.post(`assets/${asset.summary.id}/endpoint-link/remove`, { note: emptyToNull(note) }), "Association removed", onSaved)} />
      }
    >
      <Field label="Reason" htmlFor="unlink-note">
        <Input id="unlink-note" value={note} onChange={(e) => setNote(e.target.value)} maxLength={1000} />
      </Field>
    </Dialog>
  );
}
