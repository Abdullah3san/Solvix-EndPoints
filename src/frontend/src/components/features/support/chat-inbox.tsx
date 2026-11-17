"use client";

import * as React from "react";
import Link from "next/link";
import { api } from "@/lib/api/client";
import type { ChatConversationDto } from "@/lib/api/types";
import { formatRelative } from "@/lib/devices";
import { Card } from "@/components/ui/card";
import { ChatPanel } from "@/components/features/support/chat-panel";

/** Conversations with people at their laptops, most recent first; the selected one shows live. */
export function ChatInbox({ initial, selected }: { initial: ChatConversationDto[]; selected: string | null }) {
  const [conversations, setConversations] = React.useState(initial);

  React.useEffect(() => {
    const timer = setInterval(() => {
      api.get<ChatConversationDto[]>("chat/conversations").then(setConversations, () => undefined);
    }, 5000);
    return () => clearInterval(timer);
  }, []);

  return (
    <div className="grid gap-4 lg:grid-cols-3">
      <Card className="self-start lg:col-span-1">
        {conversations.length === 0 ? (
          <p className="p-4 text-sm text-muted-foreground">No conversations yet. Open a device and use its Chat tab to start one.</p>
        ) : (
          <ul className="divide-y divide-border">
            {conversations.map((c) => (
              <li key={c.deviceId}>
                <Link
                  href={`/helpdesk/chat?device=${c.deviceId}`}
                  aria-current={selected === c.deviceId ? "true" : undefined}
                  className={`block px-4 py-3 text-sm hover:bg-muted ${selected === c.deviceId ? "bg-muted" : ""}`}
                >
                  <div className="flex justify-between gap-2">
                    <span className="font-medium">{c.deviceName}</span>
                    <span className="text-xs text-muted-foreground">{formatRelative(c.lastMessageAtUtc)}</span>
                  </div>
                  <p className="truncate text-muted-foreground">
                    {c.lastSender === "User" ? "" : "You: "}
                    {c.lastMessage}
                  </p>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </Card>
      <div className="lg:col-span-2">
        {selected ? <ChatPanel key={selected} deviceId={selected} /> : <p className="text-sm text-muted-foreground">Select a conversation.</p>}
      </div>
    </div>
  );
}
