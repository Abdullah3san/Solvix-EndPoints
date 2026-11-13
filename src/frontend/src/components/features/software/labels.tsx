import { Badge } from "@/components/ui/badge";
import type { DeploymentStatus, PackageStatus, TargetStatus } from "@/lib/api/types";

type Tone = "neutral" | "brand" | "success" | "warning" | "danger";

const deploymentTone: Record<DeploymentStatus, Tone> = {
  PendingApproval: "warning",
  Scheduled: "brand",
  Running: "brand",
  Completed: "success",
  CompletedWithErrors: "warning",
  Failed: "danger",
  Cancelled: "neutral",
  Rejected: "danger",
  Expired: "neutral",
};

const targetTone: Record<TargetStatus, Tone> = {
  Pending: "neutral",
  Dispatched: "brand",
  Running: "brand",
  Succeeded: "success",
  Failed: "danger",
  Cancelled: "neutral",
  Skipped: "neutral",
  TimedOut: "danger",
  Expired: "warning",
};

const packageTone: Record<PackageStatus, Tone> = { Ready: "success", Corrupted: "danger", Retired: "neutral" };

export const humanize = (value: string) => value.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/_/g, " ");

export const DeploymentStatusBadge = ({ status }: { status: DeploymentStatus }) => <Badge tone={deploymentTone[status]}>{humanize(status)}</Badge>;
export const TargetStatusBadge = ({ status }: { status: TargetStatus }) => <Badge tone={targetTone[status]}>{humanize(status)}</Badge>;
export const PackageStatusBadge = ({ status }: { status: PackageStatus }) => <Badge tone={packageTone[status]}>{status}</Badge>;

export const sensitivityLabels: Record<string, string> = {
  unsigned_package: "Package is unsigned",
  untrusted_publisher: "Publisher is not on the trusted list",
  package_marked_sensitive: "Package is marked sensitive",
  uninstall: "Uninstallation",
  large_deployment: "Large deployment",
  server_targets: "Targets servers",
};

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(1)} ${units[unit]}`;
}
