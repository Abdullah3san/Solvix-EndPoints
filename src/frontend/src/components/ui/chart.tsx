"use client";

import * as React from "react";
import { Bar, BarChart, CartesianGrid, Cell, LabelList, ResponsiveContainer, Tooltip as RechartsTooltip, XAxis, YAxis } from "recharts";
import { ChartNoAxesColumn, Table2 } from "lucide-react";
import { cn } from "@/lib/utils";
import { chartChrome, chartColors } from "@/lib/design-tokens";
import { Button } from "./button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "./card";
import { EmptyState, ErrorState } from "./empty-state";
import { Skeleton } from "./skeleton";
import { Table, TBody, TD, TH, THead, TR } from "./table";

export interface ChartDatum {
  /** Stable identity of the series/category; colour follows this, not the rank. */
  key: string;
  label: string;
  value: number;
  /** CSS colour (a chart token). Defaults to the first categorical colour. */
  color?: string;
  /** Extra line in the tooltip and table, e.g. "Latest release". */
  note?: string;
}

const numberFormat = new Intl.NumberFormat();
const percentFormat = new Intl.NumberFormat(undefined, { style: "percent", maximumFractionDigits: 1 });

export type ChartState = "ready" | "loading" | "empty" | "error";

interface ChartCardProps {
  title: string;
  description?: string;
  state?: ChartState;
  /** Message for the empty or error state. */
  message?: string;
  onRetry?: () => void;
  /** The same data as a table; adds a "View as table" toggle. */
  table?: { columns: [string, string]; rows: ChartDatum[]; valueSuffix?: string };
  /** Reserved height for the plot so loading and empty states don't shift the layout. */
  minHeight?: number;
  badge?: React.ReactNode;
  children: React.ReactNode;
  className?: string;
}

/** Frame for every chart: title, description, table alternative and loading/empty/error states. */
export function ChartCard({ title, description, state = "ready", message, onRetry, table, minHeight = 200, badge, children, className }: ChartCardProps) {
  const [asTable, setAsTable] = React.useState(false);
  return (
    <Card className={cn("flex flex-col", className)}>
      <CardHeader className="flex-row items-start justify-between gap-3">
        <div className="min-w-0 space-y-1">
          <CardTitle className="flex items-center gap-2">
            {title} {badge}
          </CardTitle>
          {description ? <CardDescription>{description}</CardDescription> : null}
        </div>
        {table && state === "ready" ? (
          <Button variant="ghost" size="sm" onClick={() => setAsTable((v) => !v)} aria-pressed={asTable} className="shrink-0 text-muted-foreground">
            {asTable ? <ChartNoAxesColumn /> : <Table2 />} {asTable ? "View as chart" : "View as table"}
          </Button>
        ) : null}
      </CardHeader>
      <CardContent className="flex flex-1 flex-col justify-center" style={{ minHeight }}>
        {state === "loading" ? (
          <div className="space-y-3" aria-busy="true" aria-label={`Loading ${title}`}>
            <Skeleton className="h-4 w-11/12" />
            <Skeleton className="h-4 w-8/12" />
            <Skeleton className="h-4 w-9/12" />
            <Skeleton className="h-4 w-5/12" />
          </div>
        ) : state === "error" ? (
          <ErrorState size="sm" description={message} onRetry={onRetry} />
        ) : state === "empty" ? (
          <EmptyState size="sm" icon={ChartNoAxesColumn} title="No data to chart yet" description={message} />
        ) : asTable && table ? (
          <Table>
            <THead>
              <TR>
                <TH scope="col">{table.columns[0]}</TH>
                <TH scope="col" className="text-right">
                  {table.columns[1]}
                </TH>
              </TR>
            </THead>
            <TBody>
              {table.rows.map((row) => (
                <TR key={row.key}>
                  <TD>
                    {row.label}
                    {row.note ? <span className="ml-2 text-xs text-muted-foreground">{row.note}</span> : null}
                  </TD>
                  <TD className="text-right tabular-nums">
                    {numberFormat.format(row.value)}
                    {table.valueSuffix ?? ""}
                  </TD>
                </TR>
              ))}
            </TBody>
          </Table>
        ) : (
          children
        )}
      </CardContent>
    </Card>
  );
}

function TooltipCard({ datum, total, unit }: { datum: ChartDatum; total?: number; unit: string }) {
  return (
    <div className="rounded-md border border-border bg-popover px-2.5 py-2 text-xs text-popover-foreground shadow-md">
      <p className="flex items-center gap-1.5 font-medium">
        <span className="size-2 rounded-sm" style={{ background: datum.color ?? chartColors[0] }} aria-hidden />
        {datum.label}
      </p>
      <p className="mt-1 tabular-nums text-muted-foreground">
        <span className="font-medium text-foreground">{numberFormat.format(datum.value)}</span> {unit}
        {total ? ` · ${percentFormat.format(datum.value / total)}` : ""}
      </p>
      {datum.note ? <p className="mt-0.5 text-muted-foreground">{datum.note}</p> : null}
    </div>
  );
}

/**
 * Ranked horizontal bars for comparing a handful of categories. One hue by
 * default, values at the bar tips, hover tooltip, hairline grid.
 */
export function HorizontalBarChart({ data, unit, showShare = true, labelWidth = 96 }: { data: ChartDatum[]; unit: string; showShare?: boolean; labelWidth?: number }) {
  const total = data.reduce((sum, d) => sum + d.value, 0);
  const height = Math.max(120, data.length * 36 + 16);
  return (
    <div style={{ height }} role="img" aria-label={`Bar chart: ${data.map((d) => `${d.label} ${numberFormat.format(d.value)} ${unit}`).join(", ")}`}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} layout="vertical" margin={{ top: 4, right: 44, bottom: 4, left: 0 }} barCategoryGap={8} accessibilityLayer={false}>
          <CartesianGrid horizontal={false} stroke={chartChrome.grid} />
          <XAxis type="number" hide domain={[0, "dataMax"]} allowDecimals={false} />
          <YAxis
            type="category"
            dataKey="label"
            width={labelWidth}
            tickLine={false}
            axisLine={{ stroke: chartChrome.axis }}
            tick={{ fill: "var(--foreground)", fontSize: 12 }}
          />
          <RechartsTooltip
            cursor={{ fill: "var(--muted)", opacity: 0.6 }}
            isAnimationActive={false}
            content={({ active, payload }) => {
              const datum = active ? (payload?.[0]?.payload as ChartDatum | undefined) : undefined;
              return datum ? <TooltipCard datum={datum} total={showShare ? total : undefined} unit={unit} /> : null;
            }}
          />
          <Bar dataKey="value" barSize={18} radius={[0, 4, 4, 0]} isAnimationActive={false}>
            {data.map((d) => (
              <Cell key={d.key} fill={d.color ?? chartColors[0]} />
            ))}
            <LabelList dataKey="value" position="right" offset={8} formatter={(value) => numberFormat.format(Number(value))} style={{ fill: "var(--foreground)", fontSize: 12, fontVariantNumeric: "tabular-nums" }} />
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

/**
 * Part-to-whole: a thin segmented bar above a list that names each part with
 * its amount and share. Preferred over a donut for a single split.
 */
export function SegmentedBar({ data, unit, totalLabel }: { data: ChartDatum[]; unit: string; totalLabel: string }) {
  const total = data.reduce((sum, d) => sum + d.value, 0);
  const parts = data.filter((d) => d.value > 0);
  return (
    <div>
      <p className="text-2xl font-semibold leading-8 tracking-tight">
        {numberFormat.format(total)} <span className="text-sm font-normal text-muted-foreground">{totalLabel}</span>
      </p>
      <div className="mt-3 flex h-2.5 w-full gap-0.5 overflow-hidden rounded-full" aria-hidden>
        {total === 0 ? <span className="h-full w-full bg-muted" /> : null}
        {parts.map((d) => (
          <span key={d.key} className="h-full min-w-1 first:rounded-l-full last:rounded-r-full" style={{ width: `${(d.value / total) * 100}%`, background: d.color ?? chartColors[0] }} title={`${d.label}: ${numberFormat.format(d.value)} ${unit}`} />
        ))}
      </div>
      <dl className="mt-4 divide-y divide-border text-sm">
        {data.map((d) => (
          <div key={d.key} className="flex items-center gap-2.5 py-2">
            <span className="size-2.5 shrink-0 rounded-sm" style={{ background: d.color ?? chartColors[0] }} aria-hidden />
            <dt className="min-w-0 flex-1 truncate">{d.label}</dt>
            <dd className="tabular-nums font-medium">{numberFormat.format(d.value)}</dd>
            <dd className="w-14 text-right tabular-nums text-muted-foreground">{total > 0 ? percentFormat.format(d.value / total) : "—"}</dd>
          </div>
        ))}
      </dl>
    </div>
  );
}
