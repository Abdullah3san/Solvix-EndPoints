"use client";

import * as React from "react";
import Link from "next/link";
import { ClipboardList, FileUp, MoreHorizontal, Plus, SearchX, Trash2, Upload } from "lucide-react";
import { api } from "@/lib/api/client";
import type {
  BranchDto,
  DepartmentDto,
  ImportStagedComputersResult,
  PagedResult,
  StagedComputerDto,
  StagedComputerStatus,
  StagedComputerSummaryDto,
} from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { MAX_IMPORT, parseHostnames } from "@/lib/hostnames";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Badge, StatusBadge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable, FacetedFilter, ResetFiltersButton, type ColumnDef, type RowSelectionState } from "@/components/ui/data-table";
import { Dialog } from "@/components/ui/dialog";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { EmptyState } from "@/components/ui/empty-state";
import { Field } from "@/components/ui/field";
import { Input, SearchInput, Select, Textarea } from "@/components/ui/input";
import { MetricCard } from "@/components/ui/metric-card";
import { useNotifications } from "@/components/providers/notifications-provider";
import { useCan } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";
import { DownloadAgentButton } from "./download-agent";

const SEARCH_DEBOUNCE_MS = 300;
const PAGE_SIZES = [10, 25, 50, 100];

interface Query {
  search: string;
  status: StagedComputerStatus | "";
  branchId: string;
  page: number;
  pageSize: number;
}

const statusTone = { Pending: "warning", Enrolled: "success", Retired: "neutral" } as const;
const statusLabel = { Pending: "Agent not installed", Enrolled: "Enrolled", Retired: "Retired" } as const;
const sourceLabel = { Manual: "Added manually", Import: "Imported", Directory: "Directory" } as const;

export function StagedComputersView({
  initial,
  summary,
  branches,
  departments,
  initialStatus = "",
}: {
  initial: PagedResult<StagedComputerDto>;
  summary: StagedComputerSummaryDto;
  branches: BranchDto[];
  departments: DepartmentDto[];
  initialStatus?: StagedComputerStatus | "";
}) {
  const { toast } = useToast();
  const { notify } = useNotifications();
  const { isAdmin } = useCan();
  const [data, setData] = React.useState(initial);
  const [counts, setCounts] = React.useState(summary);
  const [query, setQuery] = React.useState<Query>({ search: "", status: initialStatus, branchId: "", page: 1, pageSize: initial.pageSize });
  const [searchInput, setSearchInput] = React.useState("");
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [selection, setSelection] = React.useState<RowSelectionState>({});
  const [adding, setAdding] = React.useState(false);
  const [importing, setImporting] = React.useState(false);
  const [removing, setRemoving] = React.useState<StagedComputerDto[] | null>(null);
  const requestId = React.useRef(0);
  const loadedQuery = React.useRef(query);

  const load = React.useCallback(async (state: Query) => {
    const id = ++requestId.current;
    setLoading(true);
    try {
      const result = await api.get<PagedResult<StagedComputerDto>>("staged-computers", {
        search: state.search.trim() || undefined,
        status: state.status || undefined,
        branchId: state.branchId || undefined,
        page: state.page,
        pageSize: state.pageSize,
      });
      if (id !== requestId.current) return;
      if (result.items.length === 0 && result.totalCount > 0 && state.page > 1) {
        setQuery((q) => ({ ...q, page: Math.max(1, result.totalPages) }));
        return;
      }
      setData(result);
      setError(null);
    } catch (e) {
      if (id !== requestId.current) return;
      setError(messageFrom(e, "The list could not be loaded."));
    } finally {
      if (id === requestId.current) setLoading(false);
    }
  }, []);

  React.useEffect(() => {
    if (loadedQuery.current === query) return;
    loadedQuery.current = query;
    void load(query);
  }, [query, load]);

  React.useEffect(() => {
    if (searchInput === query.search) return;
    const timer = window.setTimeout(() => {
      setSelection({});
      setQuery((q) => ({ ...q, page: 1, search: searchInput }));
    }, SEARCH_DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [searchInput, query.search]);

  function update(patch: Partial<Query>) {
    setSelection({});
    setQuery((q) => ({ ...q, page: 1, ...patch }));
  }

  async function refresh() {
    setSelection({});
    await Promise.all([load(query), api.get<StagedComputerSummaryDto>("staged-computers/summary").then(setCounts).catch(() => undefined)]);
  }

  async function confirmRemove() {
    if (!removing) return;
    try {
      await api.post("staged-computers/remove", { ids: removing.map((r) => r.id) });
    } catch (e) {
      toast({ tone: "error", title: "Could not remove", description: messageFrom(e) });
      throw e;
    }
    const subject = removing.length === 1 ? removing[0].hostname : `${removing.length} computers`;
    toast({ tone: "success", title: `${subject} removed from the list`, description: "Enrolled computers are not affected." });
    notify({ tone: "info", title: "Staged computers removed", description: subject });
    await refresh();
  }

  const columns = React.useMemo<ColumnDef<StagedComputerDto, unknown>[]>(
    () => [
      {
        id: "hostname",
        enableSorting: false,
        enableHiding: false,
        header: "Computer",
        cell: ({ row: { original: s } }) =>
          s.deviceId ? (
            <Link href={`/endpoints/devices/${s.deviceId}`} className="font-medium hover:text-link hover:underline">
              {s.hostname}
            </Link>
          ) : (
            <span className="font-medium">{s.hostname}</span>
          ),
      },
      {
        id: "placement",
        enableSorting: false,
        header: "Branch / department",
        cell: ({ row: { original: s } }) => {
          const text = [s.branchName, s.departmentName].filter(Boolean).join(" / ");
          return text ? <span className="whitespace-nowrap">{text}</span> : <span className="text-muted-foreground">—</span>;
        },
      },
      {
        id: "status",
        enableSorting: false,
        header: "Status",
        cell: ({ row: { original: s } }) => (
          <span title={s.enrolledAtUtc ? `Enrolled ${formatDate(s.enrolledAtUtc)}` : undefined}>
            <StatusBadge tone={statusTone[s.status]}>{statusLabel[s.status]}</StatusBadge>
          </span>
        ),
      },
      { id: "source", enableSorting: false, header: "Source", cell: ({ row }) => <Badge>{sourceLabel[row.original.source]}</Badge> },
      { id: "added", enableSorting: false, header: "Added", cell: ({ row }) => <span className="whitespace-nowrap text-muted-foreground">{formatDate(row.original.createdAtUtc)}</span> },
      ...(isAdmin
        ? [
            {
              id: "actions",
              enableSorting: false,
              enableHiding: false,
              meta: { align: "right" as const, className: "w-12" },
              header: () => <span className="sr-only">Actions</span>,
              cell: ({ row: { original: s } }: { row: { original: StagedComputerDto } }) => (
                <DropdownMenu>
                  <DropdownMenuTrigger asChild>
                    <Button variant="ghost" size="icon-sm" className="text-muted-foreground" aria-label={`Actions for ${s.hostname}`}>
                      <MoreHorizontal />
                    </Button>
                  </DropdownMenuTrigger>
                  <DropdownMenuContent className="w-52">
                    <DropdownMenuItem tone="danger" onSelect={() => setRemoving([s])}>
                      <Trash2 /> Remove from list…
                    </DropdownMenuItem>
                  </DropdownMenuContent>
                </DropdownMenu>
              ),
            } satisfies ColumnDef<StagedComputerDto, unknown>,
          ]
        : []),
    ],
    [isAdmin],
  );

  const filtered = query.search.trim() !== "" || query.status !== "" || query.branchId !== "" || searchInput.trim() !== "";

  return (
    <>
      <section aria-label="Summary" className="mb-4 grid gap-3 sm:grid-cols-3">
        <MetricCard label="Expected computers" value={counts.total} icon={ClipboardList} tone="brand" caption="On this list" />
        <MetricCard label="Agent not installed" value={counts.pending} icon={ClipboardList} tone={counts.pending > 0 ? "warning" : "neutral"} caption="Waiting for the agent" />
        <MetricCard label="Enrolled" value={counts.enrolled} icon={ClipboardList} tone="success" caption="Agent installed and reporting" />
      </section>

      <DataTable
        label="Staged computers"
        columns={columns}
        data={data.items}
        getRowId={(s) => s.id}
        loading={loading}
        error={error}
        onRetry={() => void load(query)}
        enableSelection={isAdmin}
        rowSelection={selection}
        onRowSelectionChange={setSelection}
        toolbar={() => (
          <>
            <SearchInput value={searchInput} onValueChange={setSearchInput} placeholder="Search computer name…" aria-label="Search staged computers" className="w-full sm:w-64" />
            <FacetedFilter
              title="Status"
              options={[
                { value: "Pending", label: "Agent not installed", count: counts.pending },
                { value: "Enrolled", label: "Enrolled", count: counts.enrolled },
                { value: "Retired", label: "Retired", count: counts.retired },
              ]}
              value={query.status ? [query.status] : []}
              onChange={(v) => update({ status: (v[0] ?? "") as Query["status"] })}
            />
            <FacetedFilter
              title="Branch"
              options={branches.filter((b) => b.isActive).map((b) => ({ value: b.id, label: b.name }))}
              value={query.branchId ? [query.branchId] : []}
              onChange={(v) => update({ branchId: v[0] ?? "" })}
              emptyText="No branches yet"
            />
            {filtered ? (
              <ResetFiltersButton
                onClick={() => {
                  setSearchInput("");
                  update({ search: "", status: "", branchId: "" });
                }}
              />
            ) : null}
            {isAdmin ? (
              <div className="ml-auto flex flex-wrap items-center gap-2">
                <Button variant="outline" size="sm" onClick={() => setImporting(true)}>
                  <Upload /> Import
                </Button>
                <Button variant="outline" size="sm" onClick={() => setAdding(true)}>
                  <Plus /> Add computer
                </Button>
                <DownloadAgentButton size="sm" />
              </div>
            ) : null}
          </>
        )}
        bulkActions={(ids) => (
          <Button
            variant="outline"
            size="sm"
            className="text-destructive"
            onClick={() => setRemoving(data.items.filter((s) => ids.includes(s.id)))}
          >
            <Trash2 /> Remove from list
          </Button>
        )}
        empty={
          filtered ? (
            <EmptyState icon={SearchX} title="No computers match" description="Try a different search or remove a filter." />
          ) : (
            <EmptyState
              icon={ClipboardList}
              title="No expected computers yet"
              description="List the computers that should have the agent, by hand or from a spreadsheet. Ones that haven't installed it yet are flagged, and each is marked enrolled as soon as it reports in."
              action={
                isAdmin ? (
                  <Button size="sm" onClick={() => setImporting(true)}>
                    <Upload /> Import a list
                  </Button>
                ) : undefined
              }
            />
          )
        }
        pagination={{
          page: data.page,
          pageSize: data.pageSize,
          totalCount: data.totalCount,
          itemLabel: "computers",
          pageSizeOptions: PAGE_SIZES,
          onPageChange: (page) => setQuery((q) => ({ ...q, page })),
          onPageSizeChange: (pageSize) => update({ pageSize }),
        }}
      />

      {adding ? (
        <AddDialog
          branches={branches}
          departments={departments}
          onClose={() => setAdding(false)}
          onAdded={async (hostname) => {
            setAdding(false);
            toast({ tone: "success", title: `${hostname} added`, description: "It is marked enrolled as soon as its agent reports in." });
            await refresh();
          }}
        />
      ) : null}
      {importing ? <ImportDialog branches={branches} departments={departments} onClose={() => setImporting(false)} onImported={refresh} /> : null}

      <ConfirmDialog
        open={removing !== null}
        onOpenChange={(open) => (open ? undefined : setRemoving(null))}
        title={removing && removing.length === 1 ? `Remove ${removing[0].hostname}?` : `Remove ${removing?.length ?? 0} computers?`}
        description="They are only removed from this list. Computers that already have the agent stay enrolled and keep reporting."
        confirmLabel={removing && removing.length > 1 ? `Remove ${removing.length} computers` : "Remove from list"}
        onConfirm={confirmRemove}
      />
    </>
  );
}

function PlacementFields({ branches, departments, branchId, departmentId, onChange, errors }: {
  branches: BranchDto[];
  departments: DepartmentDto[];
  branchId: string;
  departmentId: string;
  onChange: (next: { branchId: string; departmentId: string }) => void;
  errors: Record<string, string>;
}) {
  const options = departments.filter((d) => d.branchId === branchId);
  return (
    <div className="grid gap-4 sm:grid-cols-2">
      <Field label="Branch" htmlFor="stg-branch" error={errors.branchId}>
        <Select id="stg-branch" value={branchId} onChange={(e) => onChange({ branchId: e.target.value, departmentId: "" })}>
          <option value="">No branch</option>
          {branches.filter((b) => b.isActive).map((b) => (
            <option key={b.id} value={b.id}>
              {b.name} ({b.code})
            </option>
          ))}
        </Select>
      </Field>
      <Field label="Department" htmlFor="stg-department" error={errors.departmentId}>
        <Select id="stg-department" value={departmentId} onChange={(e) => onChange({ branchId, departmentId: e.target.value })} disabled={!branchId}>
          <option value="">{branchId ? "No department" : "Select a branch first"}</option>
          {options.map((d) => (
            <option key={d.id} value={d.id}>
              {d.name}
            </option>
          ))}
        </Select>
      </Field>
    </div>
  );
}

function AddDialog({ branches, departments, onClose, onAdded }: { branches: BranchDto[]; departments: DepartmentDto[]; onClose: () => void; onAdded: (hostname: string) => Promise<void> }) {
  const [hostname, setHostname] = React.useState("");
  const [placement, setPlacement] = React.useState({ branchId: "", departmentId: "" });
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [formError, setFormError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setFormError(null);
    setErrors({});
    try {
      const created = await api.post<StagedComputerDto>("staged-computers", { hostname: hostname.trim(), branchId: placement.branchId || null, departmentId: placement.departmentId || null });
      await onAdded(created.hostname);
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      setFormError(messageFrom(e, "The computer could not be added."));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open
      onClose={() => (pending ? undefined : onClose())}
      title="Add expected computer"
      description="Add a computer that should have the agent. It shows as not installed until it reports in."
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={pending}>
            Cancel
          </Button>
          <Button type="submit" form="stg-add-form" loading={pending}>
            Add computer
          </Button>
        </>
      }
    >
      <form id="stg-add-form" onSubmit={submit} className="space-y-4 pb-2">
        {formError ? <Alert tone="error">{formError}</Alert> : null}
        <Field label="Computer name" htmlFor="stg-hostname" error={errors.hostname} hint="The Windows computer name, e.g. FIN-LT-0142." required>
          <Input id="stg-hostname" value={hostname} onChange={(e) => setHostname(e.target.value)} autoFocus autoComplete="off" spellCheck={false} className="font-mono" required />
        </Field>
        <PlacementFields branches={branches} departments={departments} branchId={placement.branchId} departmentId={placement.departmentId} onChange={setPlacement} errors={errors} />
      </form>
    </Dialog>
  );
}

function ImportDialog({ branches, departments, onClose, onImported }: { branches: BranchDto[]; departments: DepartmentDto[]; onClose: () => void; onImported: () => Promise<void> }) {
  const { notify } = useNotifications();
  const [text, setText] = React.useState("");
  const [placement, setPlacement] = React.useState({ branchId: "", departmentId: "" });
  const [error, setError] = React.useState<string | null>(null);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [pending, setPending] = React.useState(false);
  const [result, setResult] = React.useState<ImportStagedComputersResult | null>(null);
  const names = React.useMemo(() => parseHostnames(text), [text]);
  const tooMany = names.length > MAX_IMPORT;

  async function onFile(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) return;
    if (file.size > 2 * 1024 * 1024) {
      setError("That file is too large. Use a plain list of computer names (under 2 MB).");
      return;
    }
    setError(null);
    setText(await file.text());
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    setErrors({});
    try {
      const outcome = await api.post<ImportStagedComputersResult>("staged-computers/import", {
        hostnames: names,
        branchId: placement.branchId || null,
        departmentId: placement.departmentId || null,
      });
      setResult(outcome);
      if (outcome.created > 0) notify({ tone: "success", title: "Computers imported", description: `${outcome.created} added to the expected list` });
      await onImported();
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      setError(messageFrom(e, "The import failed."));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open
      onClose={() => (pending ? undefined : onClose())}
      title="Import expected computers"
      description="Paste computer names (one per line) or upload a CSV or text file. The first column is used."
      className="max-w-xl"
      footer={
        result ? (
          <Button onClick={onClose}>Done</Button>
        ) : (
          <>
            <Button variant="outline" onClick={onClose} disabled={pending}>
              Cancel
            </Button>
            <Button type="submit" form="stg-import-form" loading={pending} disabled={names.length === 0 || tooMany}>
              Import {names.length > 0 ? `${names.length} computer${names.length === 1 ? "" : "s"}` : ""}
            </Button>
          </>
        )
      }
    >
      {result ? (
        <div className="space-y-3 pb-2">
          <Alert tone={result.created > 0 ? "success" : "info"} title={`${result.created} added`}>
            {result.alreadyStaged > 0 ? `${result.alreadyStaged} were already on the list or repeated. ` : ""}
            {result.rejected.length > 0 ? `${result.rejected.length} could not be used.` : "Nothing was rejected."}
          </Alert>
          {result.rejected.length > 0 ? (
            <div>
              <p className="mb-1 text-sm font-medium">Not imported</p>
              <ul className="scrollbar-thin max-h-40 divide-y divide-border overflow-y-auto rounded-md border border-border text-sm">
                {result.rejected.map((r, i) => (
                  <li key={`${r.value}-${i}`} className="flex justify-between gap-3 px-3 py-1.5">
                    <span className="truncate font-mono">{r.value}</span>
                    <span className="shrink-0 text-muted-foreground">{r.reason}</span>
                  </li>
                ))}
              </ul>
            </div>
          ) : null}
        </div>
      ) : (
        <form id="stg-import-form" onSubmit={submit} className="space-y-4 pb-2">
          {error ? <Alert tone="error">{error}</Alert> : null}
          <Field label="Computer names" htmlFor="stg-import-text" error={tooMany ? `Import at most ${MAX_IMPORT} computers at a time (found ${names.length}).` : errors.hostnames}>
            <Textarea id="stg-import-text" value={text} onChange={(e) => setText(e.target.value)} rows={8} spellCheck={false} placeholder={"FIN-LT-0142\nFIN-LT-0143\nHR-PC-0007"} className="font-mono text-xs" aria-invalid={tooMany} />
          </Field>
          <div className="flex items-center justify-between gap-3 text-sm">
            <label className="inline-flex cursor-pointer items-center gap-2 text-link hover:underline">
              <FileUp className="size-4" aria-hidden /> Choose a CSV or text file
              <input type="file" accept=".csv,.txt,text/csv,text/plain" className="sr-only" onChange={onFile} />
            </label>
            <span className="text-muted-foreground" aria-live="polite">
              {names.length} found
            </span>
          </div>
          <PlacementFields branches={branches} departments={departments} branchId={placement.branchId} departmentId={placement.departmentId} onChange={setPlacement} errors={errors} />
        </form>
      )}
    </Dialog>
  );
}
