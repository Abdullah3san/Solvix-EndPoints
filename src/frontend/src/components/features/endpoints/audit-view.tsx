"use client";

import * as React from "react";
import { api } from "@/lib/api/client";
import type { PagedResult } from "@/lib/api/types";
import type { AuditEntryDto } from "@/lib/api/endpoint-types";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";

const actorTone = { User: "brand", Agent: "neutral", System: "warning" } as const;

export function AuditView({ initial }: { initial: PagedResult<AuditEntryDto> }) {
  const [data, setData] = React.useState(initial);
  const [action, setAction] = React.useState("");

  const load = React.useCallback(
    async (page: number) => setData(await api.get<PagedResult<AuditEntryDto>>("audit", { action: action || undefined, page, pageSize: data.pageSize })),
    [action, data.pageSize],
  );

  return (
    <div className="space-y-4">
      <form
        className="flex items-center gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          void load(1);
        }}
      >
        <Input placeholder="Action prefix, e.g. patch_deployment" value={action} onChange={(e) => setAction(e.target.value)} className="max-w-sm" aria-label="Filter by action" />
        <Button type="submit" variant="outline">
          Filter
        </Button>
      </form>
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>When</TH>
              <TH>Actor</TH>
              <TH>Action</TH>
              <TH>Summary</TH>
              <TH>Correlation</TH>
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={5}>No entries.</EmptyRow>
            ) : (
              data.items.map((e) => (
                <TR key={e.id}>
                  <TD className="whitespace-nowrap text-muted-foreground">{new Date(e.occurredAtUtc).toLocaleString()}</TD>
                  <TD>
                    <Badge tone={actorTone[e.actorType]}>{e.actorType}</Badge>
                    <span className="block text-xs text-muted-foreground">{e.actorName}</span>
                  </TD>
                  <TD className="font-mono text-xs">{e.action}</TD>
                  <TD className="max-w-lg text-sm">{e.summary}</TD>
                  <TD className="font-mono text-xs text-muted-foreground">{e.correlationId?.slice(0, 12) ?? "—"}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
        <div className="flex items-center justify-between border-t border-border px-3 py-2 text-sm text-muted-foreground">
          <span>
            Page {data.page} of {Math.max(1, data.totalPages)} · {data.totalCount} entries (append-only)
          </span>
          <div className="flex gap-1">
            <Button variant="outline" size="sm" disabled={data.page <= 1} onClick={() => void load(data.page - 1)}>
              Previous
            </Button>
            <Button variant="outline" size="sm" disabled={data.page >= data.totalPages} onClick={() => void load(data.page + 1)}>
              Next
            </Button>
          </div>
        </div>
      </Card>
    </div>
  );
}
