"use client";

import * as React from "react";
import Link from "next/link";
import { api } from "@/lib/api/client";
import type { Policy, Remediation } from "@/lib/api/security-types";
import type { PagedResult } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { humanize, remediationStatusTone, timeAgo } from "@/lib/security";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { useCan, useSession } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";
import { LoadState, useResource } from "./security-ui";
import { NoteDialog } from "./shared";

type Decision = { kind: "remediation" | "policy"; id: string; verb: "approve" | "reject" | "cancel"; label: string };

export function ApprovalsView() {
  const { isAdmin, isOwner } = useCan();
  const { user } = useSession();
  const { toast } = useToast();
  const [status, setStatus] = React.useState<"pending" | "all">("pending");
  const remediation = useResource<PagedResult<Remediation>>("security/remediation", { pendingOnly: status === "pending", pageSize: 50 });
  const policies = useResource<Policy[]>("security/policies");
  const [decision, setDecision] = React.useState<Decision | null>(null);
  const [busy, setBusy] = React.useState(false);

  async function submit(note: string) {
    if (!decision) return;
    setBusy(true);
    try {
      const base = decision.kind === "policy" ? "security/policies" : "security/remediation";
      await api.post(`${base}/${decision.id}/${decision.verb}`, decision.verb === "cancel" ? undefined : { note });
      toast({ tone: "success", title: { approve: "Approved", reject: "Rejected", cancel: "Cancelled" }[decision.verb] });
      setDecision(null);
      await Promise.all([remediation.reload(), policies.reload()]);
    } catch (e) {
      toast({ tone: "error", title: `Could not ${decision.verb}`, description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-6">
      <Alert tone="info" title="How approvals work">
        Detecting a problem never changes a device. A change happens only when the active policy allows the action <em>and</em> a different person approves it (Low-risk actions may be approved automatically if the policy says so).
        You cannot approve something you requested or wrote. High-risk actions and policies need an owner.
      </Alert>

      <Card>
        <CardHeader>
          <CardTitle>Policies awaiting approval</CardTitle>
        </CardHeader>
        <CardContent>
          <LoadState resource={policies}>
            {(items) => {
              const pending = items.filter((p) => p.status === "PendingApproval");
              if (pending.length === 0) return <p className="text-sm text-muted-foreground">Nothing waiting.</p>;
              return (
                <ul className="divide-y divide-border">
                  {pending.map((p) => {
                    const own = p.authorUserId === user.id || p.submittedByUserId === user.id;
                    return (
                      <li key={p.id} className="flex flex-wrap items-center gap-3 py-3 text-sm">
                        <div className="flex-1">
                          <Link href="/security/policies" className="font-medium hover:underline">
                            {p.name} (v{p.version})
                          </Link>
                          <p className="text-xs text-muted-foreground">
                            Enforcement: {humanize(p.settings.enforcementMode)}
                            {p.settings.allowedRemediationActions.length ? ` · actions: ${p.settings.allowedRemediationActions.join(", ")}` : ""} · submitted {timeAgo(p.submittedAtUtc)}
                          </p>
                        </div>
                        {isOwner && !own ? (
                          <>
                            <Button size="sm" onClick={() => setDecision({ kind: "policy", id: p.id, verb: "approve", label: `Approve ${p.name} v${p.version}` })}>
                              Approve
                            </Button>
                            <Button size="sm" variant="outline" onClick={() => setDecision({ kind: "policy", id: p.id, verb: "reject", label: `Reject ${p.name} v${p.version}` })}>
                              Reject
                            </Button>
                          </>
                        ) : (
                          <span className="text-xs text-muted-foreground">{own ? "You wrote or submitted this" : "Needs an owner"}</span>
                        )}
                      </li>
                    );
                  })}
                </ul>
              );
            }}
          </LoadState>
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="flex-row items-center justify-between">
          <div>
            <CardTitle>Remediation requests</CardTitle>
            <CardDescription>Each one names the exact action, the device and what could go wrong.</CardDescription>
          </div>
          <div className="flex gap-1">
            <Button size="sm" variant={status === "pending" ? "primary" : "outline"} onClick={() => setStatus("pending")}>
              Pending
            </Button>
            <Button size="sm" variant={status === "all" ? "primary" : "outline"} onClick={() => setStatus("all")}>
              All
            </Button>
          </div>
        </CardHeader>
        <CardContent>
          <LoadState resource={remediation}>
            {(data) =>
              data.items.length === 0 ? (
                <p className="text-sm text-muted-foreground">No requests.</p>
              ) : (
                <ul className="divide-y divide-border">
                  {data.items.map((r) => {
                    const own = r.requestedByUserId === user.id;
                    const canApprove = isAdmin && !own && (r.risk !== "High" || isOwner);
                    return (
                      <li key={r.id} className="space-y-1 py-3 text-sm">
                        <div className="flex flex-wrap items-center gap-2">
                          <Badge tone={remediationStatusTone(r.status)}>{humanize(r.status)}</Badge>
                          <Badge tone={r.risk === "High" ? "danger" : r.risk === "Medium" ? "warning" : "neutral"}>{r.risk} risk</Badge>
                          <span className="font-medium">
                            {r.actionTitle}
                            {r.parameter ? ` (${r.parameter})` : ""}
                          </span>
                          <span className="text-muted-foreground">on {r.endpointHostname}</span>
                          <span className="ml-auto text-xs text-muted-foreground">{timeAgo(r.requestedAtUtc)}</span>
                        </div>
                        <p className="text-muted-foreground">{r.reason}</p>
                        <p className="text-xs text-muted-foreground">
                          Impact: {r.impact} · Requested by {r.requestedBySystem ? "policy" : own ? "you" : "another administrator"} · Approver: {r.requiredApprover}
                          {r.autoApproved ? " · auto-approved by policy" : ""}
                        </p>
                        {r.resultMessage ? <p className="text-xs">Result: {r.resultMessage}</p> : null}
                        {r.status === "PendingApproval" ? (
                          <div className="flex gap-2 pt-1">
                            {canApprove ? (
                              <>
                                <Button size="sm" onClick={() => setDecision({ kind: "remediation", id: r.id, verb: "approve", label: `Approve “${r.actionTitle}” on ${r.endpointHostname}` })}>
                                  Approve
                                </Button>
                                <Button size="sm" variant="outline" onClick={() => setDecision({ kind: "remediation", id: r.id, verb: "reject", label: `Reject “${r.actionTitle}” on ${r.endpointHostname}` })}>
                                  Reject
                                </Button>
                              </>
                            ) : (
                              <span className="text-xs text-muted-foreground">{own ? "You requested this; someone else must decide." : r.risk === "High" ? "Needs an owner." : ""}</span>
                            )}
                            {isAdmin ? (
                              <Button size="sm" variant="ghost" onClick={() => setDecision({ kind: "remediation", id: r.id, verb: "cancel", label: "Cancel request" })}>
                                Cancel request
                              </Button>
                            ) : null}
                          </div>
                        ) : null}
                      </li>
                    );
                  })}
                </ul>
              )
            }
          </LoadState>
        </CardContent>
      </Card>

      <NoteDialog
        open={decision !== null && decision.verb !== "cancel"}
        title={decision?.label ?? ""}
        confirmLabel={decision ? humanize(decision.verb) : ""}
        noteRequired={decision?.verb === "reject"}
        destructive={decision?.verb === "reject"}
        busy={busy}
        onClose={() => setDecision(null)}
        onConfirm={submit}
      />
      <NoteDialog
        open={decision?.verb === "cancel"}
        title="Cancel this request?"
        description="Nothing will be sent to the device."
        confirmLabel="Cancel request"
        busy={busy}
        onClose={() => setDecision(null)}
        onConfirm={submit}
      />
    </div>
  );
}
