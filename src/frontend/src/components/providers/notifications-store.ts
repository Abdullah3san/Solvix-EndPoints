import type { AppNotification } from "./notifications-provider";

/**
 * Tiny external store so React can subscribe with useSyncExternalStore:
 * the server snapshot is always empty, the client snapshot is hydrated from
 * sessionStorage on first read, and every change is persisted.
 */
const STORAGE_KEY = "solvix.notifications";
const MAX_ITEMS = 50;
const EMPTY: AppNotification[] = [];

let items: AppNotification[] | null = null;
const listeners = new Set<() => void>();

function load(): AppNotification[] {
  if (items !== null) return items;
  try {
    const raw = typeof sessionStorage !== "undefined" ? sessionStorage.getItem(STORAGE_KEY) : null;
    items = raw ? (JSON.parse(raw) as AppNotification[]) : [];
  } catch {
    items = [];
  }
  return items;
}

function commit(next: AppNotification[]) {
  items = next;
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(next));
  } catch {
    // storage unavailable (private mode, quota): keep in memory only
  }
  for (const listener of listeners) listener();
}

export const notificationsStore = {
  subscribe(listener: () => void) {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
  getSnapshot: (): AppNotification[] => load(),
  getServerSnapshot: (): AppNotification[] => EMPTY,
  add(notification: AppNotification) {
    commit([notification, ...load()].slice(0, MAX_ITEMS));
  },
  markAllRead() {
    commit(load().map((n) => (n.read ? n : { ...n, read: true })));
  },
  dismiss(id: string) {
    commit(load().filter((n) => n.id !== id));
  },
  clear() {
    commit([]);
  },
  /** Test helper. */
  reset() {
    items = null;
    try {
      sessionStorage.removeItem(STORAGE_KEY);
    } catch {
      // ignore
    }
  },
};
