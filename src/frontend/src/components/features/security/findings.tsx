"use client";

import * as React from "react";
import Link from "next/link";
import { api } from "@/lib/api/client";
import type { Finding, FindingStatus, Severity } from "@/lib/api/security-types";
import type { PagedResult } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { humanize, timeAgo } from "@/lib/security";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select, Textarea } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { EmptyRow, Table, TBody, TD, TH, THead, TR } from "@/components/ui/table";
import { useCan } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";
import { FindingStatusBadge, LoadState, SeverityBadge, useResource } from "./security-ui";
import { Pager } from "./shared";

export function FindingsView({ initialMinSeverity, initialRuleKey }: { initialMinSeverity?: string; initialRuleKey?: string }) {
  const { isOwner, isAdmin } = useCan();
  const { toast } = useToast();
  const [status, setStatus] = React.useState<FindingStatus | "">("");
  const [minSeverity, setMinSeverity] = React.useState<Severity | "">((initialMinSeverity as Severity) ?? "");
  const [search, setSearch] = React.useState("");
  const [applied, setApplied] = React.useState("");
  const [page, setPage] = React.useState(1);
  const resource = useResource<PagedResult<Finding>>("security/findings", { status, minSeverity, ruleKey: initialRuleKey, search: applied, page, pageSize: 25 });
  const [selected, setSelected] = React.useState<Finding | null>(null);
  const [accepting, setAccepting] = React.useState(false);
  const [reason, setReason] = React.useState("");
  const [days, setDays] = React.useState("30");
  const [busy, setBusy] = React.useState(false);

  async function accept() {
    if (!selected) return;
    setBusy(true);
    try {
      const until = new Date(Date.now() + Number(days) * 86_400_000).toISOString();
      await api.post(`security/findings/${selected.id}/accept-risk`, { reason, acceptedUntilUtc: until });
      toast({ tone: "success", title: "Risk accepted", description: `Until ${new Date(until).toLocaleDateString()}. It stays visible but is not scored.` });
      setAccepting(false);
      setSelected(null);
      setReason("");
      await resource.reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not accept risk", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <form
        className="mb-4 flex flex-wrap items-center gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          setPage(1);
          setApplied(search);
        }}
      >
        <Input placeholder="Search findings…" value={search} onChange={(e) => setSearch(e.target.value)} className="max-w-xs" aria-label="Search findings" />
        <Select value={status} onChange={(e) => { setStatus(e.target.value as FindingStatus | ""); setPage(1); }} className="w-40" aria-label="Status">
          <option value="">Open and accepted</option>
          <option value="Open">Open</option>
          <option value="Accepted">Accepted</option>
          <option value="Resolved">Resolved</option>
        </Select>
        <Select value={minSeverity} onChange={(e) => { setMinSeverity(e.target.value as Severity | ""); setPage(1); }} className="w-40" aria-label="Minimum severity">
          <option value="">Any severity</option>
          {(["Critical", "High", "Medium", "Low"] as Severity[]).map((s) => (
            <option key={s} value={s}>
              {s} and above
            </option>
          ))}
        </Select>
        <Button type="submit" variant="outline">
          Search
        </Button>
        {initialRuleKey ? <span className="text-sm text-muted-foreground">Rule {initialRuleKey}</span> : null}
      </form>

      <Card>
        <LoadState resource={resource}>
          {(data) => (
            <>
              <Table>
                <THead>
                  <TR>
                    <TH>Severity</TH>
                    <TH>Finding</TH>
                    <TH>Device</TH>
                    <TH>Category</TH>
                    <TH>Status</TH>
                    <TH>Last seen</TH>
                  </TR>
                </THead>
                <TBody>
                  {data.items.length === 0 ? (
                    <EmptyRow colSpan={6}>No findings match.</EmptyRow>
                  ) : (
                    data.items.map((f) => (
                      <TR key={f.id} className="cursor-pointer" onClick={() => setSelected(f)}>
                        <TD>
                          <SeverityBadge severity={f.severity} />
                        </TD>
                        <TD className="max-w-md font-medium">
                          <button className="text-left hover:underline" onClick={() => setSelected(f)}>
                            {f.title}
                          </button>
                        </TD>
                        <TD>
                          <Link href={`/security/devices/${f.endpointId}`} className="hover:underline" onClick={(e) => e.stopPropagation()}>
                            {f.endpointHostname}
                          </Link>
                        </TD>
                        <TD className="text-muted-foreground">{humanize(f.category)}</TD>
                        <TD>
                          <FindingStatusBadge status={f.status} />
                        </TD>
                        <TD className="text-muted-foreground">{timeAgo(f.lastDetectedAtUtc)}</TD>
                      </TR>
                    ))
                  )}
                </TBody>
              </Table>
              <Pager data={data} onPage={setPage} />
            </>
          )}
        </LoadState>
      </Card>

      <Dialog
        open={selected !== null && !accepting}
        onClose={() => setSelected(null)}
        title={selected?.title ?? ""}
        description={selected ? `${selected.endpointHostname} · rule ${selected.ruleKey}` : undefined}
        className="max-w-2xl"
        footer={
          selected && selected.status !== "Resolved" && isAdmin ? (
            selected.status === "Accepted" ? (
              <Button
                variant="outline"
                onClick={async () => {
                  try {
                    await api.post(`security/findings/${selected.id}/revoke-acceptance`);
                    setSelected(null);
                    await resource.reload();
                  } catch (e) {
                    toast({ tone: "error", title: "Could not revoke", description: messageFrom(e) });
                  }
                }}
              >
                Revoke acceptance
              </Button>
            ) : (
              <Button variant="outline" disabled={selected.severity !== "Low" && selected.severity !== "Medium" && !isOwner} onClick={() => setAccepting(true)}>
                Accept risk…
              </Button>
            )
          ) : undefined
        }
      >
        {selected ? (
          <div className="space-y-3 text-sm">
            <div className="flex items-center gap-2">
              <SeverityBadge severity={selected.severity} />
              <FindingStatusBadge status={selected.status} />
              {selected.recurrenceCount > 0 ? <span className="text-xs text-muted-foreground">returned {selected.recurrenceCount}×</span> : null}
            </div>
            <p><span className="font-medium">Evidence: </span>{selected.evidence}</p>
            <p><span className="font-medium">Why it matters: </span>{selected.explanation.whyItMatters}</p>
            <p><span className="font-medium">Why {selected.severity}: </span>{selected.explanation.severityRationale}</p>
            <p><span className="font-medium">How to fix: </span>{selected.explanation.howToFix}</p>
            <p className="text-xs text-muted-foreground">Source: {selected.explanation.windowsInterface}. First seen {timeAgo(selected.firstDetectedAtUtc)}.</p>
            {selected.status === "Accepted" ? (
              <Alert tone="info" title="Risk accepted">
                Until {selected.acceptedUntilUtc ? new Date(selected.acceptedUntilUtc).toLocaleDateString() : "?"}: {selected.acceptanceReason}
              </Alert>
            ) : null}
            {selected.severity !== "Low" && selected.severity !== "Medium" && !isOwner && selected.status === "Open" ? (
              <p className="text-xs text-muted-foreground">Only a workspace owner can accept the risk of a High or Critical finding.</p>
            ) : null}
          </div>
        ) : null}
      </Dialog>

      <Dialog
        open={accepting}
        onClose={() => setAccepting(false)}
        title="Accept risk"
        description="The finding stays visible and is re-opened automatically when the acceptance ends. This is recorded in the audit trail."
        footer={
          <>
            <Button variant="outline" onClick={() => setAccepting(false)}>
              Cancel
            </Button>
            <Button loading={busy} disabled={reason.trim().length === 0} onClick={accept}>
              Accept risk
            </Button>
          </>
        }
      >
        <div className="space-y-3">
          <Field label="Reason (for example, the compensating control)" htmlFor="accept-reason" required>
            <Textarea id="accept-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={3} />
          </Field>
          <Field label="Accept for" htmlFor="accept-days" hint="At most 90 days.">
            <Select id="accept-days" value={days} onChange={(e) => setDays(e.target.value)}>
              {[7, 30, 60, 90].map((d) => (
                <option key={d} value={d}>
                  {d} days
                </option>
              ))}
            </Select>
          </Field>
        </div>
      </Dialog>
    </>
  );
}
