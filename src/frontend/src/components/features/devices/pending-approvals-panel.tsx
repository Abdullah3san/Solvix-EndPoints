"use client";

import * as React from "react";
import { Check, X } from "lucide-react";
import { api } from "@/lib/api/client";
import type { BulkDecisionResultDto, PendingEnrollmentDto } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";

interface Props {
  items: PendingEnrollmentDto[];
  onChanged: () => Promise<void>;
}

/** The approval queue: computers that ran an installer and wait for an administrator. */
export function PendingApprovalsPanel({ items, onChanged }: Props) {
  const { toast } = useToast();
  const [selected, setSelected] = React.useState<Set<string>>(new Set());
  const [busy, setBusy] = React.useState(false);
  const waiting = items.filter((i) => i.status === "Pending" && !i.isExpired);

  function toggle(id: string) {
    setSelected((current) => {
      const next = new Set(current);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  async function decide(ids: string[], verb: "approve" | "reject") {
    setBusy(true);
    try {
      if (ids.length === 1) {
        await api.post(`pending-enrollments/${ids[0]}/${verb}`, {});
        toast({ tone: "success", title: verb === "approve" ? "Computer approved" : "Computer rejected" });
      } else {
        const result = await api.post<BulkDecisionResultDto>(`pending-enrollments/bulk/${verb}`, { ids });
        toast({
          tone: result.failed.length ? "error" : "success",
          title: `${result.succeeded} ${verb === "approve" ? "approved" : "rejected"}${result.failed.length ? `, ${result.failed.length} failed` : ""}`,
          description: result.failed[0]?.message,
        });
      }
      setSelected(new Set());
      await onChanged();
    } catch (e) {
      toast({ tone: "error", title: `Could not ${verb}`, description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  const ids = [...selected].filter((id) => waiting.some((w) => w.id === id));

  return (
    <>
      <Alert className="mb-4">
        Computers appear here after someone runs a downloaded installer. Check the name, serial number and where it will be placed, then approve. Approved computers receive their
        certificate within about a minute.
      </Alert>
      <div className="mb-4 flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted-foreground">{waiting.length} waiting</p>
        <div className="flex gap-2">
          <Button variant="outline" disabled={ids.length === 0 || busy} onClick={() => decide(ids, "reject")}>
            <X className="h-4 w-4" /> Reject selected
          </Button>
          <Button disabled={ids.length === 0 || busy} loading={busy} onClick={() => decide(ids, "approve")}>
            <Check className="h-4 w-4" /> Approve selected{ids.length ? ` (${ids.length})` : ""}
          </Button>
        </div>
      </div>
      <Card>
        <Table>
          <THead>
            <TR>
              <TH className="w-8">
                <input
                  type="checkbox"
                  aria-label="Select all"
                  checked={waiting.length > 0 && ids.length === waiting.length}
                  onChange={(e) => setSelected(e.target.checked ? new Set(waiting.map((w) => w.id)) : new Set())}
                />
              </TH>
              <TH>Computer</TH>
              <TH>Hardware</TH>
              <TH>Operating system</TH>
              <TH>Will be placed in</TH>
              <TH>Requested</TH>
              <TH className="text-right">Decision</TH>
            </TR>
          </THead>
          <TBody>
            {waiting.length === 0 ? (
              <EmptyRow colSpan={7}>No computers are waiting for approval.</EmptyRow>
            ) : (
              waiting.map((i) => (
                <TR key={i.id}>
                  <TD>
                    <input type="checkbox" aria-label={`Select ${i.hostname}`} checked={selected.has(i.id)} onChange={() => toggle(i.id)} />
                  </TD>
                  <TD className="font-medium">
                    {i.hostname}
                    {i.existingDeviceId ? (
                      <p className="text-xs font-normal">
                        <Badge tone="warning">Known computer</Badge> approving re-enrolls {i.existingDeviceHostname ?? "it"}
                      </p>
                    ) : null}
                    <p className="text-xs font-normal text-muted-foreground">via {i.profileName ?? "profile"}{i.sourceAddress ? ` · ${i.sourceAddress}` : ""}</p>
                  </TD>
                  <TD className="text-muted-foreground">
                    {[i.manufacturer, i.model].filter(Boolean).join(" ") || "—"}
                    {i.serialNumber ? <p className="text-xs">S/N {i.serialNumber}</p> : null}
                  </TD>
                  <TD className="text-muted-foreground">{[i.osName, i.osVersion].filter(Boolean).join(" ") || "—"}</TD>
                  <TD className="text-muted-foreground">{[i.branchName, i.departmentName, i.deviceGroupName].filter(Boolean).join(" / ") || "No branch or group"}</TD>
                  <TD className="text-muted-foreground">{formatDate(i.requestedAtUtc)}</TD>
                  <TD className="text-right">
                    <div className="flex justify-end gap-1">
                      <Button size="sm" disabled={busy} aria-label={`Approve ${i.hostname}`} onClick={() => decide([i.id], "approve")}>
                        Approve
                      </Button>
                      <Button size="sm" variant="outline" disabled={busy} aria-label={`Reject ${i.hostname}`} onClick={() => decide([i.id], "reject")}>
                        Reject
                      </Button>
                    </div>
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
    </>
  );
}
