"use client";

import * as React from "react";
import { FileUp, RefreshCw, Search, Trash2 } from "lucide-react";
import { api, PROXY_PREFIX } from "@/lib/api/client";
import { ApiError, problemFromResponse } from "@/lib/api/errors";
import type {
  AuditEntryDto,
  KnowledgeDocumentDto,
  KnowledgeHit,
  PagedResult,
  ProposalDto,
  ProposalStatus,
  ReportDto,
  ReportKind,
  ReportSummaryDto,
  TicketDto,
  TicketPriority,
  TicketStatus,
} from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select, Textarea } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { EmptyRow, Table, TBody, TD, TH, THead, TR } from "@/components/ui/table";
import { useCan } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";
import { Markdown } from "./markdown";
import { ProposalCard } from "./proposal-card";

function useLoad<T>(path: string, query?: Record<string, string | number | undefined>) {
  const [data, setData] = React.useState<T | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [version, setVersion] = React.useState(0);
  const key = JSON.stringify(query ?? {});
  React.useEffect(() => {
    let live = true;
    api
      .get<T>(path, JSON.parse(key) as Record<string, string | number | undefined>)
      .then((result) => {
        if (!live) return;
        setError(null);
        setData(result);
      })
      .catch((e) => live && setError(messageFrom(e)));
    return () => {
      live = false;
    };
  }, [path, key, version]);
  const reload = React.useCallback(async () => setVersion((v) => v + 1), []);
  return { data, error, reload };
}

function Loading() {
  return (
    <div className="space-y-2">
      <Skeleton className="h-10 w-full" />
      <Skeleton className="h-10 w-full" />
    </div>
  );
}

// ------------------------------------------------------------------ approvals

const STATUS_FILTERS: { value: ProposalStatus | ""; label: string }[] = [
  { value: "PendingApproval", label: "Awaiting approval" },
  { value: "Succeeded", label: "Succeeded" },
  { value: "PartiallyFailed", label: "Partially failed" },
  { value: "Failed", label: "Failed" },
  { value: "Rejected", label: "Rejected" },
  { value: "Expired", label: "Expired" },
  { value: "", label: "All" },
];

export function ApprovalsView() {
  const [status, setStatus] = React.useState<ProposalStatus | "">("PendingApproval");
  const { data, error, reload } = useLoad<PagedResult<ProposalDto>>("assistant/proposals", { status: status || undefined, pageSize: 50 });
  return (
    <div className="space-y-4">
      <div className="max-w-xs">
        <Select aria-label="Status filter" value={status} onChange={(e) => setStatus(e.target.value as ProposalStatus | "")}>
          {STATUS_FILTERS.map((f) => (
            <option key={f.label} value={f.value}>
              {f.label}
            </option>
          ))}
        </Select>
      </div>
      {error ? <Alert tone="error">{error}</Alert> : null}
      {!data ? (
        <Loading />
      ) : data.items.length === 0 ? (
        <p className="py-10 text-center text-sm text-muted-foreground">No proposals.</p>
      ) : (
        <div className="space-y-3">
          {data.items.map((p) => (
            <ProposalCard key={`${p.id}:${p.status}`} proposal={p} onChanged={() => void reload()} />
          ))}
        </div>
      )}
    </div>
  );
}

// ------------------------------------------------------------------ knowledge base

const formatSize = (n: number) => (n > 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);

export function KnowledgeView() {
  const { isAdmin } = useCan();
  const { toast } = useToast();
  const { data, error, reload } = useLoad<PagedResult<KnowledgeDocumentDto>>("assistant/knowledge/documents", { pageSize: 100 });
  const [uploading, setUploading] = React.useState(false);
  const [uploadError, setUploadError] = React.useState<string | null>(null);
  const [query, setQuery] = React.useState("");
  const [hits, setHits] = React.useState<KnowledgeHit[] | null>(null);
  const fileRef = React.useRef<HTMLInputElement>(null);

  async function upload(file: File) {
    setUploading(true);
    setUploadError(null);
    try {
      const form = new FormData();
      form.append("file", file);
      const response = await fetch(`${PROXY_PREFIX}/assistant/knowledge/documents`, { method: "POST", body: form, credentials: "same-origin" });
      if (!response.ok) throw new ApiError(response.status, await problemFromResponse(response));
      const doc = (await response.json()) as KnowledgeDocumentDto;
      toast({ tone: doc.status === "Ready" ? "success" : "error", title: doc.status === "Ready" ? "Document indexed" : "Indexing failed", description: doc.failureReason ?? `${doc.chunkCount} sections` });
      await reload();
    } catch (e) {
      setUploadError(messageFrom(e));
    } finally {
      setUploading(false);
      if (fileRef.current) fileRef.current.value = "";
    }
  }

  async function remove(doc: KnowledgeDocumentDto) {
    try {
      await api.delete(`assistant/knowledge/documents/${doc.id}`);
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not delete", description: messageFrom(e) });
    }
  }

  async function reindex(doc: KnowledgeDocumentDto) {
    try {
      await api.post(`assistant/knowledge/documents/${doc.id}/reindex`);
      toast({ tone: "success", title: "Re-indexed" });
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not re-index", description: messageFrom(e) });
    }
  }

  async function search(e: React.FormEvent) {
    e.preventDefault();
    if (!query.trim()) return;
    try {
      setHits(await api.post<KnowledgeHit[]>("assistant/knowledge/search", { query, limit: 5 }));
    } catch (err) {
      toast({ tone: "error", title: "Search failed", description: messageFrom(err) });
    }
  }

  return (
    <div className="space-y-6">
      {isAdmin ? (
        <div className="space-y-2">
          <input ref={fileRef} type="file" hidden aria-label="Upload document" accept=".txt,.md,.markdown,.log,.csv,.json,.html,.htm,.docx" onChange={(e) => e.target.files?.[0] && void upload(e.target.files[0])} />
          <Button loading={uploading} onClick={() => fileRef.current?.click()}>
            <FileUp className="h-4 w-4" aria-hidden /> Upload document
          </Button>
          <p className="text-xs text-muted-foreground">
            Text, Markdown, CSV, JSON, HTML and DOCX up to 10 MB. Documents are private to this workspace; passwords and keys found in them are removed before indexing.
          </p>
          {uploadError ? <Alert tone="error">{uploadError}</Alert> : null}
        </div>
      ) : null}
      {error ? <Alert tone="error">{error}</Alert> : null}
      {!data ? (
        <Loading />
      ) : (
        <Table>
          <THead>
            <TR>
              <TH>Title</TH>
              <TH>File</TH>
              <TH>Status</TH>
              <TH>Sections</TH>
              <TH>Size</TH>
              {isAdmin ? <TH className="text-right">Actions</TH> : null}
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={isAdmin ? 6 : 5}>No documents yet.</EmptyRow>
            ) : (
              data.items.map((d) => (
                <TR key={d.id}>
                  <TD className="font-medium">{d.title}</TD>
                  <TD>{d.fileName}</TD>
                  <TD>
                    <Badge tone={d.status === "Ready" ? "success" : d.status === "Failed" ? "danger" : "warning"} title={d.failureReason ?? undefined}>
                      {d.status}
                    </Badge>
                    {d.redactionCount > 0 ? <Badge tone="warning" className="ml-1" title="Secret-like values were removed">{d.redactionCount} redacted</Badge> : null}
                  </TD>
                  <TD>{d.chunkCount}</TD>
                  <TD>{formatSize(d.sizeBytes)}</TD>
                  {isAdmin ? (
                    <TD className="text-right">
                      <Button size="icon" variant="ghost" aria-label={`Re-index ${d.title}`} onClick={() => void reindex(d)}>
                        <RefreshCw className="h-4 w-4" />
                      </Button>
                      <Button size="icon" variant="ghost" aria-label={`Delete ${d.title}`} onClick={() => void remove(d)}>
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </TD>
                  ) : null}
                </TR>
              ))
            )}
          </TBody>
        </Table>
      )}

      <form onSubmit={search} className="flex max-w-xl gap-2">
        <Input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Test a search, e.g. how to restart the print spooler" aria-label="Search the knowledge base" />
        <Button type="submit" variant="outline">
          <Search className="h-4 w-4" aria-hidden /> Search
        </Button>
      </form>
      {hits ? (
        hits.length === 0 ? (
          <p className="text-sm text-muted-foreground">No relevant passages found.</p>
        ) : (
          <ul className="space-y-3">
            {hits.map((h) => (
              <li key={h.chunkId} className="rounded-md border border-border p-3 text-sm">
                <p className="mb-1 text-xs text-muted-foreground">
                  {h.documentTitle}
                  {h.section ? ` › ${h.section}` : ""} · score {h.score.toFixed(2)}
                </p>
                <p className="whitespace-pre-wrap">{h.content}</p>
              </li>
            ))}
          </ul>
        )
      ) : null}
    </div>
  );
}

// ------------------------------------------------------------------ tickets

const priorities: TicketPriority[] = ["Low", "Normal", "High", "Urgent"];
const ticketStatuses: TicketStatus[] = ["Open", "InProgress", "Resolved", "Closed"];
const priorityTone = { Low: "neutral", Normal: "brand", High: "warning", Urgent: "danger" } as const;

export function TicketsView() {
  const { isAdmin } = useCan();
  const { toast } = useToast();
  const { data, error, reload } = useLoad<PagedResult<TicketDto>>("assistant/tickets", { pageSize: 50 });
  const [creating, setCreating] = React.useState(false);
  const [form, setForm] = React.useState({ title: "", description: "", priority: "Normal" as TicketPriority });
  const [formError, setFormError] = React.useState<string | null>(null);

  async function create() {
    setFormError(null);
    try {
      await api.post("assistant/tickets", { ...form, category: null, devices: [] });
      setCreating(false);
      setForm({ title: "", description: "", priority: "Normal" });
      await reload();
    } catch (e) {
      setFormError(messageFrom(e));
    }
  }

  async function setStatus(t: TicketDto, status: TicketStatus) {
    try {
      await api.patch(`assistant/tickets/${t.id}/status`, { status });
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not update", description: messageFrom(e) });
    }
  }

  return (
    <div className="space-y-4">
      <Button onClick={() => setCreating(true)}>New ticket</Button>
      {error ? <Alert tone="error">{error}</Alert> : null}
      {!data ? (
        <Loading />
      ) : (
        <Table>
          <THead>
            <TR>
              <TH>Ref</TH>
              <TH>Title</TH>
              <TH>Priority</TH>
              <TH>Devices</TH>
              <TH>Status</TH>
              <TH>Created</TH>
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={6}>No tickets.</EmptyRow>
            ) : (
              data.items.map((t) => (
                <TR key={t.id}>
                  <TD className="font-mono text-xs">{t.reference}</TD>
                  <TD>
                    <span className="font-medium">{t.title}</span>
                    {t.createdViaAssistant ? <Badge className="ml-2" tone="brand">AI</Badge> : null}
                    <p className="max-w-xl truncate text-xs text-muted-foreground">{t.description}</p>
                  </TD>
                  <TD>
                    <Badge tone={priorityTone[t.priority]}>{t.priority}</Badge>
                  </TD>
                  <TD>{t.devices.map((d) => d.name).join(", ") || "–"}</TD>
                  <TD>
                    {isAdmin ? (
                      <Select aria-label={`Status of ${t.reference}`} value={t.status} onChange={(e) => void setStatus(t, e.target.value as TicketStatus)} className="h-8 w-32">
                        {ticketStatuses.map((s) => (
                          <option key={s}>{s}</option>
                        ))}
                      </Select>
                    ) : (
                      t.status
                    )}
                  </TD>
                  <TD>{new Date(t.createdAtUtc).toLocaleDateString()}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      )}
      <Dialog
        open={creating}
        onClose={() => setCreating(false)}
        title="New ticket"
        footer={
          <>
            <Button variant="outline" onClick={() => setCreating(false)}>
              Cancel
            </Button>
            <Button onClick={() => void create()} disabled={!form.title.trim() || !form.description.trim()}>
              Create
            </Button>
          </>
        }
      >
        <div className="space-y-3">
          <Field label="Title" htmlFor="t-title">
            <Input id="t-title" value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} maxLength={200} />
          </Field>
          <Field label="Description" htmlFor="t-desc">
            <Textarea id="t-desc" value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </Field>
          <Field label="Priority" htmlFor="t-prio">
            <Select id="t-prio" value={form.priority} onChange={(e) => setForm({ ...form, priority: e.target.value as TicketPriority })}>
              {priorities.map((p) => (
                <option key={p}>{p}</option>
              ))}
            </Select>
          </Field>
          {formError ? <Alert tone="error">{formError}</Alert> : null}
        </div>
      </Dialog>
    </div>
  );
}

// ------------------------------------------------------------------ reports

const reportKinds: { value: ReportKind; label: string }[] = [
  { value: "FleetHealth", label: "Fleet health" },
  { value: "SoftwareInventory", label: "Software inventory" },
  { value: "Security", label: "Security posture" },
  { value: "Performance", label: "Performance" },
];

export function ReportsView() {
  const { toast } = useToast();
  const { data, error, reload } = useLoad<PagedResult<ReportSummaryDto>>("assistant/reports", { pageSize: 50 });
  const [kind, setKind] = React.useState<ReportKind>("FleetHealth");
  const [generating, setGenerating] = React.useState(false);
  const [open, setOpen] = React.useState<ReportDto | null>(null);

  async function generate() {
    setGenerating(true);
    try {
      const report = await api.post<ReportDto>("assistant/reports", { kind, branchId: null, departmentIds: null, title: null });
      setOpen(report);
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not generate the report", description: messageFrom(e) });
    } finally {
      setGenerating(false);
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex max-w-md items-center gap-2">
        <Select aria-label="Report type" value={kind} onChange={(e) => setKind(e.target.value as ReportKind)}>
          {reportKinds.map((k) => (
            <option key={k.value} value={k.value}>
              {k.label}
            </option>
          ))}
        </Select>
        <Button loading={generating} onClick={() => void generate()}>
          Generate
        </Button>
      </div>
      {error ? <Alert tone="error">{error}</Alert> : null}
      {!data ? (
        <Loading />
      ) : (
        <Table>
          <THead>
            <TR>
              <TH>Title</TH>
              <TH>Type</TH>
              <TH>AI summary</TH>
              <TH>By</TH>
              <TH>Created</TH>
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={5}>No reports yet.</EmptyRow>
            ) : (
              data.items.map((r) => (
                <TR key={r.id}>
                  <TD>
                    <button type="button" className="font-medium text-primary hover:underline" onClick={async () => setOpen(await api.get<ReportDto>(`assistant/reports/${r.id}`))}>
                      {r.title}
                    </button>
                  </TD>
                  <TD>{r.kind}</TD>
                  <TD>{r.narrativeIncluded ? <Badge tone="brand">Included</Badge> : <Badge>Data only</Badge>}</TD>
                  <TD>{r.generatedByEmail}</TD>
                  <TD>{new Date(r.createdAtUtc).toLocaleString()}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      )}
      <Dialog open={!!open} onClose={() => setOpen(null)} title={open?.title ?? ""} className="max-w-3xl" footer={<Button variant="outline" onClick={() => setOpen(null)}>Close</Button>}>
        <div className="max-h-[60vh] overflow-y-auto">{open ? <Markdown text={open.contentMarkdown} /> : null}</div>
      </Dialog>
    </div>
  );
}

// ------------------------------------------------------------------ audit

export function AuditView() {
  const [kind, setKind] = React.useState("");
  const { data, error } = useLoad<PagedResult<AuditEntryDto>>("assistant/audit", { kind: kind || undefined, pageSize: 100 });
  const kinds = ["", "ai", "ai.tool_call", "proposal", "ticket", "report", "knowledge"];
  return (
    <div className="space-y-4">
      <div className="max-w-xs">
        <Select aria-label="Event kind" value={kind} onChange={(e) => setKind(e.target.value)}>
          {kinds.map((k) => (
            <option key={k} value={k}>
              {k || "All events"}
            </option>
          ))}
        </Select>
      </div>
      {error ? <Alert tone="error">{error}</Alert> : null}
      {!data ? (
        <Loading />
      ) : (
        <Table>
          <THead>
            <TR>
              <TH>Time</TH>
              <TH>Event</TH>
              <TH>Name</TH>
              <TH>Result</TH>
              <TH>User</TH>
              <TH>Details</TH>
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={6}>No events.</EmptyRow>
            ) : (
              data.items.map((e) => (
                <TR key={e.id}>
                  <TD className="whitespace-nowrap text-xs">{new Date(e.createdAtUtc).toLocaleString()}</TD>
                  <TD className="font-mono text-xs">{e.kind}</TD>
                  <TD>{e.name}</TD>
                  <TD>
                    <Badge tone={e.status === "Success" ? "success" : e.status === "Denied" ? "danger" : "warning"}>{e.status}</Badge>
                  </TD>
                  <TD className="text-xs">{e.userEmail}</TD>
                  <TD className="max-w-md truncate text-xs text-muted-foreground" title={e.summary ?? e.input ?? undefined}>
                    {e.summary ?? e.input}
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      )}
    </div>
  );
}
