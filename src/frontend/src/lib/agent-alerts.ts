import type { AlertSeverity, DeviceAlertDto } from "@/lib/api/types";
import type { Tone } from "@/lib/design-tokens";

export const severityTone: Record<AlertSeverity, Tone> = { Critical: "danger", Warning: "warning", Info: "info" };

/**
 * Where "View all" goes for an alert. Alerts are derived on the server; the web app only decides the
 * destination. Certificate alerts have no list filter, so they link to the sampled devices instead.
 */
export function alertHref(alert: Pick<DeviceAlertDto, "code">): string | null {
  switch (alert.code) {
    case "agent_unhealthy":
      return "/endpoints/devices?health=Unhealthy";
    case "agent_degraded":
      return "/endpoints/devices?health=Degraded";
    case "not_reporting":
      return "/endpoints/devices?notSeenHours=24";
    case "agent_outdated":
      return "/endpoints/devices?updateAvailable=true";
    case "staged_pending":
      return "/endpoints/staged?status=Pending";
    default:
      return null;
  }
}
