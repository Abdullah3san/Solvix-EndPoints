"use client";

import * as React from "react";
import { Download } from "lucide-react";
import { api, proxyUrl } from "@/lib/api/client";
import type { ReportInfoDto, ReportResultDto } from "@/lib/api/types";
import { formatCell, formatMetric, supportedFilters } from "@/lib/reports";
import { messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";

export function ReportView({ info, initial }: { info: ReportInfoDto; initial: ReportResultDto }) {
  const { toast } = useToast();
  const [result, setResult] = React.useState(initial);
  const [search, setSearch] = React.useState("");
  const [days, setDays] = React.useState("30");
  const [loading, setLoading] = React.useState(false);

  const filters = info.filters.filter((f): f is (typeof supportedFilters)[number] => (supportedFilters as readonly string[]).includes(f));
  const query = React.useMemo(() => ({ search: filters.includes("search") ? search : undefined, days: filters.includes("days") ? days : undefined }), [filters, search, days]);

  async function run() {
    setLoading(true);
    try {
      setResult(await api.get<ReportResultDto>(`reports/${info.key}`, query));
    } catch (e) {
      toast({ tone: "error", title: "Could not run report", description: messageFrom(e) });
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="space-y-6">
      <form
        className="flex flex-wrap items-end gap-3"
        onSubmit={(e) => {
          e.preventDefault();
          void run();
        }}
      >
        {filters.includes("search") ? <Input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Filter by name or serial" aria-label="Search" className="w-64" /> : null}
        {filters.includes("days") ? (
          <label className="flex items-center gap-2 text-sm">
            Last
            <Input type="number" min={1} max={365} value={days} onChange={(e) => setDays(e.target.value)} className="w-20" aria-label="Days" /> days
          </label>
        ) : null}
        <Button type="submit" variant="outline" loading={loading}>
          Run
        </Button>
        <a href={proxyUrl(`reports/${info.key}`, { ...query, format: "csv" })} className="inline-flex h-9 items-center gap-2 rounded-md bg-primary px-4 text-sm text-primary-foreground shadow-sm hover:bg-primary/90">
          <Download className="h-4 w-4" /> Download CSV
        </a>
      </form>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        {result.summary.map((m) => (
          <Card key={m.label}>
            <CardContent className="pt-4">
              <p className="text-xs text-muted-foreground">{m.label}</p>
              <p className="mt-1 text-xl font-semibold">{formatMetric(m.value)}</p>
            </CardContent>
          </Card>
        ))}
      </div>

      {result.notes ? <Alert tone="info">{result.notes}</Alert> : null}
      {result.truncated ? <Alert tone="info">Showing the first rows only. Download the CSV for the full data.</Alert> : null}

      <Card className={loading ? "opacity-60" : undefined}>
        <Table>
          <THead>
            <TR>
              {result.columns.map((c) => (
                <TH key={c.key} className={c.type === "number" ? "text-right" : undefined}>
                  {c.label}
                </TH>
              ))}
            </TR>
          </THead>
          <TBody>
            {result.rows.length === 0 ? (
              <EmptyRow colSpan={Math.max(1, result.columns.length)}>No data for this report yet.</EmptyRow>
            ) : (
              result.rows.map((row, i) => (
                <TR key={i}>
                  {row.map((value, j) => (
                    <TD key={result.columns[j]?.key ?? j} className={result.columns[j]?.type === "number" ? "text-right tabular-nums" : undefined}>
                      {formatCell(value, result.columns[j] ?? { type: "string" })}
                    </TD>
                  ))}
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
      <p className="text-xs text-muted-foreground">Generated {new Date(result.generatedAtUtc).toLocaleString()}</p>
    </div>
  );
}
