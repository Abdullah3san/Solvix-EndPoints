"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { CheckCircle2, ClipboardCheck, Plus, ScanLine } from "lucide-react";
import { api } from "@/lib/api/client";
import type { AssetCategory, BranchDto, DepartmentDto, PagedResult, PhysicalAuditDto, PhysicalAuditItemDto, PhysicalAuditResult, ScanResultDto } from "@/lib/api/types";
import { CATEGORIES, auditResultTone, categoryLabel } from "@/lib/assets";
import { emptyToNull, messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Select, Textarea } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { AssetStatusBadge } from "./asset-badges";

function Progress({ audit }: { audit: PhysicalAuditDto }) {
  const done = audit.expected === 0 ? 100 : Math.round(((audit.expected - audit.pending) / audit.expected) * 100);
  return (
    <div className="space-y-1">
      <div className="h-2 overflow-hidden rounded-full bg-muted" role="progressbar" aria-valuenow={done} aria-valuemin={0} aria-valuemax={100} aria-label="Audit progress">
        <div className="h-full bg-primary transition-all" style={{ width: `${done}%` }} />
      </div>
      <p className="text-xs text-muted-foreground">
        {audit.found} found · {audit.missing} missing · {audit.pending} pending · {audit.unexpected} unexpected · {audit.expected} expected
      </p>
    </div>
  );
}

export function AuditsList({ initial, branches, departments }: { initial: PagedResult<PhysicalAuditDto>; branches: BranchDto[]; departments: DepartmentDto[] }) {
  const router = useRouter();
  const { toast } = useToast();
  const [open, setOpen] = React.useState(false);
  const [name, setName] = React.useState("");
  const [branchId, setBranchId] = React.useState("");
  const [departmentId, setDepartmentId] = React.useState("");
  const [category, setCategory] = React.useState<AssetCategory | "">("");
  const [busy, setBusy] = React.useState(false);

  async function start() {
    setBusy(true);
    try {
      const audit = await api.post<PhysicalAuditDto>("asset-audits", {
        name: name.trim(),
        branchId: emptyToNull(branchId),
        departmentId: emptyToNull(departmentId),
        category: category || null,
        notes: null,
      });
      router.push(`/assets/audits/${audit.id}`);
    } catch (e) {
      toast({ tone: "error", title: "Could not start audit", description: messageFrom(e) });
      setBusy(false);
    }
  }

  return (
    <>
      <PageHeader
        title="Physical audits"
        description="Stock-takes: scan asset labels on site; anything not found is reported missing."
        actions={
          <Button onClick={() => setOpen(true)}>
            <Plus className="h-4 w-4" /> Start audit
          </Button>
        }
      />
      <div className="space-y-3">
        {initial.items.length === 0 ? (
          <Card>
            <CardContent className="p-8 text-center text-sm text-muted-foreground">No audits yet.</CardContent>
          </Card>
        ) : (
          initial.items.map((a) => (
            <Card key={a.id}>
              <CardContent className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center">
                <ClipboardCheck className="h-5 w-5 text-muted-foreground" aria-hidden />
                <div className="min-w-0 flex-1">
                  <Link href={`/assets/audits/${a.id}`} className="font-medium hover:underline">
                    {a.name}
                  </Link>{" "}
                  <Badge tone={a.status === "InProgress" ? "warning" : a.status === "Completed" ? "success" : "neutral"}>{a.status === "InProgress" ? "In progress" : a.status}</Badge>
                  <p className="text-xs text-muted-foreground">
                    {[a.branch?.name ?? "All branches", a.department?.name, a.category ? categoryLabel(a.category) : null].filter(Boolean).join(" · ")} · started {formatDate(a.startedAtUtc)}
                    {a.startedBy?.name ? ` by ${a.startedBy.name}` : ""}
                  </p>
                </div>
                <div className="sm:w-72">
                  <Progress audit={a} />
                </div>
              </CardContent>
            </Card>
          ))
        )}
      </div>
      <Dialog
        open={open}
        onClose={() => setOpen(false)}
        title="Start physical audit"
        description="The assets currently registered in the chosen scope (not retired or disposed) become the expected list."
        footer={
          <>
            <Button variant="outline" onClick={() => setOpen(false)}>
              Cancel
            </Button>
            <Button loading={busy} disabled={!name.trim()} onClick={start}>
              Start
            </Button>
          </>
        }
      >
        <div className="space-y-3">
          <Field label="Name" htmlFor="audit-name" required>
            <Input id="audit-name" value={name} onChange={(e) => setName(e.target.value)} maxLength={200} placeholder="Q4 stock-take — Riyadh warehouse" />
          </Field>
          <Field label="Branch" htmlFor="audit-branch">
            <Select id="audit-branch" value={branchId} onChange={(e) => (setBranchId(e.target.value), setDepartmentId(""))}>
              <option value="">All branches</option>
              {branches.map((b) => (
                <option key={b.id} value={b.id}>
                  {b.name}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Department" htmlFor="audit-department">
            <Select id="audit-department" value={departmentId} onChange={(e) => setDepartmentId(e.target.value)}>
              <option value="">All departments</option>
              {departments.filter((d) => !branchId || d.branchId === branchId).map((d) => (
                <option key={d.id} value={d.id}>
                  {d.name}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Category" htmlFor="audit-category">
            <Select id="audit-category" value={category} onChange={(e) => setCategory(e.target.value as AssetCategory | "")}>
              <option value="">All categories</option>
              {CATEGORIES.map((c) => (
                <option key={c.value} value={c.value}>
                  {c.label}
                </option>
              ))}
            </Select>
          </Field>
        </div>
      </Dialog>
    </>
  );
}

export function AuditSession({ initial, initialItems }: { initial: PhysicalAuditDto; initialItems: PagedResult<PhysicalAuditItemDto> }) {
  const router = useRouter();
  const { toast } = useToast();
  const [audit, setAudit] = React.useState(initial);
  const [items, setItems] = React.useState(initialItems);
  const [filter, setFilter] = React.useState<PhysicalAuditResult | "">("");
  const [code, setCode] = React.useState("");
  const [location, setLocation] = React.useState("");
  const [last, setLast] = React.useState<{ ok: boolean; text: string } | null>(null);
  const [completing, setCompleting] = React.useState(false);
  const scanRef = React.useRef<HTMLInputElement>(null);
  const open = audit.status === "InProgress";

  const loadItems = React.useCallback(
    async (result: PhysicalAuditResult | "", page = 1) => {
      setItems(await api.get<PagedResult<PhysicalAuditItemDto>>(`asset-audits/${audit.id}/items`, { result, page, pageSize: 100 }));
    },
    [audit.id],
  );

  async function scan(e: React.FormEvent) {
    e.preventDefault();
    const value = code.trim();
    if (!value) return;
    setCode("");
    try {
      const result = await api.post<ScanResultDto>(`asset-audits/${audit.id}/scan`, { code: value, observedLocation: emptyToNull(location), condition: null, notes: null });
      setAudit(result.audit);
      setLast({
        ok: result.item.result !== "Unexpected",
        text: `${result.item.assetTag} · ${result.item.assetName}${result.alreadyScanned ? " (already scanned)" : ""}${result.item.result === "Unexpected" ? ` — registered at ${result.item.expectedBranch?.name ?? "another location"}` : ""}`,
      });
      await loadItems(filter);
    } catch (err) {
      setLast({ ok: false, text: `${value}: ${messageFrom(err)}` });
    } finally {
      scanRef.current?.focus();
    }
  }

  async function mark(item: PhysicalAuditItemDto, result: PhysicalAuditResult) {
    try {
      await api.put(`asset-audits/${audit.id}/items/${item.id}`, { result, condition: null, notes: null });
      setAudit(await api.get<PhysicalAuditDto>(`asset-audits/${audit.id}`));
      await loadItems(filter);
    } catch (e) {
      toast({ tone: "error", title: "Update failed", description: messageFrom(e) });
    }
  }

  return (
    <>
      <PageHeader
        title={audit.name}
        description={[audit.branch?.name ?? "All branches", audit.department?.name, audit.category ? categoryLabel(audit.category) : null].filter(Boolean).join(" · ")}
        actions={
          open ? (
            <>
              <Button
                variant="outline"
                onClick={async () => {
                  try {
                    setAudit(await api.post<PhysicalAuditDto>(`asset-audits/${audit.id}/cancel`));
                    router.refresh();
                  } catch (e) {
                    toast({ tone: "error", title: "Cancel failed", description: messageFrom(e) });
                  }
                }}
              >
                Cancel audit
              </Button>
              <Button onClick={() => setCompleting(true)}>
                <CheckCircle2 className="h-4 w-4" /> Complete
              </Button>
            </>
          ) : null
        }
      />
      <Card className="mb-4">
        <CardContent className="space-y-4 p-4">
          <Progress audit={audit} />
          {open ? (
            <form onSubmit={scan} className="grid gap-2 sm:grid-cols-[1fr_14rem_auto]">
              <div className="relative">
                <ScanLine className="pointer-events-none absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" aria-hidden />
                <Input
                  ref={scanRef}
                  autoFocus
                  className="pl-8 font-mono"
                  aria-label="Scan asset tag, QR code or serial number"
                  placeholder="Scan or type an asset tag, QR code or serial number, then Enter"
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  autoComplete="off"
                />
              </div>
              <Input aria-label="Observed location" placeholder="Observed location (optional)" value={location} onChange={(e) => setLocation(e.target.value)} />
              <Button type="submit">Record</Button>
            </form>
          ) : (
            <Alert title={`Audit ${audit.status === "Completed" ? "completed" : "cancelled"} ${formatDate(audit.completedAtUtc)}`}>{audit.notes}</Alert>
          )}
          {last ? (
            <p role="status" className={`rounded-md px-3 py-2 text-sm ${last.ok ? "bg-success/10 text-success" : "bg-warning/15 text-warning"}`}>
              {last.text}
            </p>
          ) : null}
        </CardContent>
      </Card>

      <div className="mb-2 flex items-center gap-2">
        <Select
          aria-label="Filter by result"
          className="w-48"
          value={filter}
          onChange={(e) => {
            const v = e.target.value as PhysicalAuditResult | "";
            setFilter(v);
            void loadItems(v);
          }}
        >
          <option value="">All results</option>
          <option value="Pending">Pending</option>
          <option value="Found">Found</option>
          <option value="Missing">Missing</option>
          <option value="Unexpected">Unexpected</option>
        </Select>
        <span className="text-sm text-muted-foreground">{items.totalCount} items</span>
      </div>
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Asset</TH>
              <TH>Status</TH>
              <TH>Expected at</TH>
              <TH>Result</TH>
              <TH>Scanned</TH>
              {open ? <TH /> : null}
            </TR>
          </THead>
          <TBody>
            {items.items.length === 0 ? (
              <EmptyRow colSpan={6}>No items.</EmptyRow>
            ) : (
              items.items.map((i) => (
                <TR key={i.id}>
                  <TD>
                    <Link href={`/assets/${i.assetId}`} prefetch={false} className="font-mono text-xs hover:underline">
                      {i.assetTag}
                    </Link>
                    <p className="text-sm">{i.assetName}</p>
                  </TD>
                  <TD>
                    <AssetStatusBadge status={i.assetStatus} />
                  </TD>
                  <TD className="text-muted-foreground">{[i.expectedBranch?.name, i.expectedLocation].filter(Boolean).join(" · ") || "—"}</TD>
                  <TD>
                    <Badge tone={auditResultTone(i.result)}>{i.result}</Badge>
                    {i.observedLocation ? <p className="text-xs text-muted-foreground">at {i.observedLocation}</p> : null}
                  </TD>
                  <TD className="text-muted-foreground">{i.scannedAtUtc ? formatDate(i.scannedAtUtc) : "—"}</TD>
                  {open ? (
                    <TD className="whitespace-nowrap text-right">
                      {i.expected && i.result !== "Found" ? (
                        <Button size="sm" variant="ghost" onClick={() => mark(i, "Found")}>
                          Found
                        </Button>
                      ) : null}
                      {i.expected && i.result !== "Missing" ? (
                        <Button size="sm" variant="ghost" onClick={() => mark(i, "Missing")}>
                          Missing
                        </Button>
                      ) : null}
                    </TD>
                  ) : null}
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
      {completing ? (
        <CompleteDialog
          audit={audit}
          onClose={() => setCompleting(false)}
          onDone={async (a) => {
            setAudit(a);
            setCompleting(false);
            await loadItems(filter);
            router.refresh();
          }}
        />
      ) : null}
    </>
  );
}

function CompleteDialog({ audit, onClose, onDone }: { audit: PhysicalAuditDto; onClose: () => void; onDone: (audit: PhysicalAuditDto) => Promise<void> }) {
  const { toast } = useToast();
  const [markMissing, setMarkMissing] = React.useState(true);
  const [notes, setNotes] = React.useState("");
  const [busy, setBusy] = React.useState(false);
  return (
    <Dialog
      open
      onClose={onClose}
      title="Complete audit?"
      description={`${audit.pending} pending assets will be recorded as missing. Found assets get today's audit date.`}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Keep scanning
          </Button>
          <Button
            loading={busy}
            onClick={async () => {
              setBusy(true);
              try {
                await onDone(await api.post<PhysicalAuditDto>(`asset-audits/${audit.id}/complete`, { markMissingAssets: markMissing, notes: emptyToNull(notes) }));
                toast({ tone: "success", title: "Audit completed" });
              } catch (e) {
                toast({ tone: "error", title: "Could not complete", description: messageFrom(e) });
                setBusy(false);
              }
            }}
          >
            Complete audit
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        <label className="flex items-start gap-2 text-sm">
          <Checkbox checked={markMissing} onChange={(e) => setMarkMissing(e.target.checked)} className="mt-0.5" />
          <span>Set the status of assets that were not found to “Missing” in the register.</span>
        </label>
        <Field label="Notes" htmlFor="complete-notes">
          <Textarea id="complete-notes" value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={2000} />
        </Field>
      </div>
    </Dialog>
  );
}
