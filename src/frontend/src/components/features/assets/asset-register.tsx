"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { AlertTriangle, ArrowDown, ArrowUp, ArrowUpDown, Download, Filter, GitMerge, Package, PackageCheck, Plus, Printer, Search, ShieldAlert, Wrench, X } from "lucide-react";
import { api, PROXY_PREFIX } from "@/lib/api/client";
import type { AssetCondition, AssetSortField, AssetStatsDto, AssetSummaryDto, BranchDto, BulkAssetAction, DepartmentDto, PagedResult, VendorDto } from "@/lib/api/types";
import {
  CATEGORIES,
  CONDITIONS,
  DEFAULT_FILTERS,
  STATUSES,
  activeFilterCount,
  assigneeLabel,
  categoryLabel,
  formatDay,
  formatMoney,
  registerQuery,
  type RegisterFilters,
} from "@/lib/assets";
import { emptyToNull, messageFrom } from "@/lib/forms";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Select, Textarea } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { AssetStatusBadge, EndpointLinkBadge, WarrantyBadge } from "./asset-badges";

interface Props {
  initial: PagedResult<AssetSummaryDto>;
  initialFilters: RegisterFilters;
  stats: AssetStatsDto;
  branches: BranchDto[];
  departments: DepartmentDto[];
  vendors: VendorDto[];
}

type BulkDialog = Exclude<BulkAssetAction, "Return" | "MarkMissing"> | "Return" | "MarkMissing" | null;

export function AssetRegister({ initial, initialFilters, stats, branches, departments, vendors }: Props) {
  const router = useRouter();
  const { toast } = useToast();
  const [data, setData] = React.useState(initial);
  const [filters, setFilters] = React.useState<RegisterFilters>(initialFilters);
  const [search, setSearch] = React.useState(initialFilters.search);
  const [showFilters, setShowFilters] = React.useState(activeFilterCount(initialFilters) > 0);
  const [loading, setLoading] = React.useState(false);
  const [selected, setSelected] = React.useState<Set<string>>(new Set());
  const [bulk, setBulk] = React.useState<BulkDialog>(null);
  const requestId = React.useRef(0);

  const load = React.useCallback(
    async (next: RegisterFilters, page = 1) => {
      const id = ++requestId.current;
      setLoading(true);
      const query = registerQuery(next, page);
      window.history.replaceState(null, "", query.size ? `/assets?${query}` : "/assets");
      try {
        const params = Object.fromEntries(registerQuery({ ...next, pageSize: next.pageSize }, page));
        const result = await api.get<PagedResult<AssetSummaryDto>>("assets", { ...params, pageSize: next.pageSize, page });
        if (id === requestId.current) setData(result);
      } catch (e) {
        toast({ tone: "error", title: "Could not load assets", description: messageFrom(e) });
      } finally {
        if (id === requestId.current) setLoading(false);
      }
    },
    [toast],
  );

  function update(patch: Partial<RegisterFilters>) {
    const next = { ...filters, ...patch };
    setFilters(next);
    void load(next);
  }

  function sortBy(field: AssetSortField) {
    update(filters.sort === field ? { direction: filters.direction === "Asc" ? "Desc" : "Asc" } : { sort: field, direction: "Asc" });
  }

  const pageIds = data.items.map((a) => a.id);
  const allOnPage = pageIds.length > 0 && pageIds.every((id) => selected.has(id));
  function togglePage() {
    setSelected((s) => {
      const next = new Set(s);
      for (const id of pageIds) {
        if (allOnPage) next.delete(id);
        else next.add(id);
      }
      return next;
    });
  }

  function toggle(id: string) {
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  async function runBulk(action: BulkAssetAction, extra: Record<string, unknown> = {}) {
    try {
      const result = await api.post<{ processed: number }>("assets/bulk", { assetIds: [...selected], action, ...extra });
      toast({ tone: "success", title: `${result.processed} assets updated` });
      setSelected(new Set());
      setBulk(null);
      await load(filters, data.page);
      router.refresh();
    } catch (e) {
      toast({ tone: "error", title: "Bulk action failed", description: messageFrom(e) });
      throw e;
    }
  }

  const exportUrl = `${PROXY_PREFIX}/assets/export?${registerQuery({ ...filters, pageSize: DEFAULT_FILTERS.pageSize })}`;
  const filterCount = activeFilterCount(filters);
  const tiles = [
    { label: "Total assets", value: stats.total, icon: Package, patch: { status: "" } as Partial<RegisterFilters> },
    { label: "In use", value: stats.inUse, icon: PackageCheck, patch: { status: "InUse" } as Partial<RegisterFilters> },
    { label: "In stock", value: stats.inStock, icon: Package, patch: { status: "InStock" } as Partial<RegisterFilters> },
    { label: "In maintenance", value: stats.inMaintenance, icon: Wrench, patch: { status: "InMaintenance" } as Partial<RegisterFilters> },
    { label: "Missing", value: stats.missing, icon: ShieldAlert, patch: { status: "Missing" } as Partial<RegisterFilters> },
    { label: "Warranty expiring", value: stats.warrantyExpiringSoon, icon: AlertTriangle, patch: { status: "", warranty: "ExpiringSoon" } as Partial<RegisterFilters> },
  ];

  return (
    <>
      <div className="mb-4 grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-6">
        {tiles.map((tile) => (
          <button
            key={tile.label}
            type="button"
            onClick={() => {
              setShowFilters(true);
              update(tile.patch);
            }}
            className="rounded-lg border border-border bg-card p-4 text-left shadow-xs transition-colors hover:border-primary/50"
          >
            <tile.icon className="mb-2 h-4 w-4 text-muted-foreground" aria-hidden />
            <p className="text-xl font-semibold leading-none">{tile.value}</p>
            <p className="mt-1 text-xs text-muted-foreground">{tile.label}</p>
          </button>
        ))}
      </div>

      {stats.pendingEndpointMatches > 0 ? (
        <Link href="/assets/matching" className="mb-4 flex items-center gap-2 rounded-md border border-warning/40 bg-warning/10 px-3 py-2 text-sm hover:bg-warning/15">
          <GitMerge className="h-4 w-4 text-warning" aria-hidden />
          <span>
            <strong>{stats.pendingEndpointMatches}</strong> managed endpoint {stats.pendingEndpointMatches === 1 ? "match is" : "matches are"} waiting for review.
          </span>
        </Link>
      ) : null}

      <div className="mb-3 flex flex-col gap-2 lg:flex-row lg:items-center">
        <form
          className="flex flex-1 gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            update({ search: search.trim() });
          }}
        >
          <div className="relative flex-1 lg:max-w-md">
            <Search className="pointer-events-none absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" aria-hidden />
            <Input
              className="pl-8"
              placeholder="Search tag, name, serial, model, holder, PO…"
              aria-label="Search assets"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>
          <Button type="submit" variant="outline" loading={loading}>
            Search
          </Button>
        </form>
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" onClick={() => setShowFilters((v) => !v)} aria-expanded={showFilters} aria-controls="asset-filters">
            <Filter className="h-4 w-4" /> Filters{filterCount ? <Badge tone="brand">{filterCount}</Badge> : null}
          </Button>
          <a href={exportUrl} className={buttonClasses({ variant: "outline" })} download="asset-register.csv">
            <Download className="h-4 w-4" /> Export CSV
          </a>
          <Link href="/assets/new" className={buttonClasses()}>
            <Plus className="h-4 w-4" /> Register asset
          </Link>
        </div>
      </div>

      {showFilters ? (
        <Card id="asset-filters" className="mb-3">
          <CardContent className="grid gap-3 p-4 sm:grid-cols-2 lg:grid-cols-5">
            <FilterSelect label="Category" value={filters.category} onChange={(v) => update({ category: v as RegisterFilters["category"] })} options={CATEGORIES.map((c) => [c.value, c.label])} />
            <FilterSelect label="Status" value={filters.status} onChange={(v) => update({ status: v as RegisterFilters["status"] })} options={STATUSES.map((s) => [s.value, s.label])} />
            <FilterSelect label="Condition" value={filters.condition} onChange={(v) => update({ condition: v as RegisterFilters["condition"] })} options={CONDITIONS.map((c) => [c, c])} />
            <FilterSelect label="Branch" value={filters.branchId} onChange={(v) => update({ branchId: v, departmentId: "" })} options={branches.map((b) => [b.id, b.name])} />
            <FilterSelect
              label="Department"
              value={filters.departmentId}
              onChange={(v) => update({ departmentId: v })}
              options={departments.filter((d) => !filters.branchId || d.branchId === filters.branchId).map((d) => [d.id, d.name])}
            />
            <FilterSelect label="Vendor" value={filters.vendorId} onChange={(v) => update({ vendorId: v })} options={vendors.map((v) => [v.id, v.name])} />
            <FilterSelect
              label="Warranty"
              value={filters.warranty}
              onChange={(v) => update({ warranty: v as RegisterFilters["warranty"] })}
              options={[
                ["Active", "Active"],
                ["ExpiringSoon", "Expiring soon"],
                ["Expired", "Expired"],
                ["None", "No warranty"],
              ]}
            />
            <FilterSelect
              label="Managed endpoint"
              value={filters.endpointLink}
              onChange={(v) => update({ endpointLink: v as RegisterFilters["endpointLink"] })}
              options={[
                ["Linked", "Linked to endpoint"],
                ["Suggested", "Match to review"],
                ["Unlinked", "Agent-capable, not linked"],
                ["NotApplicable", "No agent (switches, printers…)"],
              ]}
            />
            <FilterSelect
              label="Assignment"
              value={filters.assigned}
              onChange={(v) => update({ assigned: v as RegisterFilters["assigned"] })}
              options={[
                ["true", "Assigned"],
                ["false", "Unassigned"],
              ]}
            />
            <div className="flex items-end">
              <Button
                variant="ghost"
                disabled={filterCount === 0}
                onClick={() => update({ category: "", status: "", condition: "", branchId: "", departmentId: "", vendorId: "", warranty: "", endpointLink: "", assigned: "" })}
              >
                <X className="h-4 w-4" /> Clear filters
              </Button>
            </div>
          </CardContent>
        </Card>
      ) : null}

      {selected.size > 0 ? (
        <div role="toolbar" aria-label="Bulk actions" className="sticky top-2 z-10 mb-3 flex flex-wrap items-center gap-2 rounded-lg border border-primary/40 bg-card p-2 shadow-md">
          <span className="px-2 text-sm font-medium">{selected.size} selected</span>
          <Button size="sm" variant="outline" onClick={() => setBulk("Transfer")}>
            Transfer…
          </Button>
          <Button size="sm" variant="outline" onClick={() => setBulk("Return")}>
            Check in
          </Button>
          <Button size="sm" variant="outline" onClick={() => setBulk("SetCondition")}>
            Set condition…
          </Button>
          <Button size="sm" variant="outline" onClick={() => setBulk("MarkMissing")}>
            Mark missing
          </Button>
          <Button size="sm" variant="outline" onClick={() => setBulk("Retire")}>
            Retire…
          </Button>
          <Link href={`/assets/labels?ids=${[...selected].join(",")}`} className={buttonClasses({ variant: "outline", size: "sm" })}>
            <Printer className="h-4 w-4" /> Print labels
          </Link>
          <Button size="sm" variant="ghost" onClick={() => setSelected(new Set())}>
            Clear selection
          </Button>
        </div>
      ) : null}

      <Card className={cn(loading && "opacity-70 transition-opacity")} aria-busy={loading}>
        <Table>
          <THead>
            <TR>
              <TH className="w-8">
                <Checkbox aria-label="Select all assets on this page" checked={allOnPage} onChange={togglePage} />
              </TH>
              <SortHeader field="AssetTag" label="Asset" filters={filters} onSort={sortBy} />
              <SortHeader field="Status" label="Status" filters={filters} onSort={sortBy} />
              <TH>Assigned to</TH>
              <TH>Location</TH>
              <SortHeader field="WarrantyEndDate" label="Warranty" filters={filters} onSort={sortBy} />
              <TH>Endpoint</TH>
              <SortHeader field="PurchaseCost" label="Cost" filters={filters} onSort={sortBy} className="text-right" />
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={8}>
                {filterCount || filters.search ? "No assets match these filters." : "No assets yet. Register your first asset, or register managed endpoints from Endpoint matching."}
              </EmptyRow>
            ) : (
              data.items.map((a) => (
                <TR key={a.id} data-state={selected.has(a.id) ? "selected" : undefined} className={selected.has(a.id) ? "bg-primary/5" : undefined}>
                  <TD>
                    <Checkbox aria-label={`Select ${a.assetTag}`} checked={selected.has(a.id)} onChange={() => toggle(a.id)} />
                  </TD>
                  <TD>
                    <Link href={`/assets/${a.id}`} prefetch={false} className="font-mono text-xs font-medium hover:underline">
                      {a.assetTag}
                    </Link>
                    <p className="max-w-60 truncate font-medium">{a.name}</p>
                    <p className="max-w-60 truncate text-xs text-muted-foreground">
                      {categoryLabel(a.category)} · {[a.manufacturer, a.model].filter(Boolean).join(" ") || "—"}
                      {a.serialNumber ? ` · ${a.serialNumber}` : ""}
                    </p>
                  </TD>
                  <TD>
                    <AssetStatusBadge status={a.status} />
                    {a.condition === "Damaged" || a.condition === "Poor" ? (
                      <Badge tone="danger" className="ml-1">
                        {a.condition}
                      </Badge>
                    ) : null}
                  </TD>
                  <TD className="max-w-40 truncate">{a.assignee ? assigneeLabel(a.assignee) : <span className="text-muted-foreground">—</span>}</TD>
                  <TD className="max-w-40 text-muted-foreground">
                    <p className="truncate">{a.branch?.name ?? "—"}</p>
                    {a.department || a.location ? <p className="truncate text-xs">{[a.department?.name, a.location].filter(Boolean).join(" · ")}</p> : null}
                  </TD>
                  <TD className="whitespace-nowrap">
                    {a.warranty === "None" ? <span className="text-muted-foreground">—</span> : <WarrantyBadge state={a.warranty} />}
                    {a.warrantyEndDate ? <p className="text-xs text-muted-foreground">{formatDay(a.warrantyEndDate)}</p> : null}
                  </TD>
                  <TD>
                    {a.linkedDeviceId ? (
                      <Link href={`/endpoints/devices/${a.linkedDeviceId}`} prefetch={false} className="hover:underline">
                        <EndpointLinkBadge state={a.endpointLink} />
                      </Link>
                    ) : (
                      <EndpointLinkBadge state={a.endpointLink} />
                    )}
                  </TD>
                  <TD className="whitespace-nowrap text-right tabular-nums">{formatMoney(a.purchaseCost, a.currency)}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
        <div className="flex flex-col gap-2 border-t border-border px-3 py-2 text-sm text-muted-foreground sm:flex-row sm:items-center sm:justify-between">
          <span>
            {data.totalCount === 0 ? "0 assets" : `${(data.page - 1) * data.pageSize + 1}–${Math.min(data.page * data.pageSize, data.totalCount)} of ${data.totalCount} assets`}
          </span>
          <div className="flex items-center gap-2">
            <label className="flex items-center gap-1">
              <span>Rows</span>
              <Select aria-label="Rows per page" className="h-8 w-20" value={filters.pageSize} onChange={(e) => update({ pageSize: Number(e.target.value) })}>
                {[10, 25, 50, 100].map((n) => (
                  <option key={n}>{n}</option>
                ))}
              </Select>
            </label>
            <Button variant="outline" size="sm" disabled={data.page <= 1 || loading} onClick={() => load(filters, data.page - 1)}>
              Previous
            </Button>
            <span className="tabular-nums">
              {data.page} / {Math.max(1, data.totalPages)}
            </span>
            <Button variant="outline" size="sm" disabled={data.page >= data.totalPages || loading} onClick={() => load(filters, data.page + 1)}>
              Next
            </Button>
          </div>
        </div>
      </Card>

      <BulkDialogs key={bulk ?? "none"} action={bulk} count={selected.size} branches={branches} departments={departments} onClose={() => setBulk(null)} onRun={runBulk} />
    </>
  );
}

function FilterSelect({ label, value, onChange, options }: { label: string; value: string; onChange: (value: string) => void; options: [string, string][] }) {
  const id = React.useId();
  return (
    <div className="space-y-1">
      <label htmlFor={id} className="text-xs font-medium text-muted-foreground">
        {label}
      </label>
      <Select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Any</option>
        {options.map(([v, l]) => (
          <option key={v} value={v}>
            {l}
          </option>
        ))}
      </Select>
    </div>
  );
}

function SortHeader({ field, label, filters, onSort, className }: { field: AssetSortField; label: string; filters: RegisterFilters; onSort: (f: AssetSortField) => void; className?: string }) {
  const active = filters.sort === field;
  const Icon = !active ? ArrowUpDown : filters.direction === "Asc" ? ArrowUp : ArrowDown;
  return (
    <TH className={className} aria-sort={active ? (filters.direction === "Asc" ? "ascending" : "descending") : "none"}>
      <button type="button" onClick={() => onSort(field)} className={cn("inline-flex items-center gap-1 uppercase hover:text-foreground", active && "text-foreground")}>
        {label}
        <Icon className="h-3 w-3" aria-hidden />
      </button>
    </TH>
  );
}

function BulkDialogs({
  action,
  count,
  branches,
  departments,
  onClose,
  onRun,
}: {
  action: BulkDialog;
  count: number;
  branches: BranchDto[];
  departments: DepartmentDto[];
  onClose: () => void;
  onRun: (action: BulkAssetAction, extra?: Record<string, unknown>) => Promise<void>;
}) {
  const [busy, setBusy] = React.useState(false);
  const [branchId, setBranchId] = React.useState("");
  const [departmentId, setDepartmentId] = React.useState("");
  const [location, setLocation] = React.useState("");
  const [reason, setReason] = React.useState("");
  const [condition, setCondition] = React.useState<AssetCondition>("Good");

  if (!action) return null;

  async function submit(extra: Record<string, unknown> = {}) {
    setBusy(true);
    try {
      await onRun(action!, extra);
    } catch {
      // toast already shown; keep the dialog open
    } finally {
      setBusy(false);
    }
  }

  const titles: Record<BulkAssetAction, string> = {
    Transfer: `Transfer ${count} assets`,
    Return: `Check in ${count} assets`,
    Retire: `Retire ${count} assets`,
    SetCondition: `Set condition of ${count} assets`,
    MarkMissing: `Mark ${count} assets missing`,
  };
  const footer = (label: string, extra: Record<string, unknown>, disabled = false, destructive = false) => (
    <>
      <Button variant="outline" onClick={onClose}>
        Cancel
      </Button>
      <Button variant={destructive ? "destructive" : "primary"} loading={busy} disabled={disabled} onClick={() => submit(extra)}>
        {label}
      </Button>
    </>
  );

  return (
    <Dialog
      open
      onClose={onClose}
      title={titles[action]}
      description="All selected assets are changed together; if any of them cannot take the action, nothing is changed."
      footer={
        action === "Transfer"
          ? footer("Transfer", { branchId: emptyToNull(branchId), departmentId: emptyToNull(departmentId), location: emptyToNull(location), reason: emptyToNull(reason) })
          : action === "Retire"
            ? footer("Retire", { reason: reason.trim() }, !reason.trim(), true)
            : action === "SetCondition"
              ? footer("Apply", { condition })
              : action === "Return"
                ? footer("Check in", { reason: emptyToNull(reason) })
                : footer("Mark missing", {}, false, true)
      }
    >
      <div className="space-y-3">
        {action === "Transfer" ? (
          <>
            <Field label="Branch" htmlFor="bulk-branch">
              <Select id="bulk-branch" value={branchId} onChange={(e) => (setBranchId(e.target.value), setDepartmentId(""))}>
                <option value="">No branch</option>
                {branches.map((b) => (
                  <option key={b.id} value={b.id}>
                    {b.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Department" htmlFor="bulk-department">
              <Select id="bulk-department" value={departmentId} onChange={(e) => setDepartmentId(e.target.value)}>
                <option value="">No department</option>
                {departments.filter((d) => !branchId || d.branchId === branchId).map((d) => (
                  <option key={d.id} value={d.id}>
                    {d.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Location" htmlFor="bulk-location">
              <Input id="bulk-location" value={location} onChange={(e) => setLocation(e.target.value)} maxLength={200} />
            </Field>
          </>
        ) : null}
        {action === "SetCondition" ? (
          <Field label="Condition" htmlFor="bulk-condition">
            <Select id="bulk-condition" value={condition} onChange={(e) => setCondition(e.target.value as AssetCondition)}>
              {CONDITIONS.map((c) => (
                <option key={c}>{c}</option>
              ))}
            </Select>
          </Field>
        ) : null}
        {action === "Transfer" || action === "Retire" || action === "Return" ? (
          <Field label={action === "Retire" ? "Reason" : "Note"} htmlFor="bulk-reason" required={action === "Retire"}>
            <Textarea id="bulk-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={1000} />
          </Field>
        ) : null}
        {action === "MarkMissing" ? <p className="text-sm text-muted-foreground">They stay in the register and can be marked found again.</p> : null}
      </div>
    </Dialog>
  );
}
