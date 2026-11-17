"use client";

import * as React from "react";
import { PlugZap, Square } from "lucide-react";
import { api } from "@/lib/api/client";
import type { PagedResult, RemoteAccessMode, RemoteOutputDto, RemoteSessionDto } from "@/lib/api/types";
import { appendTerminal, remoteModeLabel, remoteStatusTone } from "@/lib/support";
import { formatRelative } from "@/lib/devices";
import { messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";

/**
 * Remote command session for one device. The computer's own policy decides whether it can be used;
 * this panel only reports it. Sessions are audited and time-limited by the server.
 */
export function RemoteSessionPanel({ deviceId, mode, retired }: { deviceId: string; mode: RemoteAccessMode | undefined; retired: boolean }) {
  const [session, setSession] = React.useState<RemoteSessionDto | null>(null);
  const [history, setHistory] = React.useState<RemoteSessionDto[]>([]);
  const [terminal, setTerminal] = React.useState("");
  const [reason, setReason] = React.useState("");
  const [line, setLine] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const sessionId = session?.id ?? null;
  const open = session?.status === "Pending" || session?.status === "Active";
  const after = React.useRef(0);
  const screen = React.useRef<HTMLPreElement>(null);

  const loadHistory = React.useCallback(async () => {
    try {
      const page = await api.get<PagedResult<RemoteSessionDto>>("remote-sessions", { deviceId, pageSize: 20 });
      setHistory(page.items);
      const current = page.items.find((s) => s.status === "Pending" || s.status === "Active") ?? null;
      setSession((existing) => existing ?? current);
    } catch (e) {
      setError(messageFrom(e));
    }
  }, [deviceId]);

  React.useEffect(() => {
    let cancelled = false;
    api.get<PagedResult<RemoteSessionDto>>("remote-sessions", { deviceId, pageSize: 20 }).then(
      (page) => {
        if (cancelled) return;
        setHistory(page.items);
        const current = page.items.find((s) => s.status === "Pending" || s.status === "Active") ?? null;
        setSession((existing) => existing ?? current);
      },
      (e) => {
        if (!cancelled) setError(messageFrom(e));
      },
    );
    return () => {
      cancelled = true;
    };
  }, [deviceId]);

  // Long-poll the terminal output while a session is open.
  React.useEffect(() => {
    if (!sessionId || !open) return;
    let cancelled = false;
    (async () => {
      while (!cancelled) {
        try {
          const out = await api.get<RemoteOutputDto>(`remote-sessions/${sessionId}/output`, { after: after.current, wait: 15 });
          if (cancelled) return;
          if (out.chunks.length > 0) {
            after.current = out.chunks[out.chunks.length - 1].sequence;
            setTerminal((t) => appendTerminal(t, out.chunks.map((c) => c.text).join("")));
          }
          setSession((s) => (s && s.status !== out.status ? { ...s, status: out.status, endReason: out.endReason } : s));
          if (out.status !== "Pending" && out.status !== "Active") {
            void loadHistory();
            return;
          }
        } catch (e) {
          if (cancelled) return;
          setError(messageFrom(e));
          await new Promise((r) => setTimeout(r, 3000));
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [sessionId, open, loadHistory]);

  React.useEffect(() => {
    if (screen.current) screen.current.scrollTop = screen.current.scrollHeight;
  }, [terminal]);

  async function start(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      after.current = 0;
      setTerminal("");
      setSession(await api.post<RemoteSessionDto>(`devices/${deviceId}/remote-sessions`, { reason: reason.trim() || null }));
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setBusy(false);
    }
  }

  async function send(event: React.FormEvent) {
    event.preventDefault();
    if (!sessionId || session?.status !== "Active") return;
    const text = `${line}\n`;
    setLine("");
    try {
      await api.post(`remote-sessions/${sessionId}/input`, { text });
    } catch (e) {
      setError(messageFrom(e));
    }
  }

  async function end() {
    if (!sessionId) return;
    try {
      setSession(await api.post<RemoteSessionDto>(`remote-sessions/${sessionId}/end`));
      void loadHistory();
    } catch (e) {
      setError(messageFrom(e));
    }
  }

  const disabled = mode === undefined || mode === "Disabled";

  return (
    <div className="space-y-4">
      <Alert tone={disabled ? "error" : "info"}>
        <strong>Remote access:</strong> {remoteModeLabel(mode)}.{" "}
        {disabled ? "It is switched on locally, in the agent's configuration on the computer; the console cannot enable it." : "Every session is recorded with who asked, why, and who approved it."}
      </Alert>
      {error ? <Alert tone="error">{error}</Alert> : null}

      {!open ? (
        <form onSubmit={start} className="flex flex-wrap items-end gap-2">
          <div className="min-w-64 flex-1">
            <label htmlFor="remote-reason" className="mb-1.5 block text-sm font-medium">
              Reason (shown to the person at the computer)
            </label>
            <Input id="remote-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} placeholder="e.g. Fixing the printer you reported" />
          </div>
          <Button type="submit" loading={busy} disabled={disabled || retired}>
            <PlugZap className="h-4 w-4" /> Start remote session
          </Button>
        </form>
      ) : (
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2 text-sm">
            <Badge tone={remoteStatusTone(session!.status)}>{session!.status === "Pending" ? "Waiting for the computer" : "Connected"}</Badge>
            {session!.status === "Pending" ? <span className="text-muted-foreground">The person at the computer may need to approve. Requests expire after 2 minutes.</span> : null}
          </div>
          <Button variant="destructive" onClick={end}>
            <Square className="h-4 w-4" /> End session
          </Button>
        </div>
      )}

      {session ? (
        <div className="rounded-lg border border-border bg-black">
          <pre ref={screen} aria-label="Terminal output" className="h-80 overflow-y-auto whitespace-pre-wrap break-all p-3 font-mono text-xs text-green-300">
            {terminal || (session.status === "Pending" ? "Waiting for the computer to accept…\n" : "")}
            {!open ? `\n[Session ${session.status.toLowerCase()}${session.endReason ? `: ${session.endReason}` : ""}]\n` : ""}
          </pre>
          <form onSubmit={send} className="flex gap-2 border-t border-white/10 p-2">
            <span className="self-center font-mono text-xs text-green-300">&gt;</span>
            <input
              value={line}
              onChange={(e) => setLine(e.target.value)}
              disabled={session.status !== "Active"}
              aria-label="Command"
              autoComplete="off"
              spellCheck={false}
              maxLength={8000}
              className="flex-1 bg-transparent font-mono text-xs text-green-300 outline-none disabled:opacity-40"
              placeholder={session.status === "Active" ? "type a command and press Enter" : ""}
            />
          </form>
        </div>
      ) : null}

      <h3 className="text-sm font-medium text-muted-foreground">Session history</h3>
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Requested</TH>
              <TH>By</TH>
              <TH>Reason</TH>
              <TH>Result</TH>
              <TH>Approved by</TH>
            </TR>
          </THead>
          <TBody>
            {history.length === 0 ? (
              <EmptyRow colSpan={5}>No remote sessions on this device.</EmptyRow>
            ) : (
              history.map((h) => (
                <TR key={h.id}>
                  <TD>{formatRelative(h.createdAtUtc)}</TD>
                  <TD>{h.requestedByName}</TD>
                  <TD className="text-muted-foreground">{h.reason ?? "—"}</TD>
                  <TD>
                    <Badge tone={remoteStatusTone(h.status)}>{h.status}</Badge>
                    {h.endReason ? <span className="ml-2 text-xs text-muted-foreground">{h.endReason}</span> : null}
                  </TD>
                  <TD>{h.consent === "User" ? "Person at the computer" : h.consent === "Unattended" ? "Unattended policy" : "—"}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
    </div>
  );
}
