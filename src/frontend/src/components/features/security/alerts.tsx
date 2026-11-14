"use client";

import * as React from "react";
import Link from "next/link";
import { api } from "@/lib/api/client";
import type { Alert as AlertDto, SecurityEvent } from "@/lib/api/security-types";
import type { PagedResult } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { timeAgo } from "@/lib/security";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { EmptyRow, Table, TBody, TD, TH, THead, TR } from "@/components/ui/table";
import { useCan } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";
import { AlertStatusBadge, LoadState, SeverityBadge, useResource } from "./security-ui";
import { NoteDialog, Pager } from "./shared";

export function AlertsView() {
  const { isAdmin } = useCan();
  const { toast } = useToast();
  const [openOnly, setOpenOnly] = React.useState(true);
  const [page, setPage] = React.useState(1);
  const resource = useResource<PagedResult<AlertDto>>("security/alerts", { openOnly, page, pageSize: 25 });
  const events = useResource<PagedResult<SecurityEvent>>("security/events", { pageSize: 10 });
  const [closing, setClosing] = React.useState<{ alert: AlertDto; kind: "resolve" | "dismiss" } | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [expanded, setExpanded] = React.useState<string | null>(null);

  async function act(label: string, fn: () => Promise<unknown>) {
    setBusy(true);
    try {
      await fn();
      setClosing(null);
      await resource.reload();
    } catch (e) {
      toast({ tone: "error", title: label, description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-2">
        <Button size="sm" variant={openOnly ? "primary" : "outline"} onClick={() => { setOpenOnly(true); setPage(1); }}>
          Open
        </Button>
        <Button size="sm" variant={!openOnly ? "primary" : "outline"} onClick={() => { setOpenOnly(false); setPage(1); }}>
          All
        </Button>
      </div>
      <Card>
        <LoadState resource={resource}>
          {(data) => (
            <>
              <Table>
                <THead>
                  <TR>
                    <TH>Severity</TH>
                    <TH>Alert</TH>
                    <TH>Device</TH>
                    <TH>Status</TH>
                    <TH>Raised</TH>
                    {isAdmin ? <TH className="text-right">Actions</TH> : null}
                  </TR>
                </THead>
                <TBody>
                  {data.items.length === 0 ? (
                    <EmptyRow colSpan={6}>No alerts.</EmptyRow>
                  ) : (
                    data.items.flatMap((a) => [
                      <TR key={a.id}>
                        <TD>
                          <SeverityBadge severity={a.severity} />
                        </TD>
                        <TD className="max-w-md">
                          <button className="text-left font-medium hover:underline" onClick={() => setExpanded(expanded === a.id ? null : a.id)} aria-expanded={expanded === a.id}>
                            {a.title}
                          </button>
                          <p className="text-xs text-muted-foreground">{a.evidence}</p>
                        </TD>
                        <TD>
                          <Link href={`/security/devices/${a.endpointId}`} className="hover:underline">
                            {a.endpointHostname}
                          </Link>
                        </TD>
                        <TD>
                          <AlertStatusBadge status={a.status} />
                        </TD>
                        <TD className="text-muted-foreground">{timeAgo(a.raisedAtUtc)}</TD>
                        {isAdmin ? (
                          <TD>
                            <div className="flex justify-end gap-1">
                              {a.status === "New" ? (
                                <Button size="sm" variant="outline" onClick={() => act("Could not acknowledge", () => api.post(`security/alerts/${a.id}/acknowledge`))}>
                                  Acknowledge
                                </Button>
                              ) : null}
                              {a.status === "New" || a.status === "Acknowledged" ? (
                                <>
                                  <Button size="sm" variant="outline" onClick={() => setClosing({ alert: a, kind: "resolve" })}>
                                    Resolve
                                  </Button>
                                  <Button size="sm" variant="ghost" onClick={() => setClosing({ alert: a, kind: "dismiss" })}>
                                    Dismiss
                                  </Button>
                                </>
                              ) : null}
                            </div>
                          </TD>
                        ) : null}
                      </TR>,
                      expanded === a.id ? (
                        <TR key={`${a.id}-x`}>
                          <TD colSpan={6} className="bg-muted/30 text-sm">
                            <p><span className="font-medium">Why it matters: </span>{a.explanation.whyItMatters}</p>
                            <p><span className="font-medium">Why {a.severity}: </span>{a.explanation.severityRationale}</p>
                            <p><span className="font-medium">What to do: </span>{a.explanation.howToFix}</p>
                            {a.closureNote ? <p className="text-muted-foreground">Closed: {a.closureNote}</p> : null}
                          </TD>
                        </TR>
                      ) : null,
                    ])
                  )}
                </TBody>
              </Table>
              <Pager data={data} onPage={setPage} />
            </>
          )}
        </LoadState>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Recent security events</CardTitle>
          <CardDescription>Only eight allow-listed Windows event ids are collected, as short summaries.</CardDescription>
        </CardHeader>
        <CardContent>
          <LoadState resource={events}>
            {(data) =>
              data.items.length === 0 ? (
                <p className="text-sm text-muted-foreground">No events received.</p>
              ) : (
                <ul className="divide-y divide-border text-sm">
                  {data.items.map((e) => (
                    <li key={e.id} className="flex flex-wrap items-center gap-2 py-2">
                      <span className="font-mono text-xs">{e.eventId}</span>
                      <span>{e.summary ?? e.log}</span>
                      <span className="text-muted-foreground">on {e.endpointHostname}</span>
                      <span className="ml-auto text-xs text-muted-foreground">{timeAgo(e.timeCreatedUtc)}</span>
                    </li>
                  ))}
                </ul>
              )
            }
          </LoadState>
        </CardContent>
      </Card>

      <NoteDialog
        open={closing !== null}
        title={closing?.kind === "dismiss" ? "Dismiss alert" : "Resolve alert"}
        description={closing?.alert.title}
        confirmLabel={closing?.kind === "dismiss" ? "Dismiss" : "Resolve"}
        noteRequired={closing?.kind === "dismiss"}
        busy={busy}
        onClose={() => setClosing(null)}
        onConfirm={(note) => closing && act("Could not close alert", () => api.post(`security/alerts/${closing.alert.id}/${closing.kind}`, { note }))}
      />
    </div>
  );
}
