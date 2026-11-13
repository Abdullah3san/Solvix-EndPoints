"use client";

import * as React from "react";
import { HttpTransportType, HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import type { MonitoringUpdateDto } from "@/lib/api/types";
import { REALTIME_HUB_URL } from "@/lib/monitoring";
import { useNotifications } from "./notifications-provider";
import { useToast } from "./toast-provider";

export type RealtimeStatus = "connecting" | "live" | "reconnecting" | "offline";

type Listener = (update: MonitoringUpdateDto) => void;

interface RealtimeContextValue {
  status: RealtimeStatus;
  subscribe: (listener: Listener) => () => void;
}

const RealtimeContext = React.createContext<RealtimeContextValue | null>(null);

/**
 * One SignalR connection per browser tab, through the BFF (/api/realtime), so
 * the session token never reaches JavaScript. Server-Sent Events are used
 * (route handlers cannot proxy WebSockets) with long polling as fallback.
 * Offline alerts and recovery notifications become in-app notifications on
 * every page.
 */
export function RealtimeProvider({ children }: { children: React.ReactNode }) {
  const [status, setStatus] = React.useState<RealtimeStatus>("connecting");
  const listeners = React.useRef(new Set<Listener>());
  const { notify } = useNotifications();
  const { toast } = useToast();
  const notifyRef = React.useRef({ notify, toast });
  React.useEffect(() => {
    notifyRef.current = { notify, toast };
  }, [notify, toast]);

  React.useEffect(() => {
    let disposed = false;
    let retry: number | undefined;
    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(REALTIME_HUB_URL, { transport: HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling, withCredentials: true })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.None)
      .build();

    connection.on("monitoringUpdate", (update: MonitoringUpdateDto) => {
      for (const alert of update.alerts) {
        if (alert.status === "Open") {
          notifyRef.current.notify({ tone: "warning", title: `${alert.deviceName} is offline`, description: alert.message });
          notifyRef.current.toast({ tone: "error", title: `${alert.deviceName} is offline`, description: alert.message });
        } else {
          const description = `Back online${alert.downtimeSeconds ? ` after ${Math.round(alert.downtimeSeconds / 60)} min` : ""}.`;
          notifyRef.current.notify({ tone: "success", title: `${alert.deviceName} recovered`, description });
          notifyRef.current.toast({ tone: "success", title: `${alert.deviceName} recovered`, description });
        }
      }
      for (const listener of listeners.current) listener(update);
    });
    connection.onreconnecting(() => setStatus("reconnecting"));
    connection.onreconnected(() => setStatus("live"));
    connection.onclose(() => {
      if (disposed) return;
      // Closed for good (e.g. session token expired): start over with the current cookie.
      setStatus("offline");
      retry = window.setTimeout(start, 15000);
    });

    async function start() {
      if (disposed || connection.state !== HubConnectionState.Disconnected) return;
      setStatus("connecting");
      try {
        await connection.start();
        if (!disposed) setStatus("live");
      } catch {
        if (disposed) return;
        setStatus("offline");
        retry = window.setTimeout(start, 15000);
      }
    }

    void start();
    return () => {
      disposed = true;
      window.clearTimeout(retry);
      void connection.stop();
    };
  }, []);

  const value = React.useMemo<RealtimeContextValue>(
    () => ({
      status,
      subscribe: (listener) => {
        listeners.current.add(listener);
        return () => listeners.current.delete(listener);
      },
    }),
    [status],
  );

  return <RealtimeContext.Provider value={value}>{children}</RealtimeContext.Provider>;
}

export function useRealtime(): RealtimeContextValue {
  const ctx = React.useContext(RealtimeContext);
  if (!ctx) throw new Error("useRealtime must be used inside <RealtimeProvider>");
  return ctx;
}

/** Subscribes to live monitoring updates for the lifetime of the component. */
export function useMonitoringUpdates(listener: Listener) {
  const { subscribe } = useRealtime();
  const ref = React.useRef(listener);
  React.useEffect(() => {
    ref.current = listener;
  }, [listener]);
  React.useEffect(() => subscribe((u) => ref.current(u)), [subscribe]);
}

export function LiveIndicator({ className }: { className?: string }) {
  const { status } = useRealtime();
  const label = status === "live" ? "Live" : status === "offline" ? "Live updates unavailable" : status === "reconnecting" ? "Reconnecting…" : "Connecting…";
  const color = status === "live" ? "bg-success" : status === "offline" ? "bg-destructive" : "bg-warning";
  return (
    <span className={`inline-flex items-center gap-1.5 text-xs text-muted-foreground ${className ?? ""}`} role="status" aria-live="polite">
      <span className={`h-2 w-2 rounded-full ${color} ${status === "live" ? "animate-pulse" : ""}`} aria-hidden />
      {label}
    </span>
  );
}
