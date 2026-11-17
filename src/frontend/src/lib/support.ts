import type { RemoteAccessMode, RemoteSessionStatus, TicketPriority, TicketStatus } from "@/lib/api/types";

type Tone = "neutral" | "brand" | "success" | "warning" | "danger";

export const ticketStatuses: TicketStatus[] = ["Open", "InProgress", "Resolved", "Closed"];
export const ticketPriorities: TicketPriority[] = ["Low", "Normal", "High", "Urgent"];

export function statusLabel(status: TicketStatus): string {
  return status === "InProgress" ? "In progress" : status;
}

export function statusTone(status: TicketStatus): Tone {
  switch (status) {
    case "Open":
      return "warning";
    case "InProgress":
      return "brand";
    case "Resolved":
      return "success";
    default:
      return "neutral";
  }
}

export function priorityTone(priority: TicketPriority): Tone {
  switch (priority) {
    case "Urgent":
      return "danger";
    case "High":
      return "warning";
    case "Normal":
      return "brand";
    default:
      return "neutral";
  }
}

/** Highest sequence in a chat thread (0 when empty); the next poll asks for messages after it. */
export function lastSequence(messages: { sequence: number }[]): number {
  return messages.reduce((max, m) => Math.max(max, m.sequence), 0);
}

/** Appends messages we have not seen yet, keeping the thread ordered by sequence. */
export function mergeMessages<T extends { sequence: number }>(current: T[], incoming: T[]): T[] {
  if (incoming.length === 0) return current;
  const seen = new Set(current.map((m) => m.sequence));
  const fresh = incoming.filter((m) => !seen.has(m.sequence));
  return fresh.length === 0 ? current : [...current, ...fresh].sort((a, b) => a.sequence - b.sequence);
}

export function remoteStatusTone(status: RemoteSessionStatus): Tone {
  switch (status) {
    case "Active":
      return "success";
    case "Pending":
      return "warning";
    case "Denied":
    case "Expired":
      return "danger";
    default:
      return "neutral";
  }
}

export function remoteModeLabel(mode: RemoteAccessMode | undefined): string {
  switch (mode) {
    case "ConsentRequired":
      return "Enabled: the person at the computer must approve each session";
    case "Unattended":
      return "Enabled: unattended sessions allowed";
    default:
      return "Not enabled on this computer";
  }
}

/** Keeps the terminal text bounded so a chatty session cannot exhaust browser memory. */
export function appendTerminal(current: string, addition: string, max = 200_000): string {
  const next = current + addition;
  return next.length > max ? next.slice(next.length - max) : next;
}
