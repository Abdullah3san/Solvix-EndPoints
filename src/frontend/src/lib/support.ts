import type { TicketPriority, TicketStatus } from "@/lib/api/types";

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
