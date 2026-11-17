import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { ChatConversationDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { ChatInbox } from "@/components/features/support/chat-inbox";

export const metadata: Metadata = { title: "Chat" };

export default async function ChatPage({ searchParams }: { searchParams: Promise<{ device?: string }> }) {
  const { device } = await searchParams;
  const selected = device && /^[0-9a-f-]{36}$/i.test(device) ? device : null;
  const conversations = await serverApi<ChatConversationDto[]>("chat/conversations");
  return (
    <>
      <PageHeader title="Chat" description="Talk to the person using a computer. They reply from the Solvix agent on their laptop." />
      <ChatInbox initial={conversations} selected={selected} />
    </>
  );
}
