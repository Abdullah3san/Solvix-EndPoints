"use client";

import Link from "next/link";
import { Download } from "lucide-react";
import type { ComplianceBand, SecuritySummary, Severity } from "@/lib/api/security-types";
import { PROXY_PREFIX } from "@/lib/api/client";
import { bandLabel, bandTone, humanize, severityOrder } from "@/lib/security";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { SeverityBadge, ScopeNotice, LoadState, useResource } from "./security-ui";

const bands: ComplianceBand[] = ["Compliant", "AtRisk", "NonCompliant", "Unknown"];

function Stat({ label, value, hint, href }: { label: string; value: React.ReactNode; hint?: string; href?: string }) {
  const body = (
    <Card className="h-full">
      <CardContent className="p-4">
        <p className="text-xs uppercase tracking-wide text-muted-foreground">{label}</p>
        <p className="mt-1 text-2xl font-semibold tabular-nums">{value}</p>
        {hint ? <p className="mt-1 text-xs text-muted-foreground">{hint}</p> : null}
      </CardContent>
    </Card>
  );
  return href ? (
    <Link href={href} className="block rounded-lg focus-visible:outline focus-visible:outline-2">
      {body}
    </Link>
  ) : (
    body
  );
}

/** Minimal accessible trend: an SVG polyline plus a text summary for screen readers. */
function Trend({ points }: { points: SecuritySummary["trend"] }) {
  if (points.length < 2) return <p className="text-sm text-muted-foreground">The trend appears after devices have reported on at least two days.</p>;
  const w = 300;
  const h = 60;
  const coords = points.map((p, i) => `${(i / (points.length - 1)) * w},${h - (p.averageScore / 100) * h}`).join(" ");
  const first = points[0];
  const last = points[points.length - 1];
  return (
    <div>
      <svg viewBox={`0 0 ${w} ${h}`} className="h-20 w-full" role="img" aria-label={`Average score moved from ${first.averageScore} to ${last.averageScore} over ${points.length} days`}>
        <polyline fill="none" stroke="currentColor" strokeWidth="2" className="text-primary" points={coords} />
      </svg>
      <p className="text-xs text-muted-foreground">
        Average score, last {points.length} days: {first.averageScore} → {last.averageScore}
      </p>
    </div>
  );
}

export function SecurityOverview() {
  const resource = useResource<SecuritySummary>("security/summary");
  return (
    <LoadState resource={resource}>
      {(s) => (
        <div className="space-y-6">
          <ScopeNotice />
          {s.totalDevices === 0 ? (
            <Alert tone="info" title="No devices yet">
              <Link href="/security/devices" className="underline">
                Enrol a device
              </Link>{" "}
              to start receiving posture reports.
            </Alert>
          ) : null}
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-5">
            <Stat label="Devices" value={s.totalDevices} hint={`${s.reportingDevices} reporting, ${s.staleDevices} stale`} href="/security/devices" />
            <Stat label="Average score" value={s.averageScore ?? "—"} hint="Reporting devices only" />
            <Stat label="Open alerts" value={s.openAlerts} href="/security/alerts" />
            <Stat label="Awaiting approval" value={s.pendingRemediationApprovals + s.pendingPolicyApprovals} hint={`${s.pendingPolicyApprovals} policy, ${s.pendingRemediationApprovals} remediation`} href="/security/approvals" />
            <Stat label="Enforcement" value={s.enforcementEnabled ? "Approval-gated" : "Detect only"} hint={s.activePolicy} href="/security/policies" />
          </div>

          <div className="grid gap-4 lg:grid-cols-3">
            <Card>
              <CardHeader>
                <CardTitle>Compliance</CardTitle>
                <CardDescription>Devices by band. “No recent data” means no report within the staleness limit; it is not a pass.</CardDescription>
              </CardHeader>
              <CardContent className="space-y-2">
                {bands.map((b) => (
                  <div key={b} className="flex items-center justify-between text-sm">
                    <Badge tone={bandTone(b)}>{bandLabel(b)}</Badge>
                    <span className="tabular-nums">{s.bands[b] ?? 0}</span>
                  </div>
                ))}
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Open findings</CardTitle>
                <CardDescription>By severity. Severity meanings are on the Rules page.</CardDescription>
              </CardHeader>
              <CardContent className="space-y-2">
                {severityOrder
                  .filter((sev: Severity) => sev !== "Info")
                  .map((sev) => (
                    <div key={sev} className="flex items-center justify-between text-sm">
                      <SeverityBadge severity={sev} />
                      <Link href={`/security/findings?minSeverity=${sev}`} className="tabular-nums underline-offset-2 hover:underline">
                        {s.openFindingsBySeverity[sev] ?? 0}
                      </Link>
                    </div>
                  ))}
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Score trend</CardTitle>
                <CardDescription>Average of reported scores per day.</CardDescription>
              </CardHeader>
              <CardContent>
                <Trend points={s.trend} />
              </CardContent>
            </Card>
          </div>

          <Card>
            <CardHeader>
              <CardTitle>Most widespread issues</CardTitle>
              <CardDescription>Ordered by severity, then by number of affected devices.</CardDescription>
            </CardHeader>
            <CardContent>
              {s.topRules.length === 0 ? (
                <p className="text-sm text-muted-foreground">No open findings.</p>
              ) : (
                <ul className="divide-y divide-border">
                  {s.topRules.map((r) => (
                    <li key={r.ruleKey} className="flex items-center justify-between gap-3 py-2 text-sm">
                      <span className="flex items-center gap-2">
                        <SeverityBadge severity={r.severity} />
                        <Link href={`/security/findings?ruleKey=${r.ruleKey}`} className="hover:underline">
                          {r.title}
                        </Link>
                        <span className="text-xs text-muted-foreground">{humanize(r.category)}</span>
                      </span>
                      <span className="tabular-nums text-muted-foreground">{r.affectedDevices} device(s)</span>
                    </li>
                  ))}
                </ul>
              )}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Reports</CardTitle>
              <CardDescription>CSV exports are recorded in the audit trail.</CardDescription>
            </CardHeader>
            <CardContent className="flex flex-wrap gap-2">
              <a href={`${PROXY_PREFIX}/security/reports/devices.csv`} download>
                <Button variant="outline">
                  <Download className="h-4 w-4" /> Device compliance (CSV)
                </Button>
              </a>
              <a href={`${PROXY_PREFIX}/security/reports/findings.csv`} download>
                <Button variant="outline">
                  <Download className="h-4 w-4" /> Open findings (CSV)
                </Button>
              </a>
              <a href={`${PROXY_PREFIX}/security/reports/findings.csv?includeResolved=true`} download>
                <Button variant="outline">
                  <Download className="h-4 w-4" /> All findings incl. resolved (CSV)
                </Button>
              </a>
            </CardContent>
          </Card>
        </div>
      )}
    </LoadState>
  );
}
