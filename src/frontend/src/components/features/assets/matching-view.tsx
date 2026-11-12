"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { AlertTriangle, Check, CircleHelp, Minus, RefreshCw, X } from "lucide-react";
import { api } from "@/lib/api/client";
import type { AssetCategory, AssetDeviceLinkDto, AssetDeviceLinkStatus, EndpointDto, EvidenceOutcome, PagedResult, RescanResultDto } from "@/lib/api/types";
import { CATEGORIES, EVIDENCE_LABELS, categoryLabel, evidenceTone } from "@/lib/assets";
import { connectivityTone, formatRelative } from "@/lib/devices";
import { emptyToNull, messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { AssetStatusBadge, ConfidenceBadge } from "./asset-badges";

const STATUS_TABS: { value: AssetDeviceLinkStatus; label: string }[] = [
  { value: "Suggested", label: "To review" },
  { value: "Approved", label: "Approved" },
  { value: "Rejected", label: "Rejected" },
  { value: "Superseded", label: "Superseded" },
  { value: "Unlinked", label: "Unlinked" },
];

const outcomeIcon: Record<EvidenceOutcome, React.ComponentType<{ className?: string }>> = {
  Match: Check,
  Partial: Check,
  Mismatch: X,
  Unknown: CircleHelp,
  Warning: AlertTriangle,
};

export function MatchingView({ initial, unregistered, assetId }: { initial: PagedResult<AssetDeviceLinkDto>; unregistered: PagedResult<EndpointDto>; assetId?: string }) {
  const router = useRouter();
  const { toast } = useToast();
  const [status, setStatus] = React.useState<AssetDeviceLinkStatus>("Suggested");
  const [links, setLinks] = React.useState(initial);
  const [endpoints, setEndpoints] = React.useState(unregistered);
  const [decision, setDecision] = React.useState<{ link: AssetDeviceLinkDto; approve: boolean } | null>(null);
  const [register, setRegister] = React.useState<EndpointDto | null>(null);
  const [scanning, setScanning] = React.useState(false);

  const reload = React.useCallback(
    async (next: AssetDeviceLinkStatus, page = 1) => {
      try {
        const [l, u] = await Promise.all([
          api.get<PagedResult<AssetDeviceLinkDto>>("asset-matches", { status: next, assetId, page, pageSize: 25 }),
          api.get<PagedResult<EndpointDto>>("asset-matches/unregistered-endpoints", { pageSize: 25 }),
        ]);
        setLinks(l);
        setEndpoints(u);
      } catch (e) {
        toast({ tone: "error", title: "Could not load matches", description: messageFrom(e) });
      }
    },
    [assetId, toast],
  );

  async function rescan() {
    setScanning(true);
    try {
      const result = await api.post<RescanResultDto>("asset-matches/rescan");
      toast({ tone: "success", title: "Rescan complete", description: `${result.endpointsEvaluated} endpoints checked, ${result.suggestionsCreated} new suggestions, ${result.autoApproved} linked automatically.` });
      await reload(status);
    } catch (e) {
      toast({ tone: "error", title: "Rescan failed", description: messageFrom(e) });
    } finally {
      setScanning(false);
    }
  }

  return (
    <>
      <div className="mb-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div role="tablist" aria-label="Match status" className="flex gap-1 overflow-x-auto border-b border-border">
          {STATUS_TABS.map((t) => (
            <button
              key={t.value}
              role="tab"
              aria-selected={status === t.value}
              onClick={() => {
                setStatus(t.value);
                void reload(t.value);
              }}
              className={`-mb-px whitespace-nowrap border-b-2 px-3 py-2 text-sm font-medium ${status === t.value ? "border-primary text-foreground" : "border-transparent text-muted-foreground hover:text-foreground"}`}
            >
              {t.label}
              {t.value === "Suggested" && status === "Suggested" ? <Badge className="ml-1">{links.totalCount}</Badge> : null}
            </button>
          ))}
        </div>
        <Button variant="outline" onClick={rescan} loading={scanning}>
          <RefreshCw className="h-4 w-4" /> Rescan all endpoints
        </Button>
      </div>

      {assetId ? (
        <Alert className="mb-4" title="Showing matches for one asset">
          <Link href="/assets/matching" className="underline">
            Show all
          </Link>
        </Alert>
      ) : null}

      <div className="space-y-3">
        {links.items.length === 0 ? (
          <Card>
            <CardContent className="p-8 text-center text-sm text-muted-foreground">
              {status === "Suggested"
                ? "Nothing to review. When an agent enrolls, its serial number and manufacturer are compared with the asset register and possible matches appear here."
                : "No associations in this state."}
            </CardContent>
          </Card>
        ) : (
          links.items.map((l) => <MatchCard key={l.id} link={l} onDecide={(approve) => setDecision({ link: l, approve })} />)
        )}
        {links.totalPages > 1 ? (
          <div className="flex justify-end gap-2">
            <Button variant="outline" size="sm" disabled={links.page <= 1} onClick={() => reload(status, links.page - 1)}>
              Previous
            </Button>
            <Button variant="outline" size="sm" disabled={links.page >= links.totalPages} onClick={() => reload(status, links.page + 1)}>
              Next
            </Button>
          </div>
        ) : null}
      </div>

      <Card className="mt-8">
        <CardHeader>
          <CardTitle>Managed endpoints without an asset record</CardTitle>
          <CardDescription>Enrolled devices that are not associated with any asset. Register them to add them to the asset register, pre-filled from their inventory.</CardDescription>
        </CardHeader>
        <Table>
          <THead>
            <TR>
              <TH>Endpoint</TH>
              <TH>Hardware</TH>
              <TH>Serial number</TH>
              <TH>Last seen</TH>
              <TH />
            </TR>
          </THead>
          <TBody>
            {endpoints.items.length === 0 ? (
              <EmptyRow colSpan={5}>Every active endpoint has an asset record.</EmptyRow>
            ) : (
              endpoints.items.map((d) => (
                <TR key={d.id}>
                  <TD className="font-medium">
                    <Link href={`/endpoints/devices/${d.id}`} className="hover:underline">
                      {d.displayName ?? d.hostname}
                    </Link>{" "}
                    <Badge tone={connectivityTone("Active", d.connectivity)}>{d.connectivity}</Badge>
                  </TD>
                  <TD className="text-muted-foreground">{[d.manufacturer, d.model].filter(Boolean).join(" ") || "—"}</TD>
                  <TD className="font-mono text-xs">{d.serialNumber ?? "—"}</TD>
                  <TD className="text-muted-foreground">{formatRelative(d.lastSeenAtUtc)}</TD>
                  <TD className="text-right">
                    <Button size="sm" variant="outline" onClick={() => setRegister(d)}>
                      Register as asset
                    </Button>
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
        {endpoints.totalCount > endpoints.items.length ? (
          <p className="border-t border-border px-3 py-2 text-xs text-muted-foreground">
            Showing {endpoints.items.length} of {endpoints.totalCount}.
          </p>
        ) : null}
      </Card>

      {decision ? (
        <DecisionDialog
          {...decision}
          onClose={() => setDecision(null)}
          onDone={async () => {
            setDecision(null);
            await reload(status);
            router.refresh();
          }}
        />
      ) : null}
      {register ? <RegisterEndpointDialog endpoint={register} onClose={() => setRegister(null)} onDone={(id) => router.push(`/assets/${id}`)} /> : null}
    </>
  );
}

function MatchCard({ link, onDecide }: { link: AssetDeviceLinkDto; onDecide: (approve: boolean) => void }) {
  const a = link.asset;
  const d = link.endpoint;
  return (
    <Card>
      <CardContent className="space-y-3 p-4">
        <div className="flex flex-wrap items-center gap-2">
          <ConfidenceBadge confidence={link.confidence} />
          {link.isAmbiguous ? (
            <Badge tone="warning">
              <AlertTriangle className="mr-1 h-3 w-3" /> Ambiguous — review carefully
            </Badge>
          ) : null}
          <Badge>{link.source === "Manual" ? "Manual" : "Automatic match"}</Badge>
          <span className="text-xs text-muted-foreground">Detected {formatDate(link.detectedAtUtc)}</span>
          {link.decidedAtUtc ? (
            <span className="text-xs text-muted-foreground">
              · decided {formatDate(link.decidedAtUtc)} by {link.decidedBy?.name ?? "the system"}
              {link.decisionNote ? ` — “${link.decisionNote}”` : ""}
            </span>
          ) : null}
          {link.status === "Suggested" ? (
            <div className="ml-auto flex gap-2">
              <Button size="sm" variant="outline" onClick={() => onDecide(false)}>
                Reject
              </Button>
              <Button size="sm" onClick={() => onDecide(true)}>
                Approve
              </Button>
            </div>
          ) : null}
        </div>
        <div className="grid gap-3 md:grid-cols-2">
          <div className="rounded-md border border-border p-3 text-sm">
            <p className="mb-1 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Physical asset</p>
            <Link href={`/assets/${a.id}`} prefetch={false} className="font-medium hover:underline">
              {a.assetTag} · {a.name}
            </Link>{" "}
            <AssetStatusBadge status={a.status} />
            <p className="text-muted-foreground">
              {categoryLabel(a.category)} · {[a.manufacturer, a.model].filter(Boolean).join(" ") || "—"}
            </p>
            <p className="font-mono text-xs">SN {a.serialNumber ?? "—"}</p>
          </div>
          <div className="rounded-md border border-border p-3 text-sm">
            <p className="mb-1 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Managed endpoint</p>
            {d ? (
              <>
                <Link href={`/endpoints/devices/${d.id}`} prefetch={false} className="font-medium hover:underline">
                  {d.displayName ?? d.hostname}
                </Link>{" "}
                <Badge tone={connectivityTone(d.isActive ? "Active" : "Retired", d.connectivity)}>{d.isActive ? d.connectivity : "Retired"}</Badge>
                <p className="text-muted-foreground">
                  {d.osName ?? "—"} · {[d.manufacturer, d.model].filter(Boolean).join(" ") || "—"}
                </p>
                <p className="font-mono text-xs">SN {d.serialNumber ?? "—"}</p>
              </>
            ) : (
              <p className="text-muted-foreground">The endpoint record no longer exists.</p>
            )}
          </div>
        </div>
        <ul className="grid gap-1 text-sm sm:grid-cols-2" aria-label="Evidence">
          {link.evidence.map((e, i) => {
            const Icon = outcomeIcon[e.outcome] ?? Minus;
            return (
              <li key={`${e.kind}-${i}`} className="flex items-start gap-2">
                <Badge tone={evidenceTone(e.outcome)} className="mt-0.5 gap-1">
                  <Icon className="h-3 w-3" aria-hidden /> {e.outcome}
                </Badge>
                <span>
                  <span className="font-medium">{EVIDENCE_LABELS[e.kind] ?? e.kind}</span> <span className="text-muted-foreground">{e.detail}</span>
                </span>
              </li>
            );
          })}
        </ul>
      </CardContent>
    </Card>
  );
}

function DecisionDialog({ link, approve, onClose, onDone }: { link: AssetDeviceLinkDto; approve: boolean; onClose: () => void; onDone: () => Promise<void> }) {
  const { toast } = useToast();
  const [note, setNote] = React.useState("");
  const [busy, setBusy] = React.useState(false);
  async function submit() {
    setBusy(true);
    try {
      await api.post(`asset-matches/${link.id}/${approve ? "approve" : "reject"}`, { note: emptyToNull(note) });
      toast({ tone: "success", title: approve ? "Association approved" : "Suggestion rejected" });
      await onDone();
    } catch (e) {
      toast({ tone: "error", title: "Action failed", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }
  return (
    <Dialog
      open
      onClose={onClose}
      title={approve ? "Approve association?" : "Reject suggestion?"}
      description={
        approve
          ? `${link.asset.assetTag} and ${link.endpoint?.hostname ?? "the endpoint"} will be treated as the same hardware. Records are not merged; competing suggestions are closed.`
          : "The pair is remembered and will not be suggested again."
      }
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button variant={approve ? "primary" : "destructive"} loading={busy} onClick={submit}>
            {approve ? "Approve" : "Reject"}
          </Button>
        </>
      }
    >
      {link.isAmbiguous && approve ? (
        <Alert tone="error" className="mb-3" title="Ambiguous match">
          Several records share this serial number. Confirm the physical unit (e.g. check the label) before approving.
        </Alert>
      ) : null}
      <Field label="Note" htmlFor="decision-note">
        <Input id="decision-note" value={note} onChange={(e) => setNote(e.target.value)} maxLength={1000} />
      </Field>
    </Dialog>
  );
}

function RegisterEndpointDialog({ endpoint, onClose, onDone }: { endpoint: EndpointDto; onClose: () => void; onDone: (assetId: string) => void }) {
  const { toast } = useToast();
  const [category, setCategory] = React.useState<AssetCategory>("Laptop");
  const [name, setName] = React.useState(endpoint.displayName ?? endpoint.hostname);
  const [busy, setBusy] = React.useState(false);
  async function submit() {
    setBusy(true);
    try {
      const created = await api.post<{ summary: { id: string; assetTag: string } }>("asset-matches/register-endpoint", { deviceId: endpoint.id, category, name: emptyToNull(name), assetTag: null });
      toast({ tone: "success", title: `Registered as ${created.summary.assetTag}` });
      onDone(created.summary.id);
    } catch (e) {
      toast({ tone: "error", title: "Registration failed", description: messageFrom(e) });
      setBusy(false);
    }
  }
  return (
    <Dialog
      open
      onClose={onClose}
      title="Register endpoint as asset"
      description="Creates an asset from the endpoint's reported manufacturer, model, serial number, branch and primary user, and links the two records."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button loading={busy} onClick={submit}>
            Register
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        <Field label="Category" htmlFor="reg-category">
          <Select id="reg-category" value={category} onChange={(e) => setCategory(e.target.value as AssetCategory)}>
            {CATEGORIES.filter((c) => c.manageable).map((c) => (
              <option key={c.value} value={c.value}>
                {c.label}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Asset name" htmlFor="reg-name">
          <Input id="reg-name" value={name} onChange={(e) => setName(e.target.value)} maxLength={200} />
        </Field>
        <p className="text-xs text-muted-foreground">
          Reported: {[endpoint.manufacturer, endpoint.model].filter(Boolean).join(" ") || "—"} · SN {endpoint.serialNumber ?? "—"}
        </p>
      </div>
    </Dialog>
  );
}
