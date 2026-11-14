"use client";

import * as React from "react";
import { Check, ShieldAlert, X } from "lucide-react";
import { api } from "@/lib/api/client";
import type { ProposalDto, ProposalStatus, RiskLevel, TenantRole } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Card, CardContent, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { useCan } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";

const riskTone: Record<RiskLevel, "success" | "warning" | "danger"> = { Low: "success", Medium: "warning", High: "danger" };
const statusTone: Record<ProposalStatus, "neutral" | "brand" | "success" | "warning" | "danger"> = {
  PendingApproval: "warning",
  Executing: "brand",
  Succeeded: "success",
  PartiallyFailed: "warning",
  Failed: "danger",
  Rejected: "neutral",
  Expired: "neutral",
};

const roleRank: Record<TenantRole, number> = { Member: 1, Admin: 2, Owner: 3 };

export function ProposalCard({ proposal, onChanged }: { proposal: ProposalDto; onChanged?: (p: ProposalDto) => void }) {
  const { role } = useCan();
  const { toast } = useToast();
  // The server copy wins until this card itself records a decision (then we show that result).
  const [decided, setDecided] = React.useState<ProposalDto | null>(null);
  const current = decided && decided.id === proposal.id ? decided : proposal;
  const [confirming, setConfirming] = React.useState(false);
  const [busy, setBusy] = React.useState<"approve" | "reject" | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  const pending = current.status === "PendingApproval";
  const canDecide = roleRank[role] >= roleRank[current.requiredApproverRole];

  async function decide(kind: "approve" | "reject") {
    setBusy(kind);
    setError(null);
    try {
      const updated =
        kind === "approve"
          ? await api.post<ProposalDto>(`assistant/proposals/${current.id}/approve`, { confirm: true })
          : await api.post<ProposalDto>(`assistant/proposals/${current.id}/reject`, { reason: null });
      setDecided(updated);
      onChanged?.(updated);
      toast({ tone: updated.status === "Failed" ? "error" : "success", title: kind === "approve" ? `Action ${updated.status}` : "Proposal rejected" });
      setConfirming(false);
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setBusy(null);
    }
  }

  return (
    <Card className="text-sm">
      <CardHeader>
        <div className="flex flex-wrap items-center gap-2">
          <CardTitle>{current.title}</CardTitle>
          <Badge tone={riskTone[current.risk]}>{current.risk} risk</Badge>
          <Badge tone={statusTone[current.status]}>{current.status}</Badge>
        </div>
        <p className="text-muted-foreground">{current.rationale}</p>
      </CardHeader>
      <CardContent className="space-y-2">
        <p>
          <span className="font-medium">Action:</span> <code className="rounded bg-muted px-1">{current.action}</code>
          {Object.entries(current.parameters).map(([k, v]) => (
            <span key={k} className="ml-2 text-muted-foreground">
              {k}=<code>{v}</code>
            </span>
          ))}
        </p>
        <p>
          <span className="font-medium">Targets ({current.targetCount}):</span>{" "}
          {current.targets.slice(0, 12).map((t) => t.name).join(", ")}
          {current.targets.length > 12 ? ` … +${current.targets.length - 12} more` : ""}
        </p>
        {current.results.length > 0 ? (
          <ul className="list-disc pl-5 text-muted-foreground">
            {current.results.slice(0, 10).map((r) => (
              <li key={r.deviceId}>
                {current.targets.find((t) => t.id === r.deviceId)?.name ?? r.deviceId}: {r.status} – {r.message}
              </li>
            ))}
          </ul>
        ) : null}
        {current.decidedByEmail ? (
          <p className="text-muted-foreground">
            {current.status === "Rejected" ? "Rejected" : "Approved"} by {current.decidedByEmail}
          </p>
        ) : null}
        {error ? <Alert tone="error">{error}</Alert> : null}
      </CardContent>
      {pending ? (
        <CardFooter>
          {canDecide ? (
            <>
              <Button size="sm" onClick={() => setConfirming(true)}>
                <Check className="h-4 w-4" /> Review &amp; approve
              </Button>
              <Button size="sm" variant="outline" loading={busy === "reject"} onClick={() => decide("reject")}>
                <X className="h-4 w-4" /> Reject
              </Button>
            </>
          ) : (
            <span className="flex items-center gap-1 text-xs text-muted-foreground">
              <ShieldAlert className="h-4 w-4" /> Needs approval from a {current.requiredApproverRole}.
            </span>
          )}
          <span className="ml-auto text-xs text-muted-foreground">Expires {new Date(current.expiresAtUtc).toLocaleString()}</span>
        </CardFooter>
      ) : null}
      <Dialog
        open={confirming}
        onClose={() => setConfirming(false)}
        title="Approve this action?"
        description={`This will run "${current.action}" on ${current.targetCount} device(s). The backend re-validates it before executing.`}
        footer={
          <>
            <Button variant="outline" onClick={() => setConfirming(false)}>
              Cancel
            </Button>
            <Button variant={current.risk === "High" ? "destructive" : "primary"} loading={busy === "approve"} onClick={() => decide("approve")}>
              Approve and execute
            </Button>
          </>
        }
      >
        <p className="text-sm">{current.title}</p>
        {error ? <Alert tone="error" className="mt-3">{error}</Alert> : null}
      </Dialog>
    </Card>
  );
}
