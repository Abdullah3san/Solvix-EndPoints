"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { CircleCheck, ClipboardList, Info, Monitor, OctagonAlert, RefreshCw, TriangleAlert, WifiOff } from "lucide-react";
import type { AgentOverviewData } from "@/lib/agent/overview";
import { alertHref, severityTone } from "@/lib/agent-alerts";
import type { AlertSeverity, DeviceAlertDto, DeviceOverviewDto, DeviceStatsDto, NamedCountDto } from "@/lib/api/types";
import { chartChrome, chartColors } from "@/lib/design-tokens";
import { formatDate } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { ChartCard, HorizontalBarChart, SegmentedBar, type ChartDatum } from "@/components/ui/chart";
import { EmptyState, ErrorState } from "@/components/ui/empty-state";
import { MetricCard } from "@/components/ui/metric-card";
import { PageHeader } from "@/components/ui/page-header";
import { Table, TBody, TD, TH, THead, TR } from "@/components/ui/table";
import { DownloadAgentButton } from "./download-agent";

const severityIcon: Record<AlertSeverity, React.ComponentType<{ className?: string }>> = { Critical: OctagonAlert, Warning: TriangleAlert, Info: Info };
const severityText: Record<AlertSeverity, string> = { Critical: "text-destructive", Warning: "text-warning", Info: "text-info" };

const healthColor: Record<string, string> = {
  Healthy: "var(--success)",
  Degraded: "var(--warning)",
  Unhealthy: "var(--destructive)",
  Unknown: chartChrome.neutral,
};

function toData(rows: NamedCountDto[], color: string = chartColors[0]): ChartDatum[] {
  return rows.map((r) => ({ key: r.key, label: r.label, value: r.count, color }));
}

export function AlertList({ alerts }: { alerts: DeviceAlertDto[] }) {
  if (alerts.length === 0) {
    return <EmptyState size="sm" icon={CircleCheck} title="No alerts" description="Every agent is reporting normally and nothing is waiting to be installed." />;
  }
  return (
    <ul className="divide-y divide-border">
      {alerts.map((alert) => {
        const Icon = severityIcon[alert.severity];
        const href = alertHref(alert);
        return (
          <li key={alert.code} className="flex gap-3 px-5 py-3.5">
            <Icon className={`mt-0.5 size-4 shrink-0 ${severityText[alert.severity]}`} aria-hidden />
            <div className="min-w-0 flex-1">
              <p className="flex flex-wrap items-center gap-2 text-sm font-medium">
                {alert.title} <Badge tone={severityTone[alert.severity]}>{alert.severity}</Badge>
              </p>
              <p className="mt-0.5 text-sm text-muted-foreground">{alert.message}</p>
              {alert.devices.length > 0 ? (
                <p className="mt-1.5 flex flex-wrap gap-x-3 gap-y-1 text-sm">
                  {alert.devices.map((d) => (
                    <Link key={d.id} href={`/endpoints/devices/${d.id}`} className="font-medium text-link hover:underline">
                      {d.name}
                    </Link>
                  ))}
                  {alert.count > alert.devices.length ? <span className="text-muted-foreground">and {alert.count - alert.devices.length} more</span> : null}
                </p>
              ) : null}
            </div>
            {href ? (
              <Button asChild variant="outline" size="sm" className="shrink-0 self-start">
                <Link href={href}>View all {alert.count}</Link>
              </Button>
            ) : null}
          </li>
        );
      })}
    </ul>
  );
}

function BranchTable({ overview }: { overview: DeviceOverviewDto }) {
  return (
    <Table aria-label="Computers by branch">
      <THead>
        <TR>
          <TH scope="col">Branch</TH>
          <TH scope="col" className="text-right">
            Computers
          </TH>
          <TH scope="col" className="text-right">
            Online
          </TH>
          <TH scope="col" className="text-right">
            Offline
          </TH>
        </TR>
      </THead>
      <TBody>
        {overview.branches.map((b) => (
          <TR key={b.branchId ?? "none"}>
            <TD className="font-medium">
              {b.branchId ? (
                <Link href={`/endpoints/devices?branchId=${b.branchId}`} className="hover:text-link hover:underline">
                  {b.name}
                </Link>
              ) : (
                <span className="text-muted-foreground">{b.name}</span>
              )}
            </TD>
            <TD className="text-right tabular-nums">{b.total}</TD>
            <TD className="text-right tabular-nums">{b.online}</TD>
            <TD className="text-right tabular-nums">{b.offline}</TD>
          </TR>
        ))}
      </TBody>
    </Table>
  );
}

/** Agent overview: installation state, platform and version mix, health, per-branch fleet and derived alerts. */
export function AgentOverviewView({ data }: { data: AgentOverviewData }) {
  const router = useRouter();
  const [refreshing, startRefresh] = React.useTransition();
  const refresh = () => startRefresh(() => router.refresh());

  const stats: DeviceStatsDto | null = data.stats.ok ? data.stats.value : null;
  const overview = data.overview.ok ? data.overview.value : null;
  const hasDevices = (stats?.active ?? 0) > 0;
  const outdated = stats?.updateAvailable ?? 0;

  const versions: ChartDatum[] = (stats?.agentVersions ?? []).map((v) => ({
    key: v.version,
    label: `v${v.version}`,
    value: v.deviceCount,
    color: v.updateAvailable ? chartColors[1] : chartColors[0],
    note: v.isLatest ? "Latest release" : v.updateAvailable ? "Update available" : undefined,
  }));
  const health: ChartDatum[] = (overview?.health ?? []).map((h) => ({ key: h.key, label: h.label, value: h.count, color: healthColor[h.key] ?? chartChrome.neutral }));

  const chartState = (ok: boolean, empty: boolean) => (!ok ? "error" : empty ? "empty" : "ready");

  return (
    <>
      <PageHeader
        title="Agent overview"
        description={
          <>
            Installation, version and health of the agents in this workspace. Updated <time dateTime={data.generatedAtUtc}>{formatDate(data.generatedAtUtc)}</time>.
          </>
        }
        actions={
          <>
            <Button variant="outline" onClick={refresh} loading={refreshing}>
              {refreshing ? null : <RefreshCw />} Refresh
            </Button>
            <DownloadAgentButton />
          </>
        }
      />

      <section aria-label="Agent status" className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <MetricCard label="Managed computers" value={stats?.active} icon={Monitor} tone="brand" state={data.stats.ok ? "ready" : "error"} message="Couldn't load" href="/endpoints/devices?status=Active" caption="Agent installed and enrolled" />
        <MetricCard label="Online" value={stats?.online} icon={CircleCheck} tone="success" state={data.stats.ok ? "ready" : "error"} message="Couldn't load" href="/endpoints/devices?connectivity=Online" caption="Reported recently" />
        <MetricCard label="Offline" value={stats?.offline} icon={WifiOff} tone={(stats?.offline ?? 0) > 0 ? "warning" : "neutral"} state={data.stats.ok ? "ready" : "error"} message="Couldn't load" href="/endpoints/devices?connectivity=Offline" caption="Not reporting right now" />
        <MetricCard
          label="Yet to install"
          value={data.staged.ok ? data.staged.value.pending : undefined}
          icon={ClipboardList}
          tone={data.staged.ok && data.staged.value.pending > 0 ? "info" : "neutral"}
          state={data.staged.ok ? "ready" : "error"}
          message="Couldn't load"
          href="/endpoints/staged?status=Pending"
          caption="Expected computers without the agent"
        />
      </section>

      <Card className="mt-4">
        <CardHeader>
          <CardTitle>Alerts</CardTitle>
          <CardDescription>Conditions that need attention, calculated from what the agents report right now.</CardDescription>
        </CardHeader>
        <CardContent className="px-0 pb-0">
          {data.alerts.ok ? <AlertList alerts={data.alerts.value} /> : <ErrorState size="sm" description={data.alerts.message} onRetry={refresh} retrying={refreshing} />}
        </CardContent>
      </Card>

      <section aria-label="Fleet breakdown" className="mt-4 grid gap-4 lg:grid-cols-2">
        <ChartCard
          title="OS platform"
          description="Managed computers by platform."
          state={chartState(data.overview.ok, !hasDevices)}
          message={data.overview.ok ? "Download the agent and install it to see platforms." : data.overview.message}
          onRetry={refresh}
          minHeight={140}
          table={overview ? { columns: ["Platform", "Computers"], rows: toData(overview.platforms) } : undefined}
        >
          {overview ? <HorizontalBarChart data={toData(overview.platforms)} unit="computers" labelWidth={88} /> : null}
          <p className="mt-2 text-xs text-muted-foreground">Only Windows agents exist today; macOS and Linux agents are planned.</p>
        </ChartCard>

        <ChartCard
          title="Operating systems"
          description="Editions and versions in use."
          state={chartState(data.overview.ok, !hasDevices)}
          message={data.overview.ok ? "No computers have reported their operating system yet." : data.overview.message}
          onRetry={refresh}
          minHeight={140}
          table={overview ? { columns: ["Operating system", "Computers"], rows: toData(overview.operatingSystems) } : undefined}
        >
          {overview ? <HorizontalBarChart data={toData(overview.operatingSystems, chartColors[2])} unit="computers" labelWidth={168} /> : null}
        </ChartCard>

        <ChartCard
          title="Agent versions"
          description={
            stats?.latestAgentVersion
              ? outdated > 0
                ? `${outdated} ${outdated === 1 ? "computer is" : "computers are"} behind v${stats.latestAgentVersion}.`
                : `All computers run the latest release, v${stats.latestAgentVersion}.`
              : "Installed agent versions."
          }
          state={chartState(data.stats.ok, versions.length === 0)}
          message={data.stats.ok ? "No agents are reporting a version yet." : data.stats.message}
          onRetry={refresh}
          minHeight={140}
          table={{ columns: ["Agent version", "Computers"], rows: versions }}
        >
          <HorizontalBarChart data={versions} unit="computers" labelWidth={88} />
        </ChartCard>

        <ChartCard
          title="Agent health"
          description="Self-reported state of each agent."
          state={chartState(data.overview.ok, !hasDevices)}
          message={data.overview.ok ? "Health appears here once agents report in." : data.overview.message}
          onRetry={refresh}
          minHeight={140}
          table={{ columns: ["Health", "Computers"], rows: health }}
        >
          <SegmentedBar data={health} unit="computers" totalLabel="computers" />
        </ChartCard>
      </section>

      <Card className="mt-4">
        <CardHeader>
          <CardTitle>Computers by branch</CardTitle>
          <CardDescription>Where the agents are installed.</CardDescription>
        </CardHeader>
        <CardContent className="px-0 pb-0">
          {!data.overview.ok ? (
            <ErrorState size="sm" description={data.overview.message} onRetry={refresh} retrying={refreshing} />
          ) : data.overview.value.branches.length === 0 ? (
            <EmptyState size="sm" icon={Monitor} title="No computers yet" description="Computers appear here, grouped by branch, after the agent is installed." />
          ) : (
            <BranchTable overview={data.overview.value} />
          )}
        </CardContent>
      </Card>
    </>
  );
}
