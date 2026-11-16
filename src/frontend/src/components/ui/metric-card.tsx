import * as React from "react";
import Link from "next/link";
import { ArrowUpRight, CircleSlash, TriangleAlert, type LucideIcon } from "lucide-react";
import { cn } from "@/lib/utils";
import type { Tone } from "@/lib/design-tokens";
import { Badge } from "./badge";
import { Skeleton } from "./skeleton";

export type MetricState = "ready" | "loading" | "unavailable" | "error";

export interface MetricCardProps {
  label: string;
  /** The figure. Numbers are formatted with locale grouping. Ignored unless `state` is "ready". */
  value?: number | string | null;
  icon: LucideIcon;
  /** Colours the icon only; the value always uses the text colour. */
  tone?: Tone;
  /** Short context under the value, e.g. "of 240 managed". */
  caption?: React.ReactNode;
  state?: MetricState;
  /** Why there is no value ("unavailable") or what failed ("error"). */
  message?: string;
  /** Makes the whole card a link to the underlying list. */
  href?: string;
  /** Small marker next to the label, e.g. "Demo" in development. */
  badge?: string;
  className?: string;
}

const iconTone: Record<Tone, string> = {
  neutral: "bg-muted text-muted-foreground",
  brand: "bg-secondary text-secondary-foreground",
  info: "bg-info/12 text-info",
  success: "bg-success/12 text-success",
  warning: "bg-warning/14 text-warning",
  danger: "bg-destructive/12 text-destructive",
};

const numberFormat = new Intl.NumberFormat();

export function formatMetric(value: number | string | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return typeof value === "number" ? numberFormat.format(value) : value;
}

/**
 * KPI tile: one figure with a label. It never invents data: with no source it
 * shows "unavailable", and on failure an error — not a zero.
 */
export function MetricCard({ label, value, icon: Icon, tone = "brand", caption, state = "ready", message, href, badge, className }: MetricCardProps) {
  const body = (
    <>
      <div className="flex items-start justify-between gap-3">
        <p className="flex min-w-0 items-center gap-2 text-sm font-medium text-muted-foreground">
          <span className="truncate">{label}</span>
          {badge ? <Badge tone="warning">{badge}</Badge> : null}
        </p>
        <span className={cn("flex size-8 shrink-0 items-center justify-center rounded-md", state === "ready" ? iconTone[tone] : iconTone.neutral)}>
          <Icon className="size-4" aria-hidden />
        </span>
      </div>
      <div className="mt-2 min-h-[3.25rem]">
        {state === "loading" ? (
          <div aria-busy="true" aria-label={`Loading ${label}`}>
            <Skeleton className="h-8 w-20" />
            <Skeleton className="mt-2 h-3.5 w-28" />
          </div>
        ) : state === "unavailable" ? (
          <>
            <p className="text-2xl font-semibold leading-8 text-muted-foreground/70" aria-hidden>
              —
            </p>
            <p className="mt-1 flex items-center gap-1.5 text-xs text-muted-foreground">
              <CircleSlash className="size-3.5 shrink-0" aria-hidden /> {message ?? "Not available"}
            </p>
          </>
        ) : state === "error" ? (
          <>
            <p className="text-2xl font-semibold leading-8 text-muted-foreground/70" aria-hidden>
              —
            </p>
            <p className="mt-1 flex items-center gap-1.5 text-xs text-destructive" role="alert">
              <TriangleAlert className="size-3.5 shrink-0" aria-hidden /> {message ?? "Couldn't load"}
            </p>
          </>
        ) : (
          <>
            <p className="text-2xl font-semibold leading-8 tracking-tight">{formatMetric(value)}</p>
            {caption ? <p className="mt-1 truncate text-xs text-muted-foreground">{caption}</p> : null}
          </>
        )}
      </div>
    </>
  );

  const classes = cn("group relative block rounded-lg border border-border bg-card p-4 text-card-foreground shadow-xs", className);
  if (href && state === "ready") {
    return (
      <Link href={href} className={cn(classes, "transition-colors hover:border-ring/50")} aria-label={`${label}: ${formatMetric(value)}. View details`}>
        {body}
        <ArrowUpRight className="absolute bottom-3 right-3 size-4 text-muted-foreground opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100" aria-hidden />
      </Link>
    );
  }
  return <div className={classes}>{body}</div>;
}
