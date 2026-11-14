import type { AlertStatus, CheckOutcome, ComplianceBand, FindingStatus, RemediationStatus, Severity } from "@/lib/api/security-types";

export type Tone = "neutral" | "brand" | "success" | "warning" | "danger";

/** The only place severity is mapped to colour, so the meaning stays consistent across every page. */
export function severityTone(severity: Severity): Tone {
  switch (severity) {
    case "Critical":
    case "High":
      return "danger";
    case "Medium":
      return "warning";
    case "Low":
      return "brand";
    default:
      return "neutral";
  }
}

export const severityOrder: Severity[] = ["Critical", "High", "Medium", "Low", "Info"];

export function bandTone(band: ComplianceBand): Tone {
  switch (band) {
    case "Compliant":
      return "success";
    case "AtRisk":
      return "warning";
    case "NonCompliant":
      return "danger";
    default:
      return "neutral";
  }
}

export function bandLabel(band: ComplianceBand): string {
  switch (band) {
    case "AtRisk":
      return "At risk";
    case "NonCompliant":
      return "Non-compliant";
    case "Unknown":
      return "No recent data";
    default:
      return band;
  }
}

export function outcomeLabel(outcome: CheckOutcome): string {
  switch (outcome) {
    case "Pass":
      return "Passed";
    case "Fail":
      return "Failed";
    case "Unknown":
      return "Could not be checked";
    default:
      return "Not applicable";
  }
}

export function outcomeTone(outcome: CheckOutcome): Tone {
  return outcome === "Pass" ? "success" : outcome === "Fail" ? "danger" : "neutral";
}

export function findingStatusTone(status: FindingStatus): Tone {
  return status === "Open" ? "danger" : status === "Accepted" ? "warning" : "success";
}

export function alertStatusTone(status: AlertStatus): Tone {
  return status === "New" ? "danger" : status === "Acknowledged" ? "warning" : "neutral";
}

export function remediationStatusTone(status: RemediationStatus): Tone {
  switch (status) {
    case "PendingApproval":
      return "warning";
    case "Approved":
    case "Dispatched":
      return "brand";
    case "Succeeded":
      return "success";
    case "Failed":
    case "Rejected":
      return "danger";
    default:
      return "neutral";
  }
}

/** "PendingApproval" -> "Pending approval". */
export function humanize(value: string): string {
  const words = value.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

export function timeAgo(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return "never";
  const seconds = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
  if (seconds < 60) return "just now";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} h ago`;
  return `${Math.round(hours / 24)} days ago`;
}

export const productScopeShort =
  "Solvix reports and manages the security configuration of Windows devices. It is not antivirus or EDR software: it does not detect malware or block threats itself, and a compliance score describes configuration, not whether a device is free of compromise.";
