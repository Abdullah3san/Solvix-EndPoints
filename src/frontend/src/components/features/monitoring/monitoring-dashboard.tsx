"use client";

import * as React from "react";
import Link from "next/link";
import { Activity, BellRing, CircleCheck, CircleX, Monitor, Settings2, TriangleAlert } from "lucide-react";
import { api } from "@/lib/api/client";
import type { MonitoringAlertDto, MonitoringSummaryDto, PagedResult, StateCountsDto } from "@/lib/api/types";
import { applyUpdate, formatDuration, formatPercent } from "@/lib/monitoring";
import { messageFrom } from "@/lib/forms";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { PageHeader } from "@/components/ui/page-header";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { LiveIndicator, useMonitoringUpdates } from "@/components/providers/realtime-provider";
import { useCan } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";
import { EventFeed } from "./event-feed";
import { formatRelative } from "@/lib/devices";

const REFRESH_MS = 60_000;

export function MonitoringDashboard({ initial, initialAlerts }: { initial: MonitoringSummaryDto; initialAlerts: MonitoringAlertDto[] }) {
  const canManage = useCan("devices.manage");
  const { toast } = useToast();
  const [summary, setSummary] = React.useState(initial);
  const [alerts, setAlerts] = React.useState(initialAlerts);
  const refetchTimer = React.useRef<number | undefined>(undefined);

  const refresh = React.useCallback(async () => {
    try {
      const [next, open] = await Promise.all([
        api.get<MonitoringSummaryDto>("monitoring/summary"),
        api.get<PagedResult<MonitoringAlertDto>>("monitoring/alerts", { status: "Open", pageSize: 20 }),
      ]);
      setSummary(next);
      setAlerts(open.items);
    } catch {
      // keep the last snapshot; the live indicator shows connectivity problems
    }
  }, []);

  // Live: move counters instantly, then reconcile derived figures with one debounced refetch.
  useMonitoringUpdates((update) => {
    setSummary((current) => applyUpdate(current, update));
    if (update.alerts.length) {
      setAlerts((list) => {
        const resolved = new Set(update.alerts.filter((a) => a.status === "Resolved").map((a) => a.id));
        const opened = update.alerts.filter((a) => a.status === "Open" && !list.some((x) => x.id === a.id));
        return [...opened, ...list.filter((a) => !resolved.has(a.id))];
      });
    }
    window.clearTimeout(refetchTimer.current);
    refetchTimer.current = window.setTimeout(refresh, 2000);
  });

  React.useEffect(() => {
    const timer = window.setInterval(refresh, REFRESH_MS); // keeps "x min ago" and availability current
    return () => {
      window.clearInterval(timer);
      window.clearTimeout(refetchTimer.current);
    };
  }, [refresh]);

  async function acknowledge(alert: MonitoringAlertDto) {
    try {
      const updated = await api.post<MonitoringAlertDto>(`monitoring/alerts/${alert.id}/acknowledge`);
      setAlerts((list) => list.map((a) => (a.id === updated.id ? updated : a)));
    } catch (e) {
      toast({ tone: "error", title: "Could not acknowledge alert", description: messageFrom(e) });
    }
  }

  const d = summary.devices;
  const s = summary.settings;
  const tiles = [
    { label: "Total devices", value: d.total, icon: Monitor, href: "/endpoints/devices?status=Active", tone: "text-foreground" },
    { label: "Online", value: d.online, icon: CircleCheck, href: "/endpoints/devices?connectivity=Online", tone: "text-success" },
    { label: "Warning", value: d.warning, icon: TriangleAlert, href: "/endpoints/devices?connectivity=Warning", tone: "text-warning" },
    { label: "Offline", value: d.offline, icon: CircleX, href: "/endpoints/devices?connectivity=Offline", tone: "text-destructive" },
  ];

  return (
    <>
      <PageHeader
        title="Monitoring"
        description={`Agent connectivity: online below ${formatDuration(s.warningAfterSeconds)} without contact, warning up to ${formatDuration(s.offlineAfterSeconds)}, offline after that.`}
        actions={
          <>
            <LiveIndicator className="mr-2" />
            <Link href="/endpoints/monitoring/availability" className={buttonClasses({ variant: "outline", size: "sm" })}>
              Availability report
            </Link>
            {canManage ? (
              <Link href="/endpoints/monitoring/settings" className={buttonClasses({ variant: "outline", size: "sm" })}>
                <Settings2 className="h-4 w-4" /> Settings
              </Link>
            ) : null}
          </>
        }
      />

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6">
        {tiles.map((t) => (
          <Link key={t.label} href={t.href} className="rounded-lg">
            <Card className="h-full transition-colors hover:border-primary/50">
              <CardContent className="flex items-center gap-3 p-4">
                <t.icon className={`h-5 w-5 ${t.tone}`} aria-hidden />
                <div>
                  <p className="text-2xl font-semibold leading-none tabular-nums" data-testid={`tile-${t.label}`}>{t.value}</p>
                  <p className="mt-1 text-xs text-muted-foreground">{t.label}</p>
                </div>
              </CardContent>
            </Card>
          </Link>
        ))}
        <Card>
          <CardContent className="flex items-center gap-3 p-4">
            <Activity className="h-5 w-5 text-primary" aria-hidden />
            <div>
              <p className="text-2xl font-semibold leading-none tabular-nums">{formatPercent(summary.availability7dPercent)}</p>
              <p className="mt-1 text-xs text-muted-foreground">Availability 7 d · 30 d {formatPercent(summary.availability30dPercent)}</p>
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="flex items-center gap-3 p-4">
            <BellRing className={`h-5 w-5 ${summary.openAlerts ? "text-destructive" : "text-muted-foreground"}`} aria-hidden />
            <div>
              <p className="text-2xl font-semibold leading-none tabular-nums">{summary.openAlerts}</p>
              <p className="mt-1 text-xs text-muted-foreground">Open offline alerts</p>
            </div>
          </CardContent>
        </Card>
      </div>

      <StateBar counts={d} className="mt-4" />

      <div className="mt-6 grid gap-4 xl:grid-cols-3 [&>*]:min-w-0">
        <Card className="xl:col-span-2">
          <CardHeader>
            <CardTitle>Devices by branch</CardTitle>
            <CardDescription>Current agent connectivity and availability over the last 7 days.</CardDescription>
          </CardHeader>
          <CardContent className="p-0">
            <Table>
              <THead>
                <TR>
                  <TH>Branch</TH>
                  <TH className="text-right">Devices</TH>
                  <TH className="text-right">Online</TH>
                  <TH className="text-right">Warning</TH>
                  <TH className="text-right">Offline</TH>
                  <TH className="text-right">Availability</TH>
                </TR>
              </THead>
              <TBody>
                {summary.branches.length === 0 ? <EmptyRow colSpan={6}>No enrolled devices yet.</EmptyRow> : null}
                {summary.branches.map((b) => (
                  <TR key={b.branchId ?? "unassigned"}>
                    <TD className="font-medium">
                      {b.name} {b.code ? <span className="text-xs text-muted-foreground">({b.code})</span> : null}
                    </TD>
                    <CountCells counts={b.devices} />
                    <TD className="text-right tabular-nums">{formatPercent(b.availability7dPercent)}</TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Device signals</CardTitle>
            <CardDescription>
              Measured on the device by the agent. A heartbeat proves only that the agent reaches the platform, not that the network or the OS is
              healthy. Disconnected devices count as unknown.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-4 text-sm">
            <SignalRow
              title="Network reachability"
              entries={[
                ["Reachable", summary.signals.network.Reachable, "success"],
                ["Degraded", summary.signals.network.Degraded, "warning"],
                ["Unreachable", summary.signals.network.Unreachable, "danger"],
                ["Unknown", summary.signals.network.Unknown, "neutral"],
              ]}
            />
            <SignalRow
              title="Operating-system health"
              entries={[
                ["Healthy", summary.signals.operatingSystem.Healthy, "success"],
                ["Warning", summary.signals.operatingSystem.Warning, "warning"],
                ["Critical", summary.signals.operatingSystem.Critical, "danger"],
                ["Unknown", summary.signals.operatingSystem.Unknown, "neutral"],
              ]}
            />
          </CardContent>
        </Card>

        <Card className="xl:col-span-2">
          <CardHeader>
            <CardTitle>Devices by operating system</CardTitle>
          </CardHeader>
          <CardContent className="p-0">
            <Table>
              <THead>
                <TR>
                  <TH>Operating system</TH>
                  <TH className="text-right">Devices</TH>
                  <TH className="text-right">Online</TH>
                  <TH className="text-right">Warning</TH>
                  <TH className="text-right">Offline</TH>
                </TR>
              </THead>
              <TBody>
                {summary.operatingSystems.length === 0 ? <EmptyRow colSpan={5}>No data.</EmptyRow> : null}
                {summary.operatingSystems.map((o) => (
                  <TR key={o.operatingSystem}>
                    <TD className="font-medium">{o.operatingSystem}</TD>
                    <CountCells counts={o.devices} />
                  </TR>
                ))}
              </TBody>
            </Table>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Open alerts</CardTitle>
            <CardDescription>Raised automatically once a device exceeds the offline timeout; resolved when it reconnects.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-3 text-sm">
            {alerts.length === 0 ? <p className="text-muted-foreground">No open alerts.</p> : null}
            {alerts.map((a) => (
              <div key={a.id} className="flex items-start justify-between gap-3 rounded-md border border-border p-3">
                <div className="min-w-0">
                  <Link href={`/endpoints/devices/${a.deviceId}`} className="font-medium hover:underline">
                    {a.deviceName}
                  </Link>
                  <p className="text-xs text-muted-foreground">Last contact {formatRelative(a.lastSeenAtUtc)}</p>
                </div>
                {a.acknowledgedAtUtc ? (
                  <Badge>Acknowledged</Badge>
                ) : canManage ? (
                  <Button size="sm" variant="outline" onClick={() => acknowledge(a)}>
                    Acknowledge
                  </Button>
                ) : null}
              </div>
            ))}
          </CardContent>
        </Card>

        <Card className="xl:col-span-3">
          <CardHeader>
            <CardTitle>Recent connection events</CardTitle>
          </CardHeader>
          <CardContent>
            <EventFeed events={summary.recentEvents} showDevice />
          </CardContent>
        </Card>
      </div>
    </>
  );
}

function CountCells({ counts }: { counts: StateCountsDto }) {
  return (
    <>
      <TD className="text-right tabular-nums">{counts.total}</TD>
      <TD className="text-right tabular-nums text-success">{counts.online}</TD>
      <TD className={`text-right tabular-nums ${counts.warning ? "text-warning" : "text-muted-foreground"}`}>{counts.warning}</TD>
      <TD className={`text-right tabular-nums ${counts.offline ? "text-destructive" : "text-muted-foreground"}`}>{counts.offline}</TD>
    </>
  );
}

export function StateBar({ counts, className }: { counts: StateCountsDto; className?: string }) {
  if (counts.total === 0) return null;
  const pct = (n: number) => `${(100 * n) / counts.total}%`;
  return (
    <div
      className={`flex h-2 w-full overflow-hidden rounded-full bg-muted ${className ?? ""}`}
      role="img"
      aria-label={`${counts.online} online, ${counts.warning} warning, ${counts.offline} offline`}
    >
      <div className="bg-success transition-all" style={{ width: pct(counts.online) }} />
      <div className="bg-warning transition-all" style={{ width: pct(counts.warning) }} />
      <div className="bg-destructive transition-all" style={{ width: pct(counts.offline) }} />
    </div>
  );
}

function SignalRow({ title, entries }: { title: string; entries: [string, number, "success" | "warning" | "danger" | "neutral"][] }) {
  return (
    <div>
      <p className="mb-2 font-medium">{title}</p>
      <div className="flex flex-wrap gap-2">
        {entries.map(([label, value, tone]) => (
          <Badge key={label} tone={tone}>
            {label} · {value}
          </Badge>
        ))}
      </div>
    </div>
  );
}
