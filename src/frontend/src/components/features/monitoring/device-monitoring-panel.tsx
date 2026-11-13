"use client";

import * as React from "react";
import { api } from "@/lib/api/client";
import type { DeviceMonitoringDto } from "@/lib/api/types";
import { formatDuration, formatPercent, networkTone, osTone, stateTone } from "@/lib/monitoring";
import { formatRelative } from "@/lib/devices";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { useMonitoringUpdates } from "@/components/providers/realtime-provider";
import { EventFeed } from "./event-feed";

/**
 * The three independent signals of one device. They are deliberately shown
 * side by side and never merged: an agent that reaches the platform says
 * nothing about the device's network services or its operating system.
 */
export function DeviceMonitoringPanel({ deviceId, initial }: { deviceId: string; initial: DeviceMonitoringDto | null }) {
  const [data, setData] = React.useState<DeviceMonitoringDto | null>(initial);

  const load = React.useCallback(async () => {
    try {
      setData(await api.get<DeviceMonitoringDto>(`monitoring/devices/${deviceId}`));
    } catch {
      // keep the last snapshot
    }
  }, [deviceId]);

  React.useEffect(() => {
    const timer = window.setInterval(load, 60_000); // keeps relative times and availability current
    return () => window.clearInterval(timer);
  }, [load]);

  useMonitoringUpdates((update) => {
    if (update.events.some((e) => e.deviceId === deviceId) || update.statusChanges.some((c) => c.deviceId === deviceId)) void load();
  });

  if (!data) return <p className="text-sm text-muted-foreground">Monitoring data is not available for this device yet.</p>;

  const t = data.thresholds;
  return (
    <div className="space-y-4">
      <div className="grid gap-4 lg:grid-cols-3 [&>*]:min-w-0">
        <Card>
          <CardHeader>
            <CardDescription>A · Agent connectivity</CardDescription>
            <CardTitle className="flex items-center gap-2">
              <Badge tone={data.monitored ? stateTone(data.connectivity) : "neutral"}>{data.monitored ? data.connectivity : "Not monitored"}</Badge>
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-1.5 text-sm">
            <Row label="Last seen" value={formatRelative(data.lastSeenAtUtc)} title={data.lastSeenAtUtc} />
            <Row label="Last successful communication" value={formatRelative(data.lastCommunicationAtUtc)} title={data.lastCommunicationAtUtc} />
            <Row label="Last heartbeat" value={formatRelative(data.lastHeartbeatAtUtc)} title={data.lastHeartbeatAtUtc} />
            {data.connectivity === "Offline" ? (
              <Row label="Current downtime" value={formatDuration(data.currentDowntimeSeconds)} />
            ) : (
              <Row label="Connected for" value={formatDuration(data.currentUptimeSeconds)} />
            )}
            <p className="pt-1 text-xs text-muted-foreground">
              Warning after {formatDuration(t.warningAfterSeconds)}, offline after {formatDuration(t.offlineAfterSeconds)} without contact.
            </p>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardDescription>B · Network reachability</CardDescription>
            <CardTitle className="flex items-center gap-2">
              <Badge tone={networkTone(data.network.status, data.network.stale)}>{data.network.status}</Badge>
              {data.network.stale ? <span className="text-xs font-normal text-muted-foreground">last known</span> : null}
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-1.5 text-sm">
            <Row label="Reading stored" value={formatRelative(data.network.checkedAtUtc)} />
            {data.network.issues.map((i) => (
              <p key={i} className="text-xs text-warning">
                {i}
              </p>
            ))}
            <p className="pt-1 text-xs text-muted-foreground">
              Gateway, DNS and configured internal services, probed from the device with every heartbeat. Changes are recorded at once;
              unchanged readings are stored every few minutes.
            </p>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardDescription>C · Operating-system health</CardDescription>
            <CardTitle className="flex items-center gap-2">
              <Badge tone={osTone(data.operatingSystem.status, data.operatingSystem.stale)}>{data.operatingSystem.status}</Badge>
              {data.operatingSystem.stale ? <span className="text-xs font-normal text-muted-foreground">last known</span> : null}
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-1.5 text-sm">
            <Row label="System uptime" value={formatDuration(data.systemUptimeSeconds)} />
            <Row label="Agent running since" value={formatRelative(data.agentStartedAtUtc)} title={data.agentStartedAtUtc} />
            {data.operatingSystem.issues.map((i) => (
              <p key={i} className="text-xs text-warning">
                {i}
              </p>
            ))}
          </CardContent>
        </Card>
      </div>
      <div className="grid gap-4 lg:grid-cols-3 [&>*]:min-w-0">
        <Card>
          <CardHeader>
            <CardTitle>Availability</CardTitle>
          </CardHeader>
          <CardContent className="space-y-1.5 text-sm">
            <Row label="Today" value={formatPercent(data.today.availabilityPercent)} />
            <Row label="Last 7 days" value={formatPercent(data.last7Days.availabilityPercent)} />
            <Row label="Last 30 days" value={formatPercent(data.last30Days.availabilityPercent)} />
            <Row label="Downtime (30 d)" value={formatDuration(data.last30Days.downtimeSeconds)} />
            <Row label="Offline incidents (30 d)" value={String(data.last30Days.offlineCount)} />
          </CardContent>
        </Card>
        <Card className="lg:col-span-2">
          <CardHeader>
            <CardTitle>Connection history</CardTitle>
          </CardHeader>
          <CardContent>
            <EventFeed events={data.recentEvents.slice(0, 15)} />
          </CardContent>
        </Card>
      </div>
    </div>
  );
}

function Row({ label, value, title }: { label: string; value: string; title?: string | null }) {
  return (
    <div className="flex items-center justify-between gap-4">
      <span className="text-muted-foreground">{label}</span>
      <span className="font-medium" title={title ?? undefined}>
        {value}
      </span>
    </div>
  );
}
