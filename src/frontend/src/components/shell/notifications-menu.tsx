"use client";

import { Bell, Trash2 } from "lucide-react";
import { Menu, MenuPanel, MenuTrigger } from "@/components/ui/menu";
import { useNotifications } from "@/components/providers/notifications-provider";
import { cn } from "@/lib/utils";

const toneDot: Record<string, string> = {
  info: "bg-muted-foreground",
  success: "bg-success",
  warning: "bg-warning",
  error: "bg-destructive",
};

export function NotificationsMenu() {
  const { notifications, unreadCount, markAllRead, dismiss, clear } = useNotifications();

  return (
    <Menu>
      <MenuTrigger
        className="relative h-9 w-9 justify-center text-muted-foreground hover:bg-muted hover:text-foreground"
        aria-label={unreadCount > 0 ? `Notifications, ${unreadCount} unread` : "Notifications"}
        title="Notifications"
        onClick={() => {
          if (unreadCount > 0) markAllRead();
        }}
      >
        <Bell className="h-[18px] w-[18px]" />
        {unreadCount > 0 ? (
          <span className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-primary px-1 text-[10px] font-semibold text-primary-foreground">
            {unreadCount > 9 ? "9+" : unreadCount}
          </span>
        ) : null}
      </MenuTrigger>
      <MenuPanel label="Notifications" className="w-80 p-0">
        <div className="flex items-center justify-between border-b border-border px-3 py-2">
          <p className="text-sm font-medium">Notifications</p>
          {notifications.length > 0 ? (
            <button type="button" onClick={clear} className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground">
              <Trash2 className="h-3.5 w-3.5" /> Clear
            </button>
          ) : null}
        </div>
        <ul className="max-h-80 overflow-y-auto">
          {notifications.length === 0 ? (
            <li className="px-3 py-8 text-center text-sm text-muted-foreground">You&apos;re all caught up.</li>
          ) : (
            notifications.map((n) => (
              <li key={n.id} className="flex gap-3 border-b border-border px-3 py-2.5 last:border-0">
                <span className={cn("mt-1.5 h-2 w-2 shrink-0 rounded-full", toneDot[n.tone])} aria-hidden />
                <div className="min-w-0 flex-1">
                  <p className={cn("text-sm", !n.read && "font-medium")}>{n.title}</p>
                  {n.description ? <p className="truncate text-xs text-muted-foreground">{n.description}</p> : null}
                  <p className="mt-0.5 text-[11px] text-muted-foreground">{new Date(n.createdAt).toLocaleTimeString()}</p>
                </div>
                <button type="button" onClick={() => dismiss(n.id)} className="text-xs text-muted-foreground hover:text-foreground" aria-label="Dismiss notification">
                  ×
                </button>
              </li>
            ))
          )}
        </ul>
      </MenuPanel>
    </Menu>
  );
}
