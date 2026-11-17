"use client";

import * as React from "react";
import { Send } from "lucide-react";
import { api } from "@/lib/api/client";
import type { ChatMessageDto } from "@/lib/api/types";
import { lastSequence, mergeMessages } from "@/lib/support";
import { messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { formatRelative } from "@/lib/devices";

const POLL_MS = 3000;

/** Live conversation with the person using a device. Polls while mounted; key it by device to switch conversations. */
export function ChatPanel({ deviceId, disabled = false }: { deviceId: string; disabled?: boolean }) {
  const [messages, setMessages] = React.useState<ChatMessageDto[]>([]);
  const [draft, setDraft] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [sending, setSending] = React.useState(false);
  const last = React.useRef(0);
  const bottom = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    last.current = 0;
    let cancelled = false;

    async function poll() {
      try {
        const incoming = await api.get<ChatMessageDto[]>(`devices/${deviceId}/chat`, { after: last.current });
        if (cancelled) return;
        setError(null);
        setMessages((current) => {
          const merged = mergeMessages(current, incoming);
          last.current = lastSequence(merged);
          return merged;
        });
      } catch (e) {
        if (!cancelled) setError(messageFrom(e));
      }
    }

    void poll();
    const timer = setInterval(() => void poll(), POLL_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [deviceId]);

  React.useEffect(() => {
    bottom.current?.scrollIntoView?.({ block: "end" });
  }, [messages.length]);

  async function send(event: React.FormEvent) {
    event.preventDefault();
    const body = draft.trim();
    if (!body) return;
    setSending(true);
    try {
      const sent = await api.post<ChatMessageDto>(`devices/${deviceId}/chat`, { body });
      setMessages((current) => {
        const merged = mergeMessages(current, [sent]);
        last.current = lastSequence(merged);
        return merged;
      });
      setDraft("");
      setError(null);
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setSending(false);
    }
  }

  return (
    <div className="flex h-[28rem] flex-col rounded-lg border border-border bg-card">
      <div className="flex-1 space-y-3 overflow-y-auto p-4" aria-live="polite" aria-label="Chat messages">
        {messages.length === 0 ? <p className="text-sm text-muted-foreground">No messages yet. Say hello to the person using this computer.</p> : null}
        {messages.map((m) => (
          <div key={m.sequence} className={`flex ${m.sender === "Technician" ? "justify-end" : "justify-start"}`}>
            <div className={`max-w-[80%] rounded-lg px-3 py-2 text-sm ${m.sender === "Technician" ? "bg-primary text-primary-foreground" : "bg-muted"}`}>
              <div className="mb-0.5 text-xs opacity-80">
                {m.senderName ?? (m.sender === "Technician" ? "Technician" : "User")} · {formatRelative(m.sentAtUtc)}
              </div>
              <p className="whitespace-pre-wrap break-words">{m.body}</p>
            </div>
          </div>
        ))}
        <div ref={bottom} />
      </div>
      {error ? <Alert tone="error">{error}</Alert> : null}
      <form onSubmit={send} className="flex gap-2 border-t border-border p-3">
        <Input
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          placeholder={disabled ? "This device is retired" : "Type a message"}
          maxLength={4000}
          disabled={disabled}
          aria-label="Message"
        />
        <Button type="submit" loading={sending} disabled={disabled || !draft.trim()}>
          <Send className="h-4 w-4" /> Send
        </Button>
      </form>
    </div>
  );
}
