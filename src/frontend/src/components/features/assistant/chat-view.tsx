"use client";

import * as React from "react";
import { Bot, FileText, Loader2, Send, Ticket, Wrench, User } from "lucide-react";
import Link from "next/link";
import { api } from "@/lib/api/client";
import type {
  AssistantStatusDto,
  ChatReplyDto,
  ConversationDetailDto,
  ConversationDto,
  MessageDto,
  PagedResult,
  ProposalDto,
} from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/input";
import { Markdown } from "./markdown";
import { ProposalCard } from "./proposal-card";

export const EXAMPLE_PROMPTS = [
  "Show me all offline computers.",
  "Find computers with less than 10 GB free storage.",
  "Which devices have outdated software?",
  "Why is POS-018 experiencing high CPU usage?",
  "Show all computers in the Beirut branch.",
  "Prepare a software deployment for the Finance department.",
];

function ProposalArtifact({ id }: { id: string }) {
  const [proposal, setProposal] = React.useState<ProposalDto | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  React.useEffect(() => {
    let live = true;
    api
      .get<ProposalDto>(`assistant/proposals/${id}`)
      .then((p) => live && setProposal(p))
      .catch((e) => live && setError(messageFrom(e)));
    return () => {
      live = false;
    };
  }, [id]);
  if (error) return <Alert tone="error">{error}</Alert>;
  if (!proposal) return <p className="text-xs text-muted-foreground">Loading proposal…</p>;
  return <ProposalCard proposal={proposal} />;
}

export function MessageBubble({ message }: { message: MessageDto }) {
  const isUser = message.role === "User";
  return (
    <div className={`flex gap-3 ${isUser ? "flex-row-reverse" : ""}`}>
      <div className={`mt-1 flex h-8 w-8 shrink-0 items-center justify-center rounded-full ${isUser ? "bg-primary text-primary-foreground" : "bg-secondary text-secondary-foreground"}`}>
        {isUser ? <User className="h-4 w-4" aria-hidden /> : <Bot className="h-4 w-4" aria-hidden />}
      </div>
      <div className={`max-w-[85%] space-y-2 rounded-lg border border-border p-3 ${isUser ? "bg-primary/5" : "bg-card"}`}>
        {isUser ? <p className="whitespace-pre-wrap text-sm">{message.content}</p> : <Markdown text={message.content} />}

        {message.artifacts.filter((a) => a.type === "proposal").map((a) => (
          <ProposalArtifact key={a.id} id={a.id} />
        ))}

        {message.artifacts.filter((a) => a.type !== "proposal").map((a) => (
          <Link
            key={a.id}
            href={a.type === "ticket" ? "/assistant/tickets" : "/assistant/reports"}
            className="inline-flex items-center gap-1 rounded-md border border-border px-2 py-1 text-xs hover:bg-muted"
          >
            {a.type === "ticket" ? <Ticket className="h-3.5 w-3.5" /> : <FileText className="h-3.5 w-3.5" />} {a.label}
          </Link>
        ))}

        {message.sources.length > 0 ? (
          <div className="border-t border-border pt-2">
            <p className="mb-1 text-xs font-medium text-muted-foreground">Sources</p>
            <ul className="space-y-0.5 text-xs">
              {message.sources.map((s) => (
                <li key={s.id} className={s.cited ? "" : "text-muted-foreground"}>
                  <span className="mr-1 rounded bg-secondary px-1 font-semibold text-secondary-foreground">{s.id}</span>
                  {s.documentTitle}
                  {s.section ? ` › ${s.section}` : ""}
                  {s.page ? ` (p. ${s.page})` : ""}
                </li>
              ))}
            </ul>
          </div>
        ) : null}

        {message.toolCalls.length > 0 ? (
          <details className="text-xs text-muted-foreground">
            <summary className="flex cursor-pointer items-center gap-1">
              <Wrench className="h-3 w-3" /> {message.toolCalls.length} tool call{message.toolCalls.length === 1 ? "" : "s"}
            </summary>
            <ul className="mt-1 space-y-0.5">
              {message.toolCalls.map((c, i) => (
                <li key={i}>
                  <code>{c.name}</code>{" "}
                  <Badge tone={c.status === "ok" ? "success" : c.status === "denied" ? "danger" : "warning"}>{c.status}</Badge>
                </li>
              ))}
            </ul>
          </details>
        ) : null}
      </div>
    </div>
  );
}

export function ChatView({ status }: { status: AssistantStatusDto }) {
  const [conversations, setConversations] = React.useState<ConversationDto[]>([]);
  const [conversationId, setConversationId] = React.useState<string | null>(null);
  const [messages, setMessages] = React.useState<MessageDto[]>([]);
  const [input, setInput] = React.useState("");
  const [sending, setSending] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const endRef = React.useRef<HTMLDivElement>(null);
  const localId = React.useRef(0);

  const loadConversations = React.useCallback(async () => {
    try {
      const page = await api.get<PagedResult<ConversationDto>>("assistant/conversations", { pageSize: 30 });
      setConversations(page.items);
    } catch {
      /* non-critical */
    }
  }, []);

  React.useEffect(() => {
    let live = true;
    api
      .get<PagedResult<ConversationDto>>("assistant/conversations", { pageSize: 30 })
      .then((page) => live && setConversations(page.items))
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, []);

  React.useEffect(() => {
    endRef.current?.scrollIntoView?.({ behavior: "smooth" });
  }, [messages, sending]);

  async function open(id: string) {
    setError(null);
    try {
      const detail = await api.get<ConversationDetailDto>(`assistant/conversations/${id}`);
      setConversationId(detail.id);
      setMessages(detail.messages);
    } catch (e) {
      setError(messageFrom(e));
    }
  }

  async function send(text: string) {
    const content = text.trim();
    if (!content || sending) return;
    setError(null);
    setSending(true);
    setInput("");
    localId.current += 1;
    const optimistic: MessageDto = { id: `local-${localId.current}`, role: "User", content, sources: [], toolCalls: [], artifacts: [], createdAtUtc: "" };
    setMessages((m) => [...m, optimistic]);
    try {
      const reply = await api.post<ChatReplyDto>("assistant/chat", { conversationId, message: content });
      setConversationId(reply.conversationId);
      setMessages((m) => [...m, reply.message]);
      void loadConversations();
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setSending(false);
    }
  }

  function newChat() {
    setConversationId(null);
    setMessages([]);
    setError(null);
  }

  const unavailable = !status.modelConfigured;

  return (
    <div className="grid gap-4 lg:grid-cols-[16rem_1fr]">
      <aside className="space-y-2">
        <Button variant="outline" size="sm" className="w-full" onClick={newChat}>
          New conversation
        </Button>
        <ul className="space-y-1" aria-label="Conversations">
          {conversations.map((c) => (
            <li key={c.id}>
              <button
                type="button"
                onClick={() => void open(c.id)}
                className={`w-full truncate rounded-md px-2 py-1.5 text-left text-sm hover:bg-muted ${c.id === conversationId ? "bg-muted font-medium" : ""}`}
              >
                {c.title}
              </button>
            </li>
          ))}
        </ul>
      </aside>

      <section className="flex min-h-[28rem] flex-col rounded-lg border border-border bg-card">
        <div className="flex-1 space-y-4 overflow-y-auto p-4">
          {unavailable ? (
            <Alert tone="info" title="AI model not configured">
              An administrator must set <code>Solvix__Assistant__Llm__ApiKey</code> on the API.
            </Alert>
          ) : null}
          {!status.fleetConnected ? (
            <Alert tone="info" title="No device data source connected">
              Fleet questions will explain this limitation. Knowledge-base search and tickets still work.
            </Alert>
          ) : null}
          {status.demoData ? (
            <Alert tone="info" title="Demo fleet">
              Device data is synthetic and actions are simulated; no real device is touched.
            </Alert>
          ) : null}

          {messages.length === 0 ? (
            <div className="space-y-3 py-6 text-center">
              <Bot className="mx-auto h-8 w-8 text-muted-foreground" aria-hidden />
              <p className="text-sm text-muted-foreground">Ask about your fleet, software, security or troubleshooting.</p>
              <div className="flex flex-wrap justify-center gap-2">
                {EXAMPLE_PROMPTS.map((p) => (
                  <button key={p} type="button" disabled={unavailable} onClick={() => void send(p)} className="rounded-full border border-border px-3 py-1 text-xs hover:bg-muted disabled:opacity-50">
                    {p}
                  </button>
                ))}
              </div>
            </div>
          ) : (
            messages.map((m) => <MessageBubble key={m.id} message={m} />)
          )}

          {sending ? (
            <p className="flex items-center gap-2 text-sm text-muted-foreground" role="status">
              <Loader2 className="h-4 w-4 animate-spin" aria-hidden /> Working on it…
            </p>
          ) : null}
          {error ? <Alert tone="error">{error}</Alert> : null}
          <div ref={endRef} />
        </div>

        <form
          className="flex items-end gap-2 border-t border-border p-3"
          onSubmit={(e) => {
            e.preventDefault();
            void send(input);
          }}
        >
          <Textarea
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                void send(input);
              }
            }}
            placeholder="Ask the assistant…  (Enter to send, Shift+Enter for a new line)"
            aria-label="Message"
            maxLength={4000}
            className="min-h-10 flex-1 resize-none"
            rows={2}
            disabled={unavailable}
          />
          <Button type="submit" loading={sending} disabled={unavailable || input.trim().length === 0}>
            <Send className="h-4 w-4" aria-hidden /> Send
          </Button>
        </form>
        <p className="px-4 pb-2 text-xs text-muted-foreground">
          The assistant can only read what you can read and can only <em>propose</em> actions. Never paste passwords or keys.
        </p>
      </section>
    </div>
  );
}
