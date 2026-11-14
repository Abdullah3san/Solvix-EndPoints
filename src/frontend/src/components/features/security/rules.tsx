"use client";

import type { RulesCatalog } from "@/lib/api/security-types";
import { humanize } from "@/lib/security";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { LoadState, SeverityBadge, useResource } from "./security-ui";

export function RulesView() {
  const resource = useResource<RulesCatalog>("security/rules");
  return (
    <LoadState resource={resource}>
      {(c) => (
        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>What Solvix is — and is not</CardTitle>
            </CardHeader>
            <CardContent className="text-sm">{c.productScope}</CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>Severity</CardTitle>
            </CardHeader>
            <CardContent>
              <ul className="divide-y divide-border text-sm">
                {c.severities.map((s) => (
                  <li key={s.severity} className="flex flex-wrap items-start gap-3 py-2">
                    <SeverityBadge severity={s.severity} />
                    <div className="flex-1">
                      <p>{s.meaning}</p>
                      <p className="text-xs text-muted-foreground">Example: {s.example}</p>
                    </div>
                    <span className="font-mono text-xs">{s.scorePenalty === 0 ? "no points" : `−${s.scorePenalty}`}</span>
                  </li>
                ))}
              </ul>
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>How the compliance score works</CardTitle>
            </CardHeader>
            <CardContent className="space-y-2 text-sm">
              <p>{c.scoring.formula}</p>
              <p>{c.scoring.bandRules}</p>
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>Remediation actions Solvix can ask an agent to run</CardTitle>
              <CardDescription>This is the complete list. Anything else (BitLocker, administrator removal, uninstalling software) is manual.</CardDescription>
            </CardHeader>
            <CardContent>
              <ul className="divide-y divide-border text-sm">
                {c.remediationActions.map((a) => (
                  <li key={a.key} className="py-2">
                    <div className="flex items-center gap-2">
                      <Badge tone={a.risk === "High" ? "danger" : a.risk === "Medium" ? "warning" : "neutral"}>{a.risk} risk</Badge>
                      <span className="font-medium">{a.title}</span>
                      <span className="font-mono text-xs text-muted-foreground">{a.key}</span>
                    </div>
                    <p className="text-muted-foreground">{a.description}</p>
                    <p className="text-xs text-muted-foreground">Impact: {a.impact}</p>
                  </li>
                ))}
              </ul>
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>All rules</CardTitle>
            </CardHeader>
            <CardContent className="p-0">
              {c.rules.map((r) => (
                <details key={r.key} className="border-b border-border last:border-0">
                  <summary className="flex cursor-pointer list-none flex-wrap items-center gap-2 px-5 py-2.5 text-sm hover:bg-muted/50">
                    <SeverityBadge severity={r.defaultSeverity} />
                    <span className="font-medium">{r.title}</span>
                    <span className="text-xs text-muted-foreground">{humanize(r.category)}</span>
                    <span className="ml-auto font-mono text-xs text-muted-foreground">{r.key}</span>
                  </summary>
                  <div className="space-y-1 bg-muted/30 px-5 py-3 text-sm">
                    <p><span className="font-medium">Checks: </span>{r.whatItChecks}</p>
                    <p><span className="font-medium">Why it matters: </span>{r.whyItMatters}</p>
                    <p><span className="font-medium">Why {r.defaultSeverity}: </span>{r.severityRationale}</p>
                    <p><span className="font-medium">How to fix: </span>{r.howToFix}</p>
                    <p className="text-xs text-muted-foreground">Source: {r.windowsInterface}{r.remediationActionKey ? ` · can be remediated with ${r.remediationActionKey} (needs policy + approval)` : " · manual fix only"}</p>
                  </div>
                </details>
              ))}
            </CardContent>
          </Card>
        </div>
      )}
    </LoadState>
  );
}
