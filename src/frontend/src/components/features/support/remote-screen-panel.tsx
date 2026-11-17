"use client";

import * as React from "react";
import { Monitor, Square } from "lucide-react";
import { api, PROXY_PREFIX } from "@/lib/api/client";
import type { RemoteAccessMode } from "@/lib/api/types";
import { hasRemoteFeature, pointerFraction, remoteStatusTone, toKeyEvent } from "@/lib/support";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useRemoteSession } from "@/components/features/support/use-remote-session";

/**
 * Watch (and, when the person at the computer allowed it, control) the screen of a device. Only a
 * session the computer's own policy allows can start; the person may be asked first.
 */
export function RemoteScreenPanel({ deviceId, mode, features, retired }: { deviceId: string; mode: RemoteAccessMode | undefined; features: string | undefined; retired: boolean }) {
  const { session, open, error, setError, busy, start, end } = useRemoteSession(deviceId, "Screen");
  const [reason, setReason] = React.useState("");
  const [control, setControl] = React.useState(false);
  const [frameUrl, setFrameUrl] = React.useState<string | null>(null);
  const image = React.useRef<HTMLImageElement>(null);
  const queue = React.useRef<Promise<unknown>>(Promise.resolve());
  const pendingMove = React.useRef<{ x: number; y: number } | null>(null);
  const sessionId = session?.id ?? null;
  const active = session?.status === "Active";
  const controlGranted = active && session?.controlGranted === true;
  const enabled = mode !== undefined && mode !== "Disabled" && hasRemoteFeature(features, "Screen");
  const canControl = hasRemoteFeature(features, "Control");

  // Pull frames while the session is open. Only the newest frame matters, so slow viewers skip ahead.
  React.useEffect(() => {
    if (!sessionId || !open) return;
    let cancelled = false;
    let previous: string | null = null;
    (async () => {
      let after = 0;
      while (!cancelled) {
        try {
          const response = await fetch(`${PROXY_PREFIX}/remote-sessions/${sessionId}/screen/frame?after=${after}&wait=15`, { cache: "no-store" });
          if (cancelled) return;
          if (response.status === 200) {
            after = Number(response.headers.get("x-frame-sequence") ?? after);
            const url = URL.createObjectURL(await response.blob());
            setFrameUrl(url);
            if (previous) URL.revokeObjectURL(previous);
            previous = url;
          } else if (response.status !== 204) {
            setError(`The screen could not be read (${response.status}).`);
            await new Promise((r) => setTimeout(r, 3000));
          } else {
            const state = response.headers.get("x-session-status");
            if (state && state !== "Pending" && state !== "Active") return;
          }
        } catch {
          if (!cancelled) await new Promise((r) => setTimeout(r, 2000));
        }
      }
    })();
    return () => {
      cancelled = true;
      if (previous) URL.revokeObjectURL(previous);
    };
  }, [sessionId, open, setError]);

  // Input is sent in order, one request at a time; a burst of mouse moves collapses to the latest position.
  const send = React.useCallback(
    (event: Record<string, unknown>) => {
      if (!sessionId || !controlGranted) return;
      queue.current = queue.current.then(() => api.post(`remote-sessions/${sessionId}/input`, { text: JSON.stringify(event) }).catch(() => undefined));
    },
    [sessionId, controlGranted],
  );

  const point = (e: React.MouseEvent) => (image.current ? pointerFraction(e.clientX, e.clientY, image.current.getBoundingClientRect()) : { x: 0, y: 0 });
  const button = (e: React.MouseEvent) => (e.button === 2 ? "right" : e.button === 1 ? "middle" : "left");

  function onMove(e: React.MouseEvent) {
    if (!controlGranted) return;
    const first = pendingMove.current === null;
    pendingMove.current = point(e);
    if (first) {
      setTimeout(() => {
        const p = pendingMove.current;
        pendingMove.current = null;
        if (p) send({ type: "move", ...p });
      }, 60);
    }
  }

  function onKey(e: React.KeyboardEvent, pressed: boolean) {
    if (!controlGranted) return;
    const event = toKeyEvent(e, pressed);
    if (!event) return;
    e.preventDefault();
    send(event);
  }

  return (
    <div className="space-y-4">
      <Alert tone={enabled ? "info" : "error"}>
        <strong>Screen sharing:</strong>{" "}
        {enabled
          ? `Enabled on this computer${canControl ? " (view and control)" : " (view only)"}. The person at the computer may be asked to approve; every session is recorded.`
          : "Not enabled on this computer. It is switched on locally, in the agent's installation, and requires someone signed in with the screen helper running."}
      </Alert>
      {error ? <Alert tone="error">{error}</Alert> : null}

      {!open ? (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            setFrameUrl(null);
            void start(reason, control);
          }}
          className="flex flex-wrap items-end gap-3"
        >
          <div className="min-w-64 flex-1">
            <label htmlFor="screen-reason" className="mb-1.5 block text-sm font-medium">
              Reason (shown to the person at the computer)
            </label>
            <Input id="screen-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} placeholder="e.g. Looking at the printer error you reported" />
          </div>
          <label className="flex items-center gap-2 pb-2 text-sm">
            <input type="checkbox" checked={control} disabled={!canControl} onChange={(e) => setControl(e.target.checked)} /> Ask to control (not only watch)
          </label>
          <Button type="submit" loading={busy} disabled={!enabled || retired}>
            <Monitor className="h-4 w-4" /> Start screen session
          </Button>
        </form>
      ) : (
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2 text-sm">
            <Badge tone={remoteStatusTone(session!.status)}>{session!.status === "Pending" ? "Waiting for the computer" : "Connected"}</Badge>
            {active ? <Badge tone={controlGranted ? "warning" : "neutral"}>{controlGranted ? "You can control this computer" : "View only"}</Badge> : null}
            {session!.status === "Pending" ? <span className="text-muted-foreground">The person at the computer may need to approve. Requests expire after 2 minutes.</span> : null}
          </div>
          <Button variant="destructive" onClick={end}>
            <Square className="h-4 w-4" /> End session
          </Button>
        </div>
      )}

      {session && !open ? (
        <p className="text-sm text-muted-foreground">
          Session {session.status.toLowerCase()}
          {session.endReason ? `: ${session.endReason}` : ""}.
        </p>
      ) : null}

      {open ? (
        <div
          tabIndex={0}
          role="application"
          aria-label="Remote screen"
          className="overflow-hidden rounded-lg border border-border bg-black outline-none focus:ring-2 focus:ring-primary"
          onKeyDown={(e) => onKey(e, true)}
          onKeyUp={(e) => onKey(e, false)}
        >
          {frameUrl ? (
            // eslint-disable-next-line @next/next/no-img-element -- live frames are blob URLs, not optimisable assets
            <img
              ref={image}
              src={frameUrl}
              alt="The shared screen"
              draggable={false}
              className={`mx-auto max-h-[70vh] w-auto select-none ${controlGranted ? "cursor-crosshair" : ""}`}
              onMouseMove={onMove}
              onMouseDown={(e) => send({ type: "down", ...point(e), button: button(e) })}
              onMouseUp={(e) => send({ type: "up", ...point(e), button: button(e) })}
              onContextMenu={(e) => controlGranted && e.preventDefault()}
              onWheel={(e) => controlGranted && send({ type: "wheel", ...point(e), delta: e.deltaY < 0 ? 1 : -1 })}
            />
          ) : (
            <div className="flex h-64 items-center justify-center text-sm text-white/70">
              {session?.status === "Pending" ? "Waiting for the person at the computer…" : "Waiting for the first picture…"}
            </div>
          )}
        </div>
      ) : null}
      {controlGranted ? <p className="text-xs text-muted-foreground">Click the picture to focus it, then type: keys and mouse go to the computer.</p> : null}
    </div>
  );
}
