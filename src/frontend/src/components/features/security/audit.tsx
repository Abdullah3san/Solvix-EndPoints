"use client";

import * as React from "react";
import type { AuditEntry } from "@/lib/api/security-types";
import type { PagedResult } from "@/lib/api/types";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { EmptyRow, Table, TBody, TD, TH, THead, TR } from "@/components/ui/table";
import { LoadState, useResource } from "./security-ui";
import { Pager } from "./shared";

export function AuditView() {
  const [action, setAction] = React.useState("");
  const [applied, setApplied] = React.useState("");
  const [page, setPage] = React.useState(1);
  const [open, setOpen] = React.useState<string | null>(null);
  const resource = useResource<PagedResult<AuditEntry>>("security/audit", { action: applied, page, pageSize: 50 });

  return (
    <>
      <form
        className="mb-4 flex items-center gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          setPage(1);
          setApplied(action);
        }}
      >
        <Input placeholder="Filter by action, e.g. policy. or remediation.approved" value={action} onChange={(e) => setAction(e.target.value)} className="max-w-sm" aria-label="Filter by action" />
        <Button type="submit" variant="outline">
          Filter
        </Button>
        <span className="text-xs text-muted-foreground">Append-only: entries cannot be edited or deleted.</span>
      </form>
      <Card>
        <LoadState resource={resource}>
          {(data) => (
            <>
              <Table>
                <THead>
                  <TR>
                    <TH>When (UTC)</TH>
                    <TH>Who</TH>
                    <TH>Action</TH>
                    <TH>What happened</TH>
                  </TR>
                </THead>
                <TBody>
                  {data.items.length === 0 ? (
                    <EmptyRow colSpan={4}>No entries.</EmptyRow>
                  ) : (
                    data.items.flatMap((a) => [
                      <TR key={a.id}>
                        <TD className="whitespace-nowrap font-mono text-xs">{new Date(a.occurredAtUtc).toISOString().replace("T", " ").slice(0, 19)}</TD>
                        <TD className="text-sm">
                          <Badge tone={a.actorType === "User" ? "brand" : "neutral"} className="mr-2">
                            {a.actorType}
                          </Badge>
                          {a.actorLabel}
                        </TD>
                        <TD className="font-mono text-xs">{a.action}</TD>
                        <TD className="text-sm">
                          {a.summary}
                          {a.details ? (
                            <button className="ml-2 text-xs underline" onClick={() => setOpen(open === a.id ? null : a.id)} aria-expanded={open === a.id}>
                              details
                            </button>
                          ) : null}
                        </TD>
                      </TR>,
                      open === a.id ? (
                        <TR key={`${a.id}-d`}>
                          <TD colSpan={4} className="bg-muted/30">
                            <pre className="overflow-x-auto whitespace-pre-wrap break-all text-xs">{a.details}</pre>
                            <p className="mt-1 text-xs text-muted-foreground">
                              {a.sourceIp ? `from ${a.sourceIp} · ` : ""}correlation {a.correlationId ?? "—"}
                            </p>
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
    </>
  );
}
