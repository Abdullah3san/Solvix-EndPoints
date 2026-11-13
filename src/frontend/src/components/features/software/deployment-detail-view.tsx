"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Download } from "lucide-react";
import { api, PROXY_PREFIX } from "@/lib/api/client";
import type { DeploymentDetailDto, DeploymentReport } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Textarea } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Table, THead, TBody, TR, TH, TD } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan, useSession } from "@/components/providers/session-provider";
import { DeploymentStatusBadge, TargetStatusBadge, humanize, sensitivityLabels } from "./labels";

const ACTIVE = new Set(["PendingApproval", "Scheduled", "Running"]);

export function DeploymentDetailView({ initial, initialReport }: { initial: DeploymentDetailDto; initialReport: DeploymentReport }) {
  const { toast } = useToast();
  const router = useRouter();
  const session = useSession();
  const canDeploy = useCan("software.deploy");
  const canApprove = useCan("software.approve");
  const [detail, setDetail] = React.useState(initial);
  const [report, setReport] = React.useState(initialReport);
  const [comment, setComment] = React.useState("");
  const [deciding, setDeciding] = React.useState<"approve" | "reject" | null>(null);
  const [busy, setBusy] = React.useState(false);
  const d = detail.deployment;
  const me = session.user.id;
  const alreadyDecided = detail.approvals.some((a) => a.approverId === me);

  const reload = React.useCallback(async () => {
    const [next, nextReport] = await Promise.all([
      api.get<DeploymentDetailDto>(`software/deployments/${initial.deployment.id}`),
      api.get<DeploymentReport>(`software/deployments/${initial.deployment.id}/report`),
    ]);
    setDetail(next);
    setReport(nextReport);
  }, [initial.deployment.id]);

  // Follow a deployment that is still in progress.
  React.useEffect(() => {
    if (!ACTIVE.has(d.status)) return;
    const timer = setInterval(() => void reload().catch(() => undefined), 10_000);
    return () => clearInterval(timer);
  }, [d.status, reload]);

  async function act(path: string, body?: unknown, success?: string) {
    setBusy(true);
    try {
      await api.post(`software/deployments/${d.id}/${path}`, body);
      if (success) toast({ tone: "success", title: success });
      setDeciding(null);
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Action failed", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  async function redeploy() {
    setBusy(true);
    try {
      const created = await api.post<DeploymentDetailDto>(`software/deployments/${d.id}/redeploy-failed`);
      router.push(`/software/deployments/${created.deployment.id}`);
      setBusy(false);
    } catch (e) {
      toast({ tone: "error", title: "Could not redeploy", description: messageFrom(e) });
      setBusy(false);
    }
  }

  const c = d.counts;
  const failed = c.failed + c.timedOut + c.expired;
  return (
    <>
      <PageHeader
        title={d.name}
        description={`${d.action} ${d.packageName} ${d.packageVersion ?? ""} · requested ${formatDate(d.requestedAtUtc)}`}
        actions={
          <div className="flex flex-wrap gap-2">
            {d.status === "PendingApproval" && canApprove && d.createdBy !== me && !alreadyDecided ? (
              <>
                <Button onClick={() => setDeciding("approve")}>Approve</Button>
                <Button variant="outline" onClick={() => setDeciding("reject")}>Reject</Button>
              </>
            ) : null}
            {ACTIVE.has(d.status) && canDeploy ? (
              <Button variant="destructive" loading={busy} onClick={() => window.confirm("Cancel this deployment? Installations in progress are asked to stop.") && void act("cancel", undefined, "Deployment cancelled")}>Cancel deployment</Button>
            ) : null}
            {failed > 0 && !ACTIVE.has(d.status) && canDeploy ? <Button variant="outline" loading={busy} onClick={redeploy}>Redeploy failed devices</Button> : null}
          </div>
        }
      />

      <div className="grid gap-4 md:grid-cols-3">
        <Card>
          <CardHeader><CardTitle>Status</CardTitle></CardHeader>
          <CardContent className="space-y-2 text-sm">
            <DeploymentStatusBadge status={d.status} />
            <p>Window: {formatDate(d.scheduledStartUtc)} → {formatDate(d.deadlineUtc)}</p>
            <p>Approvals: {d.approvalsReceived} of {d.requiredApprovals}</p>
            {d.isSensitive ? <p className="text-muted-foreground">Sensitive: {d.sensitivityReasons.map((r) => sensitivityLabels[r] ?? r).join("; ")}</p> : null}
          </CardContent>
        </Card>
        <Card>
          <CardHeader><CardTitle>Results</CardTitle></CardHeader>
          <CardContent className="space-y-1 text-sm">
            <p className="text-2xl font-semibold">{report.successRate}%</p>
            <p>{c.succeeded} succeeded · {failed} failed · {c.inProgress + c.pending} in progress · {c.skipped} skipped · {c.cancelled} cancelled</p>
            {report.rebootRequired > 0 ? <p>{report.rebootRequired} device(s) need a restart</p> : null}
            {report.averageDurationSeconds ? <p className="text-muted-foreground">Average {Math.round(report.averageDurationSeconds)} s per device</p> : null}
          </CardContent>
        </Card>
        <Card>
          <CardHeader><CardTitle>Failure report</CardTitle></CardHeader>
          <CardContent className="space-y-1 text-sm">
            {report.failures.length === 0 ? <p className="text-muted-foreground">No failures.</p> : report.failures.map((f) => (
              <p key={f.errorCode}><strong>{f.devices}×</strong> {humanize(f.errorCode)}{f.example ? <span className="block text-xs text-muted-foreground">{f.example}</span> : null}</p>
            ))}
            <a className="mt-2 inline-flex items-center gap-1 text-primary hover:underline" href={`${PROXY_PREFIX}/software/deployments/${d.id}/report.csv`}>
              <Download className="h-4 w-4" /> Download report (CSV)
            </a>
          </CardContent>
        </Card>
      </div>

      {detail.approvals.length > 0 ? (
        <Card className="mt-4">
          <CardHeader><CardTitle>Approvals</CardTitle></CardHeader>
          <CardContent className="space-y-1 text-sm">
            {detail.approvals.map((a) => <p key={a.id}>{a.decision} · {formatDate(a.decidedAtUtc)}{a.comment ? ` — ${a.comment}` : ""}</p>)}
          </CardContent>
        </Card>
      ) : null}

      <Card className="mt-4">
        <Table>
          <THead><TR><TH>Device</TH><TH>Status</TH><TH>Attempts</TH><TH>Result</TH><TH>Finished</TH></TR></THead>
          <TBody>
            {detail.targets.map((t) => (
              <TR key={t.id}>
                <TD><Link className="text-primary hover:underline" href={`/endpoints/devices/${t.deviceId}`}>{t.deviceName}</Link></TD>
                <TD><TargetStatusBadge status={t.status} />{t.nextAttemptAtUtc ? <div className="text-xs text-muted-foreground">retry {formatDate(t.nextAttemptAtUtc)}</div> : null}</TD>
                <TD>{t.attempts}</TD>
                <TD className="text-sm">
                  {t.outcome ? humanize(t.outcome) : "—"}{t.detectedVersion ? ` · ${t.detectedVersion}` : ""}{t.rebootRequired ? " · restart needed" : ""}
                  {t.errorMessage ? <div className="text-xs text-destructive">{t.exitCode !== null ? `Exit ${t.exitCode}: ` : ""}{t.errorMessage}</div> : null}
                  {t.logExcerpt ? <details className="text-xs"><summary className="cursor-pointer text-muted-foreground">Installer log</summary><pre className="max-h-48 overflow-auto whitespace-pre-wrap">{t.logExcerpt}</pre></details> : null}
                </TD>
                <TD className="text-sm">{formatDate(t.completedAtUtc)}</TD>
              </TR>
            ))}
          </TBody>
        </Table>
      </Card>

      <Dialog
        open={deciding !== null}
        onClose={() => setDeciding(null)}
        title={deciding === "approve" ? "Approve deployment?" : "Reject deployment?"}
        description={deciding === "approve" ? `${d.name} will be sent to ${d.counts.total} device(s) once it has all ${d.requiredApprovals} approval(s).` : "Nothing will be sent to any device."}
        footer={
          <>
            <Button variant="outline" onClick={() => setDeciding(null)}>Cancel</Button>
            <Button variant={deciding === "reject" ? "destructive" : "primary"} loading={busy} onClick={() => deciding && void act(deciding, { comment: comment || null }, deciding === "approve" ? "Approval recorded" : "Deployment rejected")}>
              {deciding === "approve" ? "Approve" : "Reject"}
            </Button>
          </>
        }
      >
        <Textarea aria-label="Comment" placeholder="Comment (optional)" value={comment} onChange={(e) => setComment(e.target.value)} maxLength={500} />
      </Dialog>
      {!ACTIVE.has(d.status) && failed === 0 && c.succeeded === 0 ? <Alert tone="info" className="mt-4">This deployment ended without running on any device.</Alert> : null}
    </>
  );
}
