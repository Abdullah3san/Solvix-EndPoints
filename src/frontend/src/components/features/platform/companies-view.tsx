"use client";

import * as React from "react";
import { Building, Check, CircleCheck, Copy, Landmark, MoreHorizontal, Pause, Play, Plus, RefreshCw, SearchX } from "lucide-react";
import { api } from "@/lib/api/client";
import type { CreatePlatformTenantRequest, PagedResult, PlatformSummaryDto, PlatformTenantDto, TenantStatus } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { generatePassword, meetsPasswordPolicy } from "@/lib/passwords";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { StatusBadge, Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable, FacetedFilter, ResetFiltersButton, type ColumnDef } from "@/components/ui/data-table";
import { Dialog } from "@/components/ui/dialog";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { EmptyState } from "@/components/ui/empty-state";
import { Field } from "@/components/ui/field";
import { Input, SearchInput } from "@/components/ui/input";
import { MetricCard } from "@/components/ui/metric-card";
import { useNotifications } from "@/components/providers/notifications-provider";
import { useToast } from "@/components/providers/toast-provider";

const SEARCH_DEBOUNCE_MS = 300;
const PAGE_SIZES = [10, 25, 50, 100];

interface Query {
  search: string;
  status: TenantStatus | "";
  page: number;
  pageSize: number;
}

const statusTone = { Active: "success", Suspended: "warning", PendingDeletion: "danger" } as const;

function slugFromName(name: string): string {
  return name
    .toLowerCase()
    .normalize("NFKD")
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 40);
}

export function CompaniesView({ initial, summary }: { initial: PagedResult<PlatformTenantDto>; summary: PlatformSummaryDto }) {
  const { toast } = useToast();
  const { notify } = useNotifications();
  const [data, setData] = React.useState(initial);
  const [counts, setCounts] = React.useState(summary);
  const [query, setQuery] = React.useState<Query>({ search: "", status: "", page: 1, pageSize: initial.pageSize });
  const [searchInput, setSearchInput] = React.useState("");
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [creating, setCreating] = React.useState(false);
  const [created, setCreated] = React.useState<{ company: PlatformTenantDto; email: string; password: string } | null>(null);
  const [suspending, setSuspending] = React.useState<PlatformTenantDto | null>(null);
  const requestId = React.useRef(0);
  const loadedQuery = React.useRef(query);

  const load = React.useCallback(async (state: Query) => {
    const id = ++requestId.current;
    setLoading(true);
    try {
      const result = await api.get<PagedResult<PlatformTenantDto>>("platform/tenants", {
        search: state.search.trim() || undefined,
        status: state.status || undefined,
        page: state.page,
        pageSize: state.pageSize,
      });
      if (id !== requestId.current) return;
      setData(result);
      setError(null);
    } catch (e) {
      if (id !== requestId.current) return;
      setError(messageFrom(e, "The company list could not be loaded."));
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
    const timer = window.setTimeout(() => setQuery((q) => ({ ...q, page: 1, search: searchInput })), SEARCH_DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [searchInput, query.search]);

  async function refresh() {
    await Promise.all([load(query), api.get<PlatformSummaryDto>("platform/summary").then(setCounts).catch(() => undefined)]);
  }

  async function reactivate(company: PlatformTenantDto) {
    try {
      await api.post<PlatformTenantDto>(`platform/tenants/${company.id}/reactivate`);
      toast({ tone: "success", title: `${company.name} reactivated`, description: "Its users and devices can connect again." });
      notify({ tone: "success", title: "Company reactivated", description: company.name });
      await refresh();
    } catch (e) {
      toast({ tone: "error", title: "Could not reactivate", description: messageFrom(e) });
    }
  }

  async function confirmSuspend() {
    if (!suspending) return;
    try {
      await api.post<PlatformTenantDto>(`platform/tenants/${suspending.id}/suspend`);
    } catch (e) {
      toast({ tone: "error", title: "Could not suspend", description: messageFrom(e) });
      throw e; // keeps the dialog open
    }
    toast({ tone: "success", title: `${suspending.name} suspended`, description: "Nobody from this company can sign in until you reactivate it." });
    notify({ tone: "warning", title: "Company suspended", description: suspending.name });
    await refresh();
  }

  const columns = React.useMemo<ColumnDef<PlatformTenantDto, unknown>[]>(
    () => [
      {
        id: "name",
        enableSorting: false,
        header: "Company",
        cell: ({ row: { original: c } }) => (
          <div className="min-w-44">
            <p className="flex items-center gap-2 font-medium">
              {c.name} {c.isCurrent ? <Badge tone="brand">Your workspace</Badge> : null}
            </p>
            <p className="font-mono text-xs text-muted-foreground">{c.slug}</p>
          </div>
        ),
      },
      {
        id: "owners",
        enableSorting: false,
        header: "Owner",
        cell: ({ row: { original: c } }) =>
          c.ownerEmails.length > 0 ? (
            <span className="whitespace-nowrap">
              {c.ownerEmails[0]}
              {c.ownerEmails.length > 1 ? <span className="ml-1.5 text-muted-foreground">+{c.ownerEmails.length - 1}</span> : null}
            </span>
          ) : (
            <span className="text-muted-foreground">No owner</span>
          ),
      },
      { id: "members", enableSorting: false, header: "Users", meta: { align: "right" }, cell: ({ row }) => <span className="tabular-nums">{row.original.memberCount}</span> },
      { id: "devices", enableSorting: false, header: "Devices", meta: { align: "right" }, cell: ({ row }) => <span className="tabular-nums">{row.original.deviceCount}</span> },
      {
        id: "status",
        enableSorting: false,
        header: "Status",
        cell: ({ row: { original: c } }) => (
          <span title={c.suspendedAtUtc ? `Suspended ${formatDate(c.suspendedAtUtc)}` : undefined}>
            <StatusBadge tone={statusTone[c.status]}>{c.status === "PendingDeletion" ? "Pending deletion" : c.status}</StatusBadge>
          </span>
        ),
      },
      {
        id: "created",
        enableSorting: false,
        header: "Created",
        cell: ({ row }) => <span className="whitespace-nowrap text-muted-foreground">{formatDate(row.original.createdAtUtc)}</span>,
      },
      {
        id: "actions",
        enableSorting: false,
        enableHiding: false,
        meta: { align: "right", className: "w-12" },
        header: () => <span className="sr-only">Actions</span>,
        cell: ({ row: { original: c } }) => (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon-sm" className="text-muted-foreground" aria-label={`Actions for ${c.name}`}>
                <MoreHorizontal />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent className="w-52">
              {c.status === "Suspended" ? (
                <DropdownMenuItem onSelect={() => void reactivate(c)}>
                  <Play /> Reactivate
                </DropdownMenuItem>
              ) : (
                <DropdownMenuItem tone="danger" disabled={c.isCurrent} onSelect={() => setSuspending(c)}>
                  <Pause /> {c.isCurrent ? "Cannot suspend your own workspace" : "Suspend…"}
                </DropdownMenuItem>
              )}
            </DropdownMenuContent>
          </DropdownMenu>
        ),
      },
    ],
    // reactivate/setSuspending only close over stable setters and `refresh` (re-created per render by design).
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [query],
  );

  const filtered = query.search.trim() !== "" || query.status !== "" || searchInput.trim() !== "";

  return (
    <>
      <section aria-label="Summary" className="mb-4 grid gap-3 sm:grid-cols-3">
        <MetricCard label="Companies" value={counts.total} icon={Building} tone="brand" caption="Including your own workspace" />
        <MetricCard label="Active" value={counts.active} icon={CircleCheck} tone="success" caption="Can sign in and connect devices" />
        <MetricCard label="Suspended" value={counts.suspended} icon={Pause} tone={counts.suspended > 0 ? "warning" : "neutral"} caption="Blocked until reactivated" />
      </section>

      {counts.publicRegistrationOpen ? (
        <Alert tone="warning" title="Public registration is open" className="mb-4">
          Anyone who finds the sign-up page can create a company. Set <code className="font-mono text-xs">SOLVIX_ALLOW_PUBLIC_REGISTRATION=false</code> to close it.
        </Alert>
      ) : null}

      <DataTable
        label="Companies"
        columns={columns}
        data={data.items}
        getRowId={(c) => c.id}
        loading={loading}
        error={error}
        onRetry={() => void load(query)}
        toolbar={() => (
          <>
            <SearchInput value={searchInput} onValueChange={setSearchInput} placeholder="Search company or workspace id…" aria-label="Search companies" className="w-full sm:w-72" />
            <FacetedFilter
              title="Status"
              options={[
                { value: "Active", label: "Active", count: counts.active },
                { value: "Suspended", label: "Suspended", count: counts.suspended },
              ]}
              value={query.status ? [query.status] : []}
              onChange={(v) => setQuery((q) => ({ ...q, page: 1, status: (v[0] ?? "") as Query["status"] }))}
            />
            {filtered ? (
              <ResetFiltersButton
                onClick={() => {
                  setSearchInput("");
                  setQuery((q) => ({ ...q, page: 1, search: "", status: "" }));
                }}
              />
            ) : null}
            <div className="ml-auto flex items-center gap-2">
              <Button variant="outline" size="sm" onClick={() => void refresh()} loading={loading}>
                {loading ? null : <RefreshCw />} Refresh
              </Button>
              <Button size="sm" onClick={() => setCreating(true)}>
                <Plus /> New company
              </Button>
            </div>
          </>
        )}
        empty={
          filtered ? (
            <EmptyState icon={SearchX} title="No companies match" description="Try a different search or remove the filter." />
          ) : (
            <EmptyState
              icon={Landmark}
              title="No companies yet"
              description="Create a workspace for each company that subscribes. Its owner receives the sign-in details you set here."
              action={
                <Button size="sm" onClick={() => setCreating(true)}>
                  <Plus /> New company
                </Button>
              }
            />
          )
        }
        pagination={{
          page: data.page,
          pageSize: data.pageSize,
          totalCount: data.totalCount,
          itemLabel: "companies",
          pageSizeOptions: PAGE_SIZES,
          onPageChange: (page) => setQuery((q) => ({ ...q, page })),
          onPageSizeChange: (pageSize) => setQuery((q) => ({ ...q, page: 1, pageSize })),
        }}
      />

      <CreateCompanyDialog
        open={creating}
        onClose={() => setCreating(false)}
        onCreated={async (company, email, password) => {
          setCreating(false);
          setCreated({ company, email, password });
          notify({ tone: "success", title: "Company created", description: company.name });
          await refresh();
        }}
      />
      <CredentialsDialog created={created} onClose={() => setCreated(null)} />

      <ConfirmDialog
        open={suspending !== null}
        onOpenChange={(open) => (open ? undefined : setSuspending(null))}
        title={`Suspend ${suspending?.name ?? "company"}?`}
        description={
          <>
            Every user of this company is signed out of the service and its {suspending?.deviceCount ?? 0} device(s) can no longer connect. Nothing is deleted, and you can
            reactivate the company at any time.
          </>
        }
        confirmLabel="Suspend company"
        onConfirm={confirmSuspend}
      />
    </>
  );
}

function CreateCompanyDialog({
  open,
  onClose,
  onCreated,
}: {
  open: boolean;
  onClose: () => void;
  onCreated: (company: PlatformTenantDto, ownerEmail: string, password: string) => void | Promise<void>;
}) {
  const [pending, setPending] = React.useState(false);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [formError, setFormError] = React.useState<string | null>(null);
  const [password, setPassword] = React.useState("");
  const [name, setName] = React.useState("");
  const [slug, setSlug] = React.useState("");
  const [slugEdited, setSlugEdited] = React.useState(false);

  React.useEffect(() => {
    if (open) {
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setPassword(generatePassword());
      setName("");
      setSlug("");
      setSlugEdited(false);
      setErrors({});
      setFormError(null);
    }
  }, [open]);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setFormError(null);
    const form = new FormData(event.currentTarget);
    const body: CreatePlatformTenantRequest = {
      companyName: name.trim(),
      slug: slug.trim() || null,
      ownerEmail: String(form.get("ownerEmail") ?? "").trim(),
      ownerDisplayName: String(form.get("ownerDisplayName") ?? "").trim(),
      ownerPassword: password,
    };
    const local: Record<string, string> = {};
    if (body.companyName.length < 2) local.companyName = "Company name is required";
    if (!/^\S+@\S+\.\S+$/.test(body.ownerEmail)) local.ownerEmail = "Enter a valid e-mail address";
    if (!body.ownerDisplayName) local.ownerDisplayName = "The owner's name is required";
    if (!meetsPasswordPolicy(body.ownerPassword)) local.ownerPassword = "At least 10 characters with a letter and a digit";
    setErrors(local);
    if (Object.keys(local).length > 0) return;

    setPending(true);
    try {
      const company = await api.post<PlatformTenantDto>("platform/tenants", body);
      await onCreated(company, body.ownerEmail, body.ownerPassword);
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      setFormError(messageFrom(e, "The company could not be created."));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open={open}
      onClose={() => (pending ? undefined : onClose())}
      title="New company"
      description="Creates the workspace and its first owner. The owner can then add their own colleagues."
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={pending}>
            Cancel
          </Button>
          <Button type="submit" form="new-company-form" loading={pending}>
            Create company
          </Button>
        </>
      }
    >
      <form id="new-company-form" onSubmit={onSubmit} className="space-y-4 pb-2" noValidate>
        {formError ? <Alert tone="error">{formError}</Alert> : null}
        <Field label="Company name" htmlFor="companyName" error={errors.companyName} required>
          <Input
            id="companyName"
            value={name}
            onChange={(e) => {
              setName(e.target.value);
              if (!slugEdited) setSlug(slugFromName(e.target.value));
            }}
            autoComplete="off"
            aria-invalid={!!errors.companyName}
          />
        </Field>
        <Field label="Workspace id" htmlFor="slug" error={errors.slug} hint="Used in the sign-in. Lowercase letters, digits and hyphens; cannot be changed later.">
          <Input
            id="slug"
            value={slug}
            onChange={(e) => {
              setSlug(e.target.value.toLowerCase());
              setSlugEdited(true);
            }}
            autoComplete="off"
            aria-invalid={!!errors.slug}
            className="font-mono"
          />
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Owner's name" htmlFor="ownerDisplayName" error={errors.ownerDisplayName} required>
            <Input id="ownerDisplayName" name="ownerDisplayName" autoComplete="off" aria-invalid={!!errors.ownerDisplayName} />
          </Field>
          <Field label="Owner's e-mail" htmlFor="ownerEmail" error={errors.ownerEmail} required>
            <Input id="ownerEmail" name="ownerEmail" type="email" autoComplete="off" aria-invalid={!!errors.ownerEmail} />
          </Field>
        </div>
        <Field label="Initial password" htmlFor="ownerPassword" error={errors.ownerPassword} hint="Shown once after creation so you can hand it over. Generated for you.">
          <div className="flex gap-2">
            <Input id="ownerPassword" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="off" spellCheck={false} aria-invalid={!!errors.ownerPassword} className="font-mono" />
            <Button type="button" variant="outline" onClick={() => setPassword(generatePassword())} aria-label="Generate a new password">
              <RefreshCw />
            </Button>
          </div>
        </Field>
      </form>
    </Dialog>
  );
}

function CredentialsDialog({ created, onClose }: { created: { company: PlatformTenantDto; email: string; password: string } | null; onClose: () => void }) {
  const [copied, setCopied] = React.useState(false);
  const details = created
    ? `Solvix EndPoint\nCompany: ${created.company.name}\nWorkspace id: ${created.company.slug}\nE-mail: ${created.email}\nPassword: ${created.password}`
    : "";

  async function copy() {
    try {
      await navigator.clipboard.writeText(details);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2500);
    } catch {
      // clipboard unavailable: the details are selectable on screen
    }
  }

  return (
    <Dialog
      open={created !== null}
      onClose={() => {
        setCopied(false);
        onClose();
      }}
      title={created ? `${created.company.name} is ready` : "Company created"}
      description="Give these sign-in details to the company's owner. The password is not stored anywhere you can read it again."
      footer={
        <>
          <Button variant="outline" onClick={copy}>
            {copied ? <Check /> : <Copy />} {copied ? "Copied" : "Copy details"}
          </Button>
          <Button onClick={onClose}>Done</Button>
        </>
      }
    >
      {created ? (
        <dl className="grid grid-cols-[7rem_1fr] gap-x-3 gap-y-2 rounded-md border border-border bg-muted/40 p-4 text-sm">
          <dt className="text-muted-foreground">Company</dt>
          <dd className="font-medium">{created.company.name}</dd>
          <dt className="text-muted-foreground">Workspace id</dt>
          <dd className="font-mono">{created.company.slug}</dd>
          <dt className="text-muted-foreground">E-mail</dt>
          <dd className="break-all font-mono">{created.email}</dd>
          <dt className="text-muted-foreground">Password</dt>
          <dd className="break-all font-mono">{created.password}</dd>
        </dl>
      ) : null}
    </Dialog>
  );
}
