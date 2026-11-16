import type { SlaState, TicketPriority, TicketStatus } from "@/lib/api/types";

type Tone = "neutral" | "brand" | "success" | "warning" | "danger";

export const ticketStatuses: TicketStatus[] = ["New", "Open", "InProgress", "WaitingOnRequester", "Resolved", "Closed"];
export const ticketPriorities: TicketPriority[] = ["Low", "Medium", "High", "Critical"];

export function statusLabel(status: TicketStatus): string {
  switch (status) {
    case "InProgress":
      return "In progress";
    case "WaitingOnRequester":
      return "Waiting on requester";
    default:
      return status;
  }
}

export function statusTone(status: TicketStatus): Tone {
  switch (status) {
    case "New":
      return "brand";
    case "Open":
    case "InProgress":
      return "warning";
    case "WaitingOnRequester":
      return "neutral";
    case "Resolved":
      return "success";
    default:
      return "neutral";
  }
}

export function priorityTone(priority: TicketPriority): Tone {
  switch (priority) {
    case "Critical":
      return "danger";
    case "High":
      return "warning";
    case "Medium":
      return "brand";
    default:
      return "neutral";
  }
}

export function slaTone(state: SlaState): Tone {
  switch (state) {
    case "breached":
      return "danger";
    case "at_risk":
      return "warning";
    case "ok":
    case "met":
      return "success";
    default:
      return "neutral";
  }
}

export function slaLabel(state: SlaState): string {
  switch (state) {
    case "at_risk":
      return "At risk";
    case "breached":
      return "Breached";
    case "paused":
      return "Paused";
    case "met":
      return "Met";
    case "ok":
      return "On track";
    default:
      return "No SLA";
  }
}

/** "2h 30m left" / "overdue by 1d 2h" — compact countdown to an SLA deadline. */
export function formatDue(due: string | null | undefined, now: Date = new Date()): string {
  if (!due) return "—";
  const ms = new Date(due).getTime() - now.getTime();
  if (Number.isNaN(ms)) return "—";
  const abs = Math.abs(ms);
  const minutes = Math.floor(abs / 60_000);
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const mins = minutes % 60;
  const text = days > 0 ? `${days}d ${hours}h` : hours > 0 ? `${hours}h ${mins}m` : `${mins}m`;
  return ms >= 0 ? `${text} left` : `overdue by ${text}`;
}

/** "90" -> "1h 30m"; used for SLA targets in the settings screens. */
export function formatMinutes(minutes: number): string {
  if (!Number.isFinite(minutes) || minutes < 0) return "—";
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const mins = minutes % 60;
  return [days ? `${days}d` : "", hours ? `${hours}h` : "", mins || (!days && !hours) ? `${mins}m` : ""].filter(Boolean).join(" ");
}

/** Bit 0 = Sunday ... bit 6 = Saturday (matches the API). */
export const weekdayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"] as const;

export function maskToDays(mask: number): number[] {
  return weekdayNames.map((_, i) => i).filter((i) => (mask & (1 << i)) !== 0);
}

export function daysToMask(days: number[]): number {
  return days.reduce((mask, d) => mask | (1 << d), 0);
}

export function minuteToTime(minute: number): string {
  const h = Math.floor(minute / 60);
  const m = minute % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}

export function timeToMinute(value: string): number | null {
  const match = /^(\d{1,2}):(\d{2})$/.exec(value);
  if (!match) return null;
  const minute = Number(match[1]) * 60 + Number(match[2]);
  return minute <= 1440 ? minute : null;
}
