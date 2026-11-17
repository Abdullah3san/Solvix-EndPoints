import type { FileOperationStatus, RemoteAccessMode, RemoteSessionStatus, TicketPriority, TicketStatus } from "@/lib/api/types";

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

/** Features the computer reported (e.g. "Shell, Screen, Files"); an agent that predates the field only offers a shell. */
export function hasRemoteFeature(features: string | undefined, feature: "Shell" | "Screen" | "Files" | "Control"): boolean {
  const reported = features ?? "Shell";
  return reported.split(",").some((f) => f.trim() === feature);
}

/** Parent folder of a Windows or POSIX path; null when there is none. */
export function parentPath(path: string): string | null {
  const trimmed = path.replace(/[\\/]+$/, "");
  const index = Math.max(trimmed.lastIndexOf("\\"), trimmed.lastIndexOf("/"));
  if (index <= 0) return null;
  const parent = trimmed.slice(0, index);
  return /^[A-Za-z]:$/.test(parent) ? `${parent}\\` : parent;
}

export function joinPath(folder: string, name: string): string {
  const separator = folder.includes("\\") ? "\\" : "/";
  return folder.endsWith("\\") || folder.endsWith("/") ? `${folder}${name}` : `${folder}${separator}${name}`;
}

export function fileStatusTone(status: FileOperationStatus): Tone {
  return status === "Completed" ? "success" : status === "Failed" ? "danger" : "warning";
}

/** Browser key events as the agent's input events (fractions of the shared screen, never pixels). */
export function toKeyEvent(event: { key: string; ctrlKey: boolean; altKey: boolean; metaKey: boolean }, pressed: boolean):
  | { type: "text"; characters: string }
  | { type: "key"; name: string; pressed: boolean }
  | null {
  const printable = event.key.length === 1 && !event.ctrlKey && !event.altKey && !event.metaKey;
  if (printable) return pressed ? { type: "text", characters: event.key } : null; // typed once, on key down
  if (event.key === "Unidentified" || event.key === "Dead") return null;
  return { type: "key", name: event.key, pressed };
}

export function pointerFraction(clientX: number, clientY: number, rect: { left: number; top: number; width: number; height: number }): { x: number; y: number } {
  const clamp = (v: number) => Math.min(1, Math.max(0, v));
  return { x: clamp((clientX - rect.left) / (rect.width || 1)), y: clamp((clientY - rect.top) / (rect.height || 1)) };
}
