import type * as React from "react";
import type { AssetStatus, EndpointLinkState, MatchConfidence, WarrantyState } from "@/lib/api/types";
import { confidenceTone, linkLabel, linkTone, statusLabel, statusTone, warrantyLabel, warrantyTone } from "@/lib/assets";
import { Badge } from "@/components/ui/badge";

export function AssetStatusBadge({ status }: { status: AssetStatus }) {
  return <Badge tone={statusTone(status)} className="whitespace-nowrap">{statusLabel(status)}</Badge>;
}

export function WarrantyBadge({ state }: { state: WarrantyState }) {
  return <Badge tone={warrantyTone(state)} className="whitespace-nowrap">{warrantyLabel(state)}</Badge>;
}

export function EndpointLinkBadge({ state }: { state: EndpointLinkState }) {
  return state === "NotApplicable" ? null : <Badge tone={linkTone(state)} className="whitespace-nowrap">{linkLabel(state)}</Badge>;
}

export function ConfidenceBadge({ confidence }: { confidence: MatchConfidence }) {
  return <Badge tone={confidenceTone(confidence)}>{confidence} confidence</Badge>;
}

export function InfoRows({ rows }: { rows: [string, React.ReactNode][] }) {
  return (
    <dl className="grid grid-cols-[minmax(0,10rem)_minmax(0,1fr)] gap-x-4 gap-y-2 text-sm">
      {rows.map(([label, value]) => (
        <div key={label} className="contents">
          <dt className="text-muted-foreground">{label}</dt>
          <dd className="min-w-0 [overflow-wrap:anywhere]">{value === null || value === undefined || value === "" ? "—" : value}</dd>
        </div>
      ))}
    </dl>
  );
}
