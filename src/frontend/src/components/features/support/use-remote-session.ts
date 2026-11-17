"use client";

import * as React from "react";
import { api } from "@/lib/api/client";
import type { PagedResult, RemoteSessionDto, RemoteSessionKind } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";

/** The device's open session of one kind (screen / files), with start and end. Polls its state while open. */
export function useRemoteSession(deviceId: string, kind: RemoteSessionKind) {
  const [session, setSession] = React.useState<RemoteSessionDto | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const id = session?.id ?? null;
  const open = session?.status === "Pending" || session?.status === "Active";

  React.useEffect(() => {
    let cancelled = false;
    api.get<PagedResult<RemoteSessionDto>>("remote-sessions", { deviceId, pageSize: 20 }).then(
      (page) => {
        if (cancelled) return;
        const current = page.items.find((s) => (s.kind ?? "Shell") === kind && (s.status === "Pending" || s.status === "Active")) ?? null;
        setSession((existing) => existing ?? current);
      },
      (e) => {
        if (!cancelled) setError(messageFrom(e));
      },
    );
    return () => {
      cancelled = true;
    };
  }, [deviceId, kind]);

  React.useEffect(() => {
    if (!id || !open) return;
    const timer = setInterval(() => {
      api.get<RemoteSessionDto>(`remote-sessions/${id}`).then(setSession, () => undefined);
    }, 2000);
    return () => clearInterval(timer);
  }, [id, open]);

  const start = React.useCallback(
    async (reason: string, control = false) => {
      setBusy(true);
      setError(null);
      try {
        setSession(await api.post<RemoteSessionDto>(`devices/${deviceId}/remote-sessions`, { reason: reason.trim() || null, kind, control }));
      } catch (e) {
        setError(messageFrom(e));
      } finally {
        setBusy(false);
      }
    },
    [deviceId, kind],
  );

  const end = React.useCallback(async () => {
    if (!id) return;
    try {
      setSession(await api.post<RemoteSessionDto>(`remote-sessions/${id}/end`));
    } catch (e) {
      setError(messageFrom(e));
    }
  }, [id]);

  return { session, open, error, setError, busy, start, end };
}
