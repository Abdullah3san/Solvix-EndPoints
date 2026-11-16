"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import {
  Activity,
  Boxes,
  CircleCheck,
  KeyRound,
  Monitor,
  PackageCheck,
  Plus,
  RefreshCw,
  Rocket,
  ShieldAlert,
  Smartphone,
  TriangleAlert,
  UserRoundCheck,
  WifiOff,
  CircleSlash,
  type LucideIcon,
} from "lucide-react";
import type { ActivityKind, DashboardOverview, Metric, MetricKey } from "@/lib/dashboard/model";
import type { Tone } from "@/lib/design-tokens";
import { chartChrome, chartColors } from "@/lib/design-tokens";
import { formatRelative } from "@/lib/devices";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { ChartCard, HorizontalBarChart, SegmentedBar, type ChartDatum } from "@/components/ui/chart";
import { EmptyState, ErrorState } from "@/components/ui/empty-state";
import { MetricCard } from "@/components/ui/metric-card";
import { PageHeader } from "@/components/ui/page-header";
import { useCan, useSession } from "@/components/providers/session-provider";

interface MetricSpec {
  key: MetricKey;
  label: string;
  icon: LucideIcon;
  tone: Tone | ((value: number) => Tone);
  href?: string;
  caption?: (overview: DashboardOverview, value: number) => string | undefined;
}

const share = (value: number, total: number | undefined) =>
  total && total > 0 ? `${new Intl.NumberFormat(undefined, { style: "percent", maximumFractionDigits: 0 }).format(value / total)} of managed devices` : undefined;

const METRICS: MetricSpec[] = [
  {
    key: "managedDevices",
    label: "Managed devices",
    icon: Monitor,
    tone: "brand",
    href: "/endpoints/devices?status=Active",
    caption: (o) => (o.fleet && o.fleet.retired > 0 ? `${o.fleet.retired} retired not included` : "Enrolled and active"),
  },
  { key: "onlineDevices", label: "Online", icon: CircleCheck, tone: "success", href: "/endpoints/devices?connectivity=Online", caption: (o, v) => share(v, o.fleet?.active) },
  { key: "offlineDevices", label: "Offline", icon: WifiOff, tone: (v) => (v > 0 ? "warning" : "neutral"), href: "/endpoints/devices?connectivity=Offline", caption: (o, v) => share(v, o.fleet?.active) },
  {
    key: "attentionDevices",
    label: "Requiring attention",
    icon: TriangleAlert,
    tone: (v) => (v > 0 ? "danger" : "success"),
    href: "/endpoints/devices?attention=true",
    caption: (_, v) => (v > 0 ? "Agent degraded or unhealthy" : "All agents healthy"),
  },
  { key: "mobileDevices", label: "Managed mobile devices", icon: Smartphone, tone: "info" },
  { key: "itAssets", label: "IT assets", icon: Boxes, tone: "info" },
  { key: "criticalAlerts", label: "Critical security alerts", icon: ShieldAlert, tone: (v) => (v > 0 ? "danger" : "success") },
  { key: "pendingDeployments", label: "Pending deployments", icon: Rocket, tone: "info" },
];

function MetricTile({ spec, metric, overview }: { spec: MetricSpec; metric: Metric; overview: DashboardOverview }) {
  if (metric.status === "unavailable") return <MetricCard label={spec.label} icon={spec.icon} state="unavailable" message={metric.reason} />;
  if (metric.status === "error") return <MetricCard label={spec.label} icon={spec.icon} state="error" message="Couldn't load" />;
  return (
    <MetricCard
      label={spec.label}
      icon={spec.icon}
      value={metric.value}
      tone={typeof spec.tone === "function" ? spec.tone(metric.value) : spec.tone}
      caption={metric.demo ? "Demo value, not real data" : spec.caption?.(overview, metric.value)}
      href={metric.demo ? undefined : spec.href}
      badge={metric.demo ? "Demo" : undefined}
    />
  );
}

const activityIcons: Record<ActivityKind, LucideIcon> = {
  enrollment: KeyRound,
  alert: ShieldAlert,
  deployment: PackageCheck,
  assignment: UserRoundCheck,
  retirement: CircleSlash,
  other: Activity,
};

function RecentActivity({ overview }: { overview: DashboardOverview }) {
  const feed = overview.activity;
  return (
    <Card className="flex flex-col">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          Recent activity {feed.status === "ok" && feed.demo ? <Badge tone="warning">Demo</Badge> : null}
        </CardTitle>
        <CardDescription>Latest changes across the workspace.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-1 flex-col justify-center">
        {feed.status === "unavailable" ? (
          <EmptyState size="sm" icon={Activity} title="No activity feed yet" description={feed.reason} />
        ) : feed.status === "error" ? (
          <ErrorState size="sm" description={feed.message} />
        ) : feed.items.length === 0 ? (
          <EmptyState size="sm" icon={Activity} title="Nothing has happened yet" description="Activity appears here as devices enroll and settings change." />
        ) : (
          <ol className="-my-1 flex-1 divide-y divide-border">
            {feed.items.map((item) => {
              const Icon = activityIcons[item.kind];
              return (
                <li key={item.id} className="flex gap-3 py-2.5">
                  <span className="mt-0.5 flex size-7 shrink-0 items-center justify-center rounded-full bg-muted text-muted-foreground">
                    <Icon className="size-3.5" aria-hidden />
                  </span>
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium">{item.title}</p>
                    <p className="truncate text-xs text-muted-foreground">{[item.detail, item.actor].filter(Boolean).join(" · ")}</p>
                  </div>
                  <time dateTime={item.occurredAtUtc} title={formatDate(item.occurredAtUtc)} className="shrink-0 whitespace-nowrap text-xs text-muted-foreground">
                    {formatRelative(item.occurredAtUtc)}
                  </time>
                </li>
              );
            })}
          </ol>
        )}
      </CardContent>
    </Card>
  );
}

/** Executive overview: KPI tiles, fleet charts and recent activity. */
export function DashboardView({ overview }: { overview: DashboardOverview }) {
  const router = useRouter();
  const { tenant } = useSession();
  const { isAdmin } = useCan();
  const [refreshing, startRefresh] = React.useTransition();
  const refresh = () => startRefresh(() => router.refresh());

  const fleet = overview.fleet;
  const chartState = overview.fleetError ? "error" : !fleet || fleet.total === 0 ? "empty" : "ready";

  const connectivity: ChartDatum[] = fleet
    ? [
        { key: "online", label: "Online", value: fleet.online, color: chartColors[0] },
        { key: "offline", label: "Offline", value: fleet.offline, color: chartColors[1] },
        { key: "retired", label: "Retired", value: fleet.retired, color: chartChrome.neutral },
      ]
    : [];
  const versions: ChartDatum[] = fleet
    ? fleet.agentVersions.map((v) => ({
        key: v.version,
        label: `v${v.version}`,
        value: v.deviceCount,
        color: v.updateAvailable ? chartColors[1] : chartColors[0],
        note: v.isLatest ? "Latest release" : v.updateAvailable ? "Update available" : undefined,
      }))
    : [];
  const outdated = fleet?.updateAvailable ?? 0;

  return (
    <>
      <PageHeader
        title="Overview"
        description={
          <>
            Fleet health and operations for <span className="font-medium text-foreground">{tenant.name}</span>. Updated{" "}
            <time dateTime={overview.generatedAtUtc}>{formatDate(overview.generatedAtUtc)}</time>.
          </>
        }
        actions={
          <>
            <Button variant="outline" onClick={refresh} loading={refreshing}>
              {refreshing ? null : <RefreshCw />} Refresh
            </Button>
            {isAdmin ? (
              <Button asChild>
                <Link href="/endpoints/enrollment">
                  <Plus /> Enroll devices
                </Link>
              </Button>
            ) : null}
          </>
        }
      />

      {overview.demo ? (
        <Alert tone="warning" title="Development demo data is on" className="mb-4">
          Widgets marked <span className="font-medium text-foreground">Demo</span> show placeholder values because their backend modules don&apos;t exist yet. Device
          figures are real. Unset <code className="font-mono text-xs">NEXT_PUBLIC_SOLVIX_DEMO_DATA</code> to hide them; production builds never show demo data.
        </Alert>
      ) : null}

      {overview.fleetError ? (
        <Alert tone="error" title="Device statistics couldn't be loaded" className="mb-4" action={<Button variant="outline" size="sm" onClick={refresh} loading={refreshing}>Try again</Button>}>
          {overview.fleetError}
        </Alert>
      ) : null}

      <section aria-label="Key metrics" className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {METRICS.map((spec) => (
          <MetricTile key={spec.key} spec={spec} metric={overview.metrics[spec.key]} overview={overview} />
        ))}
      </section>

      <section aria-label="Fleet breakdown and activity" className="mt-4 grid gap-4 lg:grid-cols-2 xl:grid-cols-3">
        <ChartCard
          title="Fleet connectivity"
          description="Enrolled devices by current state."
          state={chartState}
          message={overview.fleetError ?? "Enroll your first device to see fleet connectivity."}
          onRetry={refresh}
          minHeight={220}
        >
          <SegmentedBar data={connectivity} unit="devices" totalLabel="enrolled devices" />
        </ChartCard>

        <ChartCard
          title="Agent versions"
          description={
            fleet?.latestAgentVersion
              ? outdated > 0
                ? `${outdated} active ${outdated === 1 ? "device is" : "devices are"} behind v${fleet.latestAgentVersion}.`
                : `All active devices run the latest release, v${fleet.latestAgentVersion}.`
              : "Active devices by installed agent version."
          }
          state={chartState === "ready" && versions.length === 0 ? "empty" : chartState}
          message={overview.fleetError ?? "No active devices are reporting an agent version yet."}
          onRetry={refresh}
          minHeight={220}
          table={{ columns: ["Agent version", "Devices"], rows: versions }}
        >
          <HorizontalBarChart data={versions} unit="devices" />
          {versions.some((v) => v.color === chartColors[1]) ? (
            <ul className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground" aria-label="Legend">
              <li className="flex items-center gap-1.5">
                <span className="size-2.5 rounded-sm" style={{ background: chartColors[0] }} aria-hidden /> Up to date
              </li>
              <li className="flex items-center gap-1.5">
                <span className="size-2.5 rounded-sm" style={{ background: chartColors[1] }} aria-hidden /> Update available
              </li>
            </ul>
          ) : null}
        </ChartCard>

        <div className="lg:col-span-2 xl:col-span-1">
          <RecentActivity overview={overview} />
        </div>
      </section>
    </>
  );
}
