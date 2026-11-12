"use client";

import * as React from "react";
import { notificationsStore } from "./notifications-store";

export type NotificationTone = "info" | "success" | "warning" | "error";

export interface AppNotification {
  id: string;
  title: string;
  description?: string;
  tone: NotificationTone;
  createdAt: number;
  read: boolean;
}

interface NotificationsContextValue {
  notifications: AppNotification[];
  unreadCount: number;
  notify: (input: { title: string; description?: string; tone?: NotificationTone }) => void;
  markAllRead: () => void;
  dismiss: (id: string) => void;
  clear: () => void;
}

const NotificationsContext = React.createContext<NotificationsContextValue | null>(null);

/**
 * In-app notification centre. Phase 01 has no server-side notification feed,
 * so entries are produced by UI actions (e.g. "Branch created") and kept per
 * browser tab in sessionStorage. The provider exposes the same API a
 * push-based feed will use later, so consumers do not change.
 */
export function NotificationsProvider({ children }: { children: React.ReactNode }) {
  const notifications = React.useSyncExternalStore(
    notificationsStore.subscribe,
    notificationsStore.getSnapshot,
    notificationsStore.getServerSnapshot,
  );

  const value = React.useMemo<NotificationsContextValue>(
    () => ({
      notifications,
      unreadCount: notifications.filter((n) => !n.read).length,
      notify: ({ title, description, tone = "info" }) =>
        notificationsStore.add({ id: crypto.randomUUID(), title, description, tone, createdAt: Date.now(), read: false }),
      markAllRead: notificationsStore.markAllRead,
      dismiss: notificationsStore.dismiss,
      clear: notificationsStore.clear,
    }),
    [notifications],
  );

  return <NotificationsContext.Provider value={value}>{children}</NotificationsContext.Provider>;
}

export function useNotifications(): NotificationsContextValue {
  const ctx = React.useContext(NotificationsContext);
  if (!ctx) throw new Error("useNotifications must be used inside <NotificationsProvider>");
  return ctx;
}
