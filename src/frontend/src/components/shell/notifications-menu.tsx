"use client";

import { Bell, BellOff, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useNotifications } from "@/components/providers/notifications-provider";
import { formatRelative } from "@/lib/devices";
import { cn } from "@/lib/utils";

const toneDot: Record<string, string> = {
  info: "bg-info",
  success: "bg-success",
  warning: "bg-warning",
  error: "bg-destructive",
};

/** Notification centre: results of actions taken in this session. Opening it marks everything read. */
export function NotificationsMenu() {
  const { notifications, unreadCount, markAllRead, dismiss, clear } = useNotifications();

  return (
    <Popover
      onOpenChange={(open) => {
        if (open && unreadCount > 0) markAllRead();
      }}
    >
      <PopoverTrigger asChild>
        <Button
          variant="ghost"
          size="icon"
          className="relative text-muted-foreground"
          aria-label={unreadCount > 0 ? `Notifications, ${unreadCount} unread` : "Notifications"}
          title="Notifications"
        >
          <Bell className="size-[18px]" />
          {unreadCount > 0 ? (
            <span className="absolute right-1 top-1 flex h-4 min-w-4 items-center justify-center rounded-full bg-destructive px-1 text-[10px] font-semibold leading-none text-destructive-foreground ring-2 ring-background">
              {unreadCount > 9 ? "9+" : unreadCount}
            </span>
          ) : null}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-[22rem] max-w-[calc(100vw-2rem)] p-0">
        <div className="flex items-center justify-between border-b border-border px-4 py-2.5">
          <p className="text-sm font-semibold">Notifications</p>
          {notifications.length > 0 ? (
            <Button variant="ghost" size="sm" className="-mr-2 h-7 text-muted-foreground" onClick={clear}>
              Clear all
            </Button>
          ) : null}
        </div>
        {notifications.length === 0 ? (
          <EmptyState size="sm" icon={BellOff} title="You're all caught up" description="Results of actions you take in this session appear here." />
        ) : (
          <ul className="scrollbar-thin max-h-96 overflow-y-auto">
            {notifications.map((n) => (
              <li key={n.id} className="group flex gap-3 border-b border-border px-4 py-3 last:border-0">
                <span className={cn("mt-1.5 size-2 shrink-0 rounded-full", toneDot[n.tone])} aria-hidden />
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium leading-5">{n.title}</p>
                  {n.description ? <p className="truncate text-sm text-muted-foreground">{n.description}</p> : null}
                  <p className="mt-0.5 text-xs text-muted-foreground">{formatRelative(new Date(n.createdAt).toISOString())}</p>
                </div>
                <button
                  type="button"
                  onClick={() => dismiss(n.id)}
                  className="flex size-6 shrink-0 items-center justify-center rounded text-muted-foreground opacity-60 hover:bg-muted hover:text-foreground group-hover:opacity-100"
                  aria-label={`Dismiss notification: ${n.title}`}
                >
                  <X className="size-3.5" />
                </button>
              </li>
            ))}
          </ul>
        )}
      </PopoverContent>
    </Popover>
  );
}
