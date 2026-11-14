"use client";

import * as React from "react";
import { ShieldAlert } from "lucide-react";
import { api } from "@/lib/api/client";
import { messageFrom } from "@/lib/forms";
import type { AlertStatus, ComplianceBand, FindingStatus, Severity } from "@/lib/api/security-types";
import { alertStatusTone, bandLabel, bandTone, findingStatusTone, productScopeShort, severityTone } from "@/lib/security";
import { Badge } from "@/components/ui/badge";
import { Alert } from "@/components/ui/alert";

export function SeverityBadge({ severity }: { severity: Severity }) {
  return <Badge tone={severityTone(severity)}>{severity}</Badge>;
}

export function BandBadge({ band }: { band: ComplianceBand }) {
  return <Badge tone={bandTone(band)}>{bandLabel(band)}</Badge>;
}

export function FindingStatusBadge({ status }: { status: FindingStatus }) {
  return <Badge tone={findingStatusTone(status)}>{status}</Badge>;
}

export function AlertStatusBadge({ status }: { status: AlertStatus }) {
  return <Badge tone={alertStatusTone(status)}>{status}</Badge>;
}

/** Score with its band. Shows "—" when there is no score, never a misleading 0. */
export function ScoreBadge({ score, band }: { score: number | null; band: ComplianceBand }) {
  return (
    <span className="inline-flex items-center gap-2">
      <span className="w-8 text-right font-mono text-sm tabular-nums">{score ?? "—"}</span>
      <BandBadge band={band} />
    </span>
  );
}

/** Reminder of what the product is and is not; shown on pages that present compliance results. */
export function ScopeNotice() {
  return (
    <Alert tone="info" title="What this shows">
      {productScopeShort}
    </Alert>
  );
}

export function EmptyState({ title, children }: { title: string; children?: React.ReactNode }) {
  return (
    <div className="flex flex-col items-center gap-2 py-10 text-center text-sm text-muted-foreground">
      <ShieldAlert className="h-6 w-6" aria-hidden />
      <p className="font-medium text-foreground">{title}</p>
      {children ? <p className="max-w-md">{children}</p> : null}
    </div>
  );
}

export interface Resource<T> {
  data: T | null;
  error: string | null;
  loading: boolean;
  reload: () => Promise<void>;
}

/** Loads `path` (with query) through the BFF and exposes reload. Re-runs when the serialised query changes. */
export function useResource<T>(path: string, query?: Record<string, string | number | boolean | null | undefined>): Resource<T> {
  const [state, setState] = React.useState<{ data: T | null; error: string | null; loading: boolean }>({ data: null, error: null, loading: true });
  const [tick, setTick] = React.useState(0);
  const key = JSON.stringify(query ?? {});

  React.useEffect(() => {
    let cancelled = false;
    api
      .get<T>(path, JSON.parse(key))
      .then((data) => {
        if (!cancelled) setState({ data, error: null, loading: false });
      })
      .catch((e: unknown) => {
        if (!cancelled) setState((s) => ({ data: s.data, error: messageFrom(e, "Could not load data."), loading: false }));
      });
    return () => {
      cancelled = true;
    };
  }, [path, key, tick]);

  const reload = React.useCallback(async () => {
    setTick((t) => t + 1);
  }, []);

  return { data: state.data, error: state.error, loading: state.loading, reload };
}

export function LoadState<T>({ resource, children }: { resource: Resource<T>; children: (data: T) => React.ReactNode }) {
  if (resource.error && !resource.data) return <Alert tone="error" title="Could not load">{resource.error}</Alert>;
  if (!resource.data) return <p className="py-8 text-sm text-muted-foreground">Loading…</p>;
  return <>{children(resource.data)}</>;
}
