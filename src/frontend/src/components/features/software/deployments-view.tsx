"use client";

import * as React from "react";
import Link from "next/link";
import { Plus } from "lucide-react";
import { api } from "@/lib/api/client";
import type { DeploymentDto, PagedResult } from "@/lib/api/types";
import { formatDate } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Select } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useCan } from "@/components/providers/session-provider";
import { DeploymentStatusBadge } from "./labels";

export function DeploymentsView({ initial }: { initial: DeploymentDto[] }) {
  const canDeploy = useCan("software.deploy");
  const canApprove = useCan("software.approve");
  const [items, setItems] = React.useState(initial);
  const [filter, setFilter] = React.useState("");

  React.useEffect(() => {
    let cancelled = false;
    const query = filter === "mine" ? { awaitingMyApproval: true, pageSize: 50 } : { status: filter || undefined, pageSize: 50 };
    api.get<PagedResult<DeploymentDto>>("software/deployments", query).then((r) => !cancelled && setItems(r.items), () => undefined);
    return () => {
      cancelled = true;
    };
  }, [filter]);

  return (
    <>
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <Select aria-label="Filter deployments" value={filter} onChange={(e) => setFilter(e.target.value)} className="w-64">
          <option value="">All deployments</option>
          {canApprove ? <option value="mine">Awaiting my approval</option> : null}
          <option value="PendingApproval">Pending approval</option>
          <option value="Scheduled">Scheduled</option>
          <option value="Running">Running</option>
          <option value="Completed">Completed</option>
          <option value="CompletedWithErrors">Completed with errors</option>
          <option value="Failed">Failed</option>
        </Select>
        {canDeploy ? (
          <Link href="/software/deployments/new">
            <Button tabIndex={-1}><Plus className="h-4 w-4" /> New deployment</Button>
          </Link>
        ) : null}
      </div>
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Deployment</TH>
              <TH>Status</TH>
              <TH>Devices</TH>
              <TH>Approvals</TH>
              <TH>Starts</TH>
            </TR>
          </THead>
          <TBody>
            {items.length === 0 ? (
              <EmptyRow colSpan={5}>No deployments.</EmptyRow>
            ) : (
              items.map((d) => (
                <TR key={d.id}>
                  <TD>
                    <Link href={`/software/deployments/${d.id}`} className="font-medium text-primary hover:underline">{d.name}</Link>
                    <div className="text-xs text-muted-foreground">{d.action} {d.packageName} {d.packageVersion ?? ""}</div>
                  </TD>
                  <TD><DeploymentStatusBadge status={d.status} /></TD>
                  <TD className="text-sm">
                    {d.counts.succeeded}/{d.counts.total} done
                    {d.counts.failed + d.counts.timedOut + d.counts.expired > 0 ? <span className="text-destructive"> · {d.counts.failed + d.counts.timedOut + d.counts.expired} failed</span> : null}
                  </TD>
                  <TD className="text-sm">{d.approvalsReceived}/{d.requiredApprovals}{d.isSensitive ? " (sensitive)" : ""}</TD>
                  <TD className="text-sm">{formatDate(d.scheduledStartUtc)}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
    </>
  );
}
